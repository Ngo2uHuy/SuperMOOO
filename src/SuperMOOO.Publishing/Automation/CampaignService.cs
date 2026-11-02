using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Http;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.Publishing.Automation;

public sealed record CampaignSummary(Campaign Campaign, CampaignCounts Counts);

public sealed record BulkResult(IReadOnlyList<string> Succeeded, IReadOnlyList<string> Errors);

/// <summary>
/// Use case của màn S22: lưu/bật/dừng/xóa chiến dịch, xem trước lịch, duyệt hoặc từ chối bài chờ duyệt. Lỗi do người dùng →
/// <see cref="PublishingException"/>. Mức "Chỉ gợi ý" không tạo bài; "Cần duyệt" tạo bài AwaitingApproval; "Tự động" tạo bài Scheduled.
/// </summary>
[Service]
public sealed class CampaignService(
    ICampaignRepository campaigns,
    IPublicationRepository publications,
    IDestinationRepository destinations,
    IConnectionRepository connections,
    IContentRepository contents,
    ISourceRepository sources,
    CampaignPlanner planner,
    RiskAssessor risk,
    IActionRequiredRepository actions,
    IPublishSettingsStore settings,
    IAuditLog audit,
    IClock clock)
{
    public const int MaxRules = 50;
    public const int MaxSlots = 200;
    public const int MaxBulk = 200;

    // --- Đọc ---

    public async Task<IReadOnlyList<CampaignSummary>> ListAsync(CancellationToken ct)
    {
        var counts = await campaigns.CountsAsync(clock.UtcNow, ct);
        return (await campaigns.ListAsync(ct)).Select(c => new CampaignSummary(c, counts.GetValueOrDefault(c.Id) ?? new CampaignCounts(0, 0, 0))).ToList();
    }

    public async Task<CampaignPlan> GetAsync(string id, CancellationToken ct)
    {
        var c = await campaigns.GetAsync(id, ct) ?? throw NotFound();
        var rules = await campaigns.ListRulesAsync(id, ct);
        var policy = (await campaigns.ListPoliciesAsync(id, ct)).FirstOrDefault() ?? new SchedulePolicy { CampaignId = id };
        return new CampaignPlan(c, rules, policy);
    }

    // --- Ghi ---

    /// <summary>Lưu chiến dịch (mới hoặc sửa); trạng thái bật/dừng giữ nguyên, chiến dịch mới ở trạng thái Paused.</summary>
    public async Task<CampaignPlan> SaveAsync(CampaignPlan draft, CancellationToken ct)
    {
        var existing = await campaigns.GetAsync(draft.Campaign.Id, ct);
        var plan = await NormalizeAsync(draft, existing, ct);
        await campaigns.SaveAsync(plan.Campaign, plan.Rules, plan.Policy, ct);
        await audit.WriteAsync("ui", "campaign.upsert", plan.Campaign.Id, plan.Campaign.Name, ct);
        return await GetAsync(plan.Campaign.Id, ct);
    }

    public async Task SetStatusAsync(string id, CampaignStatus status, CancellationToken ct)
    {
        var c = await campaigns.GetAsync(id, ct) ?? throw NotFound();
        if (status == c.Status) return;
        if (status == CampaignStatus.Active)
        {
            var rules = await campaigns.ListRulesAsync(c.Id, ct);
            var policy = (await campaigns.ListPoliciesAsync(c.Id, ct)).FirstOrDefault();
            if (rules.Count == 0 || rules.All(x => x.DestinationIds.Count == 0)) throw Invalid("Cần ít nhất một quy tắc có kênh đích trước khi bật.");
            if (policy is null || policy.Slots.Count == 0) throw Invalid("Cần ít nhất một slot giờ đăng trước khi bật.");
        }
        var reason = status switch
        {
            CampaignStatus.Paused => "Người dùng tạm dừng.",
            CampaignStatus.Stopped => "Người dùng dừng hẳn.",
            _ => null
        };
        // Mốc đổi trạng thái mới: lỗi cũ trước khi bật lại không làm chiến dịch dừng ngay.
        if (!await campaigns.SetStatusAsync(c.Id, status, reason, [c.Status], clock.UtcNow, ct)) throw Invalid("Chiến dịch vừa đổi trạng thái; tải lại.");
        if (status == CampaignStatus.Active) await actions.ResolveByRefAsync(ActionKinds.CampaignPaused, c.Id, ct);
        if (status == CampaignStatus.Stopped) await CancelPendingAsync(c.Id, "Chiến dịch đã dừng hẳn.", ct);
        await audit.WriteAsync("ui", "campaign.status", c.Id, status.ToString(), ct);
    }

    /// <summary>Nút "Dừng tất cả": tạm dừng mọi chiến dịch đang chạy (bài đã hẹn giữ nguyên, chờ tới khi chạy lại). Trả số chiến dịch đã dừng.</summary>
    public async Task<int> PauseAllAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var c in (await campaigns.ListAsync(ct)).Where(c => c.Status == CampaignStatus.Active))
            if (await campaigns.SetStatusAsync(c.Id, CampaignStatus.Paused, "Người dùng bấm Dừng tất cả.", [CampaignStatus.Active], clock.UtcNow, ct)) count++;
        if (count > 0) await audit.WriteAsync("ui", "campaign.pause_all", "*", $"count={count}", ct);
        return count;
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        var c = await campaigns.GetAsync(id, ct) ?? throw NotFound();
        // Bài chưa upload của chiến dịch bị hủy; bài đã đăng giữ nguyên (campaign_id về NULL).
        await CancelPendingAsync(c.Id, "Chiến dịch đã bị xóa.", ct);
        await campaigns.RemoveAsync(c.Id, ct);
        foreach (var kind in new[] { ActionKinds.CampaignPaused, ActionKinds.CampaignPlanBlocked, ActionKinds.ProductionBlocked })
            await actions.ResolveByRefAsync(kind, c.Id, ct);
        await audit.WriteAsync("ui", "campaign.remove", c.Id, c.Name, ct);
    }

    private async Task CancelPendingAsync(string campaignId, string reason, CancellationToken ct)
    {
        foreach (var id in await campaigns.ListPendingAsync(campaignId, ct))
            if (await campaigns.CancelPendingAsync(id, reason, ct))
                await actions.ResolveByRefAsync(ActionKinds.PublishAwaitingApproval, id, ct);
    }

    // --- Lập lịch / xem trước ---

    /// <summary>"Lập lịch ngay": chạy một lượt planner (tôn trọng automation_paused).</summary>
    public Task<PlanResult> RunPlannerAsync(CancellationToken ct) => planner.RunAsync(ct);

    /// <summary>Xem trước các bài kế tiếp của bản nháp (hoặc bản đã lưu) trong <paramref name="days"/> ngày tới; không ghi gì.</summary>
    public async Task<PlanResult> PreviewAsync(CampaignPlan draft, int days, CancellationToken ct)
    {
        var existing = await campaigns.GetAsync(draft.Campaign.Id, ct);
        var plan = await NormalizeAsync(draft, existing, ct);
        var result = await planner.PreviewAsync(plan, 200, ct);
        var until = clock.UtcNow.AddDays(days);
        return result with { Entries = result.Entries.Where(e => e.Post.ScheduledUtc <= until).ToList() };
    }

    // --- Duyệt / từ chối ---

    public async Task<IReadOnlyList<PublicationRow>> ListAwaitingApprovalAsync(string? campaignId, CancellationToken ct) =>
        (await publications.QueryAsync(new PublicationQuery(PublicationStatus.AwaitingApproval, null, null, null, 1, PublishingJson.MaxPageSize, campaignId), ct)).Items;

    public async Task<BulkResult> ApproveAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ids = ValidateIds(ids);
        var now = clock.UtcNow;
        var ok = new List<string>();
        var errors = new List<string>();
        var ready = new List<(Publication Pub, SocialConnection Conn, DateTimeOffset At, FinishedContent Content)>();
        var audited = await PlatformFlags.TikTokAuditedAsync(settings, ct);
        foreach (var id in ids)
        {
            var pub = await publications.GetAsync(id, ct);
            if (pub is null) { errors.Add($"{id}: không tìm thấy bài."); continue; }
            if (pub.Status != PublicationStatus.AwaitingApproval) { errors.Add($"\"{pub.Title}\": không ở trạng thái chờ duyệt."); continue; }
            var conn = await ConnectionOfAsync(pub, ct);
            if (conn is null || conn.ApiStatus != ApiStatus.Connected) { errors.Add($"\"{pub.Title}\": tài khoản chưa kết nối API."); continue; }
            var tikTok = PlatformPostSettings.TikTokOf(pub.PlatformSettingsJson);
            if (TikTokPostRules.Validate(tikTok, audited, conn.Scopes) is { Count: > 0 } formErrors)
            {
                errors.Add($"\"{pub.Title}\": hoàn tất form TikTok (S14 → Bài đăng) trước khi duyệt. {string.Join(" ", formErrors)}");
                continue;
            }
            var content = (await contents.GetAsync(pub.ContentItemId, ct))?.Item;
            if (content is null) { errors.Add($"\"{pub.Title}\": nội dung không còn."); continue; }
            // Duyệt muộn hơn giờ hẹn: đăng ngay khi đến lượt (người dùng vừa chủ động duyệt).
            ready.Add((pub, conn, pub.ScheduledUtc is { } s && s > now ? s : now, content));
        }

        var warnings = await risk.AssessAsync(ready.Select(x => new RiskItem(x.Pub.Id, x.Pub.DestinationId, x.Conn.Id, x.At, x.Content, x.Pub.Description,
            x.Pub.ContainsSyntheticMedia)).ToList(), ct);
        foreach (var (pub, _, at, _) in ready)
        {
            if (PublishRiskAdvisor.IsBlocked(warnings, pub.Id))
            {
                errors.Add($"\"{pub.Title}\": " + string.Join(" ", warnings.Where(w => w.PublicationId == pub.Id && w.Level == RiskLevel.Block).Select(w => w.Message)));
                continue;
            }
            if (!await publications.RequeueAsync(new PublicationRequeue(pub.Id, [PublicationStatus.AwaitingApproval], PublicationStatus.Scheduled, at,
                    Schedule: (at, pub.UseNativeSchedule)), ct))
            {
                errors.Add($"\"{pub.Title}\": bài vừa đổi trạng thái.");
                continue;
            }
            await actions.ResolveByRefAsync(ActionKinds.PublishAwaitingApproval, pub.Id, ct);
            ok.Add(pub.Id);
        }
        if (ok.Count > 0) await audit.WriteAsync("ui", "publication.approve", string.Join(',', ok), null, ct);
        return new BulkResult(ok, errors);
    }

    public async Task<BulkResult> RejectAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var ok = new List<string>();
        var errors = new List<string>();
        foreach (var id in ValidateIds(ids))
        {
            var pub = await publications.GetAsync(id, ct);
            if (pub is null) { errors.Add($"{id}: không tìm thấy bài."); continue; }
            if (pub.Status != PublicationStatus.AwaitingApproval || !await publications.CancelAsync(id, allowUnknown: false, ct))
            {
                errors.Add($"\"{pub.Title}\": không ở trạng thái chờ duyệt.");
                continue;
            }
            await actions.ResolveByRefAsync(ActionKinds.PublishAwaitingApproval, id, ct);
            ok.Add(id);
        }
        if (ok.Count > 0) await audit.WriteAsync("ui", "publication.reject", string.Join(',', ok), null, ct);
        return new BulkResult(ok, errors);
    }

    private static IReadOnlyList<string> ValidateIds(IReadOnlyList<string>? ids)
    {
        var list = (ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        if (list.Count == 0 || list.Count > MaxBulk) throw Invalid($"Cần 1..{MaxBulk} bài.");
        return list;
    }

    private async Task<SocialConnection?> ConnectionOfAsync(Publication pub, CancellationToken ct)
    {
        var destination = await destinations.GetAsync(pub.DestinationId, ct);
        return destination is null ? null : await connections.GetAsync(destination.ConnectionId, ct);
    }

    // --- Kiểm tra dữ liệu ---

    private async Task<CampaignPlan> NormalizeAsync(CampaignPlan draft, Campaign? existing, CancellationToken ct)
    {
        var r = draft.Campaign;
        var name = r.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200) throw Invalid("Tên chiến dịch phải có 1..200 ký tự.");
        if (TimeZones.Find(r.TimeZoneId) is null) throw Invalid("Múi giờ không hợp lệ.");
        if (double.IsNaN(r.Weight) || r.Weight is <= 0 or > 100) throw Invalid("Trọng số phải trong (0, 100].");
        if (r.ContentMaxAgeDays is <= 0 or > 3650) throw Invalid("Hạn sử dụng nội dung phải trong 1..3650 ngày.");
        if (r.PostBudget is <= 0) throw Invalid("Ngân sách bài phải > 0.");
        if (r.MaxConsecutiveFailures is < 1 or > 100) throw Invalid("Số lỗi liên tiếp tối đa phải trong 1..100.");
        if (r.EvergreenMinDays is < 1 or > 3650) throw Invalid("Chu kỳ evergreen phải trong 1..3650 ngày.");
        if (string.IsNullOrWhiteSpace(r.TitleTemplate) || r.TitleTemplate.Length > 500) throw Invalid("Mẫu tiêu đề phải có 1..500 ký tự.");
        if ((r.DescriptionTemplate ?? "").Length > 5000) throw Invalid("Mẫu mô tả tối đa 5000 ký tự.");
        if (r.PlatformSettingsJson is { Length: > 0 } json
            && PlatformPostSettings.TikTokOf(json) is { } tikTok && tikTok.Mode is not (TikTokPostModes.Inbox or TikTokPostModes.DirectPost))
            throw Invalid("Chế độ đăng TikTok không hợp lệ.");

        var now = clock.UtcNow;
        var id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N") : r.Id;
        var campaign = new Campaign
        {
            Id = id, Name = name, Status = existing?.Status ?? CampaignStatus.Paused, TimeZoneId = r.TimeZoneId.Trim(), Weight = r.Weight,
            Automation = r.Automation, SuggestOnly = r.SuggestOnly, ContentMaxAgeDays = r.ContentMaxAgeDays, PostBudget = r.PostBudget,
            MaxConsecutiveFailures = r.MaxConsecutiveFailures, TitleTemplate = r.TitleTemplate.Trim(), DescriptionTemplate = r.DescriptionTemplate ?? "",
            Privacy = r.Privacy, Evergreen = r.Evergreen, EvergreenMinDays = r.EvergreenMinDays,
            // Form Direct Post thiếu/sai vẫn lưu được: bài TikTok của chiến dịch sẽ chờ duyệt để người dùng hoàn tất.
            PlatformSettingsJson = string.IsNullOrWhiteSpace(r.PlatformSettingsJson) ? null : r.PlatformSettingsJson,
            StatusReason = existing?.StatusReason, StatusChangedUtc = existing?.StatusChangedUtc ?? now,
            CreatedUtc = existing?.CreatedUtc ?? now, UpdatedUtc = now
        };

        var known = (await destinations.ListAsync(null, true, ct)).ToDictionary(d => d.Id);
        var sourceIds = (await sources.ListAsync(ct)).Select(s => s.Id).ToHashSet();
        if (draft.Rules.Count > MaxRules) throw Invalid($"Tối đa {MaxRules} quy tắc.");
        var rules = new List<CampaignRule>();
        foreach (var (rule, i) in draft.Rules.Select((x, i) => (x, i + 1)))
        {
            var label = $"Quy tắc {i}";
            var dests = rule.DestinationIds.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToList();
            if (dests.Count == 0) throw Invalid($"{label}: chọn ít nhất một kênh đích.");
            if (dests.FirstOrDefault(d => !known.ContainsKey(d)) is { } missing) throw Invalid($"{label}: không tìm thấy kênh {missing}.");
            if (rule.SourceId is not null && !sourceIds.Contains(rule.SourceId)) throw Invalid($"{label}: không tìm thấy nguồn.");
            if (rule.MinDurationSec is < 0 || rule.MaxDurationSec is < 0 || rule.MinDurationSec > rule.MaxDurationSec)
                throw Invalid($"{label}: khoảng thời lượng không hợp lệ.");
            rules.Add(new CampaignRule
            {
                Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id, CampaignId = id, Position = i,
                SourceId = string.IsNullOrWhiteSpace(rule.SourceId) ? null : rule.SourceId,
                Tags = rule.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Language = string.IsNullOrWhiteSpace(rule.Language) ? null : rule.Language.Trim(),
                MinDurationSec = rule.MinDurationSec, MaxDurationSec = rule.MaxDurationSec, Aspect = rule.Aspect, DestinationIds = dests
            });
        }

        var p = draft.Policy;
        var ruleDests = rules.SelectMany(x => x.DestinationIds).ToHashSet();
        if (p.Slots.Count > MaxSlots) throw Invalid($"Tối đa {MaxSlots} slot.");
        foreach (var (s, i) in p.Slots.Select((s, i) => (s, i + 1)))
            if (s.DestinationId is not null && !ruleDests.Contains(s.DestinationId))
                throw Invalid($"Slot {i}: kênh không nằm trong quy tắc của chiến dịch.");
        if (p.MaxPerDay is < 1 or > 50) throw Invalid("Giới hạn bài/ngày phải trong 1..50.");
        if (p.MinGapMinutes is < 0 or > 1440) throw Invalid("Khoảng cách tối thiểu phải trong 0..1440 phút.");
        if (p.JitterMinutes is < 0 or > 120) throw Invalid("Jitter phải trong 0..120 phút.");
        if (p.LateGraceMinutes is < 0 or > 1440) throw Invalid("Ân hạn trễ phải trong 0..1440 phút.");
        if (p.LateMaxCatchUp is < 0 or > 50) throw Invalid("Trần bài đăng bù phải trong 0..50.");
        if (p.LateMaxLatenessMinutes is < 0 or > 10080) throw Invalid("Cửa sổ đăng bù phải trong 0..10080 phút.");
        if (p.HorizonDays is < 1 or > 28) throw Invalid("Số ngày xếp trước phải trong 1..28.");
        var policy = new SchedulePolicy
        {
            CampaignId = id, Slots = p.Slots.Distinct().ToList(), MaxPerDay = p.MaxPerDay, MinGapMinutes = p.MinGapMinutes, Forbidden = p.Forbidden,
            JitterMinutes = p.JitterMinutes, LateMode = p.LateMode, LateGraceMinutes = p.LateGraceMinutes, LateMaxCatchUp = p.LateMaxCatchUp,
            LateMaxLatenessMinutes = p.LateMaxLatenessMinutes, HorizonDays = p.HorizonDays
        };
        return new CampaignPlan(campaign, rules, policy);
    }

    private static PublishingException Invalid(string message) => new(PublishingErrors.InvalidRequest, message);
    private static PublishingException NotFound() => new(PublishingErrors.NotFound, "Không tìm thấy chiến dịch.");
}
