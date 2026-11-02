using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Http;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.Publishing.Automation;

/// <summary>
/// Điều kiện tự dừng chiến dịch: hết ngân sách bài, mất quyền API, ngắt mạch, tuyến mạng/VPN không đạt, lỗi liên tiếp, và (nếu bật
/// <see cref="SettingKeys.HealthAutoPauseCampaigns"/>) kênh bất thường theo AnomalyDetector. Dừng rồi không tự chạy lại.
/// Hết quota API trong ngày KHÔNG dừng chiến dịch: job tự dời tới lúc quota reset.
/// </summary>
[Service]
public sealed class CampaignGuard(
    ICampaignRepository campaigns,
    IDestinationRepository destinations,
    IConnectionRepository connections,
    ICircuitBreaker breaker,
    IActionRequiredRepository actions,
    IAuditLog audit,
    IClock clock)
{
    /// <summary>Lý do phải dừng, hoặc null nếu được chạy tiếp.</summary>
    public async Task<string?> CheckAsync(CampaignPlan plan, CancellationToken ct)
    {
        var c = plan.Campaign;
        var counts = (await campaigns.CountsAsync(clock.UtcNow, ct)).GetValueOrDefault(c.Id);
        if (c.PostBudget is { } budget && (counts?.Total ?? 0) >= budget)
            return $"Đã dùng hết ngân sách {budget} bài của chiến dịch.";

        var destIds = plan.Rules.SelectMany(r => r.DestinationIds).ToHashSet();
        var connIds = (await destinations.ListAsync(null, true, ct)).Where(d => destIds.Contains(d.Id)).Select(d => d.ConnectionId).ToHashSet();
        var vpnDown = (await actions.ListOpenByKindAsync(ActionKinds.VpnDown, ct)).Select(a => a.RefId).ToHashSet();
        foreach (var conn in (await connections.ListAsync(ct)).Where(x => connIds.Contains(x.Id)))
        {
            if (conn.ApiStatus is ApiStatus.NeedsReauth or ApiStatus.Revoked)
                return $"Tài khoản \"{conn.DisplayName}\" mất quyền API ({conn.ApiStatus}): cần kết nối lại.";
            if (vpnDown.Contains(conn.Id))
                return $"Tuyến mạng/VPN của tài khoản \"{conn.DisplayName}\" không đạt: dừng để không đăng sai IP.";
            var decision = breaker.Peek(conn.Platform.ToString(), conn.Id);
            if (!decision.Allowed && decision.State == CircuitState.Open)
                return $"Tài khoản \"{conn.DisplayName}\" đang ngắt mạch do lỗi liên tiếp: {decision.Reason}";
        }
        var outcomes = await campaigns.RecentOutcomesAsync(c.Id, c.StatusChangedUtc, c.MaxConsecutiveFailures, ct);
        if (outcomes.Count >= c.MaxConsecutiveFailures && outcomes.All(s => s != PublicationStatus.Published))
            return $"{c.MaxConsecutiveFailures} bài liên tiếp không đăng được (lỗi/cần xử lý/không xác định).";
        return null;
    }

    public async Task PauseAsync(Campaign c, string reason, CancellationToken ct)
    {
        if (!await campaigns.SetStatusAsync(c.Id, CampaignStatus.Paused, reason, [CampaignStatus.Active], clock.UtcNow, ct)) return;
        await actions.RaiseAsync(ActionKinds.CampaignPaused, c.Id, $"Chiến dịch \"{c.Name}\" đã tự dừng",
            reason + " Xử lý nguyên nhân rồi bật lại chiến dịch; app không tự chạy lại.", ct);
        await audit.WriteAsync("planner", "campaign.autopause", c.Id, reason, ct);
    }

    /// <summary>Dừng mọi chiến dịch đang chạy có đăng lên kênh này (kênh bất thường). Trả số chiến dịch đã dừng.</summary>
    public async Task<int> PauseForDestinationAsync(string destinationId, string reason, CancellationToken ct)
    {
        var rules = (await campaigns.ListRulesAsync(null, ct)).Where(r => r.DestinationIds.Contains(destinationId)).Select(r => r.CampaignId).ToHashSet();
        if (rules.Count == 0) return 0;
        var paused = 0;
        foreach (var c in (await campaigns.ListAsync(ct)).Where(c => c.Status == CampaignStatus.Active && rules.Contains(c.Id)))
        {
            await PauseAsync(c, reason, ct);
            paused++;
        }
        return paused;
    }
}

/// <summary>
/// Chính sách trễ lịch cho bài chiến dịch (máy tắt, sleep): Skip / Shift (slot kế còn trống) / CatchUp (có trần).
/// Gọi từ planner mỗi lượt và từ job đăng khi gặp bài trễ, để đăng bù không bị dồn khi app vừa thức dậy.
/// </summary>
[Service(As = [typeof(ICampaignPublishGate)])]
public sealed class LatePublicationResolver(
    ICampaignRepository campaigns,
    IPublicationRepository publications,
    IAuditLog audit,
    IClock clock) : ICampaignPublishGate
{
    private static readonly TimeSpan PausedRecheck = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<int> ResolveAsync(string? campaignId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.UtcNow;
            var late = await campaigns.ListLateAsync(now, campaignId, ct);
            if (late.Count == 0) return 0;
            var all = (await campaigns.ListAsync(ct)).ToDictionary(c => c.Id);
            var policies = (await campaigns.ListPoliciesAsync(campaignId, ct)).ToDictionary(p => p.CampaignId);
            var posts = (await campaigns.ListChannelPostsAsync(ct)).ToLookup(p => p.DestinationId);
            var handled = 0;

            foreach (var group in late.GroupBy(l => (l.CampaignId, l.DestinationId)))
            {
                // Chiến dịch tạm dừng: để nguyên, xét khi bật lại.
                if (!all.TryGetValue(group.Key.CampaignId, out var campaign) || campaign.Status != CampaignStatus.Active) continue;
                if (!policies.TryGetValue(campaign.Id, out var policy)) continue;
                var grace = TimeSpan.FromMinutes(policy.LateGraceMinutes);
                var due = group.Where(l => now - l.ScheduledUtc > grace).ToList();
                if (due.Count == 0) continue;

                var schedule = CampaignSchedules.For(campaign, policy, group.Key.DestinationId);
                var lateIds = due.Select(d => d.PublicationId).ToHashSet();
                var occupied = posts[group.Key.DestinationId].Where(p => !lateIds.Contains(p.PublicationId)).Select(p => p.AtUtc).ToList();
                DateTimeOffset? NextSlot(DateTimeOffset after)
                {
                    foreach (var slot in SlotScheduler.GenerateSlots(schedule, after, 14).Where(t => t > after && t > now))
                        if (SlotScheduler.CanPlace(schedule, occupied, slot))
                        {
                            occupied.Add(slot);
                            return slot;
                        }
                    return null;
                }

                var options = new LatePolicyOptions(policy.LateMode, grace, TimeSpan.FromMinutes(policy.LateMaxLatenessMinutes), policy.LateMaxCatchUp);
                var decisions = LatePolicy.Decide(due.Select(d => new DuePost(d.PublicationId, d.ScheduledUtc)).ToList(), now, options, NextSlot);
                foreach (var d in decisions)
                {
                    var item = due.First(x => x.PublicationId == d.PublicationId);
                    var lateBy = (int)(now - item.ScheduledUtc).TotalMinutes;
                    switch (d.Action)
                    {
                        case LateAction.PublishNow:
                            await campaigns.MarkLateResolvedAsync(d.PublicationId, ct);
                            break;
                        case LateAction.Reschedule:
                            await publications.RequeueAsync(new PublicationRequeue(d.PublicationId, [PublicationStatus.Scheduled], PublicationStatus.Scheduled,
                                d.NewTimeUtc!.Value, Schedule: (d.NewTimeUtc, false)), ct);
                            break;
                        default:
                            await campaigns.CancelPendingAsync(d.PublicationId, $"Trễ lịch {lateBy} phút: {d.Reason}", ct);
                            break;
                    }
                    await audit.WriteAsync("planner", "publication.late." + d.Action, d.PublicationId, $"late={lateBy}m; {d.Reason}", ct);
                    handled++;
                }
            }
            return handled;
        }
        finally { _gate.Release(); }
    }

    public async Task<CampaignGateResult> CheckAsync(Publication pub, string jobId, CancellationToken ct)
    {
        if (pub.CampaignId is null) return CampaignGateResult.Continue;
        var campaign = await campaigns.GetAsync(pub.CampaignId, ct);
        if (campaign is null) return CampaignGateResult.Continue;
        var now = clock.UtcNow;
        if (campaign.Status != CampaignStatus.Active)
            return new CampaignGateResult(false, now + PausedRecheck, $"Chiến dịch \"{campaign.Name}\" đang {campaign.Status}: bài chờ tới khi chiến dịch chạy lại.");
        if (pub.LateResolved || pub.ScheduledUtc is not { } at || pub.UseNativeSchedule) return CampaignGateResult.Continue;

        var policy = (await campaigns.ListPoliciesAsync(campaign.Id, ct)).FirstOrDefault();
        if (policy is null || now - at <= TimeSpan.FromMinutes(policy.LateGraceMinutes)) return CampaignGateResult.Continue;

        await ResolveAsync(campaign.Id, ct);
        var current = await publications.GetAsync(pub.Id, ct);
        return current is { Status: PublicationStatus.Scheduled, LateResolved: true } && current.JobId == jobId
            ? CampaignGateResult.Continue
            : CampaignGateResult.Stop;
    }
}
