using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Http;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.Publishing.Automation;

public sealed record CampaignPlan(Campaign Campaign, IReadOnlyList<CampaignRule> Rules, SchedulePolicy Policy);

/// <param name="PlatformSettingsJson">Lựa chọn riêng theo nền tảng lấy từ mặc định của chiến dịch (TikTok Inbox/Direct Post).</param>
public sealed record PlannedPost(
    string PublicationId, CampaignPlan Plan, SocialDestination Destination, SocialConnection Connection, FinishedContent Content,
    string PlanKey, DateTimeOffset ScheduledUtc, string Title, string Description, IReadOnlyList<string> Tags,
    string? PlatformSettingsJson = null)
{
    /// <summary>Instagram luôn công khai theo tài khoản: không áp quyền riêng tư của chiến dịch.</summary>
    public PublishPrivacy Privacy => Destination.Platform == SocialPlatform.Instagram ? PublishPrivacy.Public : Plan.Campaign.Privacy;
}

public static class PlanOutcomes
{
    public const string Scheduled = "Scheduled";
    public const string AwaitingApproval = "AwaitingApproval";
    /// <summary>Chiến dịch ở mức "Chỉ gợi ý": chỉ hiện trong xem trước, không tạo bài.</summary>
    public const string Suggested = "Suggested";
    public const string Blocked = "Blocked";
}

public sealed record PlanEntry(PlannedPost Post, string Outcome, IReadOnlyList<RiskWarning> Warnings);

public sealed record PlanResult(IReadOnlyList<PlanEntry> Entries, IReadOnlyList<string> Notes, int Created);

/// <summary>Map Domain → record của thuật toán lịch chiến dịch.</summary>
public static class CampaignSchedules
{
    public static ChannelSchedule For(Campaign campaign, SchedulePolicy policy, string destinationId) => new(
        campaign.ResolveTimeZone(),
        policy.SlotsFor(destinationId).Select(s => new WeeklySlot(s.Day, s.Time)).Distinct().ToList(),
        new SchedulingConstraints(policy.MaxPerDay, TimeSpan.FromMinutes(policy.MinGapMinutes),
            policy.Forbidden.Select(f => new ForbiddenWindow(f.Start, f.End)).ToList(), policy.JitterMinutes));

    /// <summary>Định danh slot ổn định: chạy planner lại (kể cả sau restart) không tạo bài thứ hai cho cùng slot.</summary>
    public static string PlanKey(string campaignId, string destinationId, DateTimeOffset slotUtc) =>
        $"{campaignId}:{destinationId}:{slotUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}";

    public static string LocalText(Campaign c, DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, c.ResolveTimeZone()).ToString("ddd dd/MM HH:mm", CultureInfo.GetCultureInfo("vi-VN"));
}

/// <summary>
/// Lập lịch chiến dịch: chọn nội dung (ContentSelector, loại gần trùng nếu có chữ ký) → chia slot giữa các chiến dịch chung kênh
/// (FairShare, deficit lưu DB) → xếp slot → RiskAssessor → ghi trong 1 transaction (origin "campaign", origin_ref = id chiến dịch).
/// Idempotent: mỗi slot có plan_key duy nhất; xem trước dùng cùng thuật toán nhưng không ghi gì. Bài tới giờ do PublishDispatcher xếp job.
/// </summary>
[Service]
public sealed class CampaignPlanner(
    ICampaignRepository campaigns,
    IContentRepository contents,
    ISourceRepository sources,
    IDestinationRepository destinations,
    IConnectionRepository connections,
    IContentSignatureRepository signatures,
    ConnectorRegistry registry,
    RiskAssessor risk,
    CampaignGuard guard,
    LatePublicationResolver late,
    IPublishSettingsStore settings,
    IProductionRepository production,
    IActionRequiredRepository actions,
    IAuditLog audit,
    IClock clock,
    ILogger<CampaignPlanner> logger)
{
    private const int MaxTitle = 100;
    private const int MaxDescription = 5000;
    /// <summary>Số nội dung ứng viên tối đa xét cho một cặp chiến dịch × kênh mỗi lượt.</summary>
    private const int MaxCandidates = 500;
    /// <summary>Lớn hơn tổng điểm độ mới + gấp (tối đa 2) để nội dung đặt riêng cho chiến dịch luôn được chọn trước.</summary>
    private const double OwnRequestPriority = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class State(CampaignPlan plan, ChannelSchedule schedule, Queue<string> queue)
    {
        public CampaignPlan Plan { get; } = plan;
        public ChannelSchedule Schedule { get; } = schedule;
        public Queue<string> Queue { get; } = queue;
        public string Id => Plan.Campaign.Id;
    }

    public async Task<IReadOnlyList<CampaignPlan>> LoadPlansAsync(CancellationToken ct)
    {
        var rules = (await campaigns.ListRulesAsync(null, ct)).ToLookup(r => r.CampaignId);
        var policies = (await campaigns.ListPoliciesAsync(null, ct)).ToDictionary(p => p.CampaignId);
        return (await campaigns.ListAsync(ct))
            .Select(c => new CampaignPlan(c, rules[c.Id].ToList(), policies.GetValueOrDefault(c.Id) ?? new SchedulePolicy { CampaignId = c.Id }))
            .ToList();
    }

    /// <summary>Một lượt thật: xử lý bài trễ, kiểm tra điều kiện dừng, lập lịch và ghi. automation_paused → không làm gì.</summary>
    public async Task<PlanResult> RunAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (PlatformFlags.IsTrue(await settings.GetAsync(SettingKeys.AutomationPaused, ct)))
                return new PlanResult([], ["Đăng tự động đang tạm dừng: planner không chạy."], 0);

            await late.ResolveAsync(null, ct);
            var active = new List<CampaignPlan>();
            foreach (var plan in (await LoadPlansAsync(ct)).Where(p => p.Campaign.Status == CampaignStatus.Active && !p.Campaign.SuggestOnly))
            {
                if (await guard.CheckAsync(plan, ct) is { } reason) await guard.PauseAsync(plan.Campaign, reason, ct);
                else active.Add(plan);
            }
            var (result, deficits, blocked) = await PlanAsync(active, clock.UtcNow, ct);
            if (active.Count == 0) return result;
            return result with { Created = await CommitAsync(result, deficits, blocked, ct) };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Xem trước cho một chiến dịch (đã lưu hoặc nháp, kể cả đang tạm dừng) cùng các chiến dịch Active khác chung kênh.</summary>
    public async Task<PlanResult> PreviewAsync(CampaignPlan target, int count, CancellationToken ct)
    {
        var others = (await LoadPlansAsync(ct)).Where(p => p.Campaign.Status == CampaignStatus.Active && !p.Campaign.SuggestOnly && p.Campaign.Id != target.Campaign.Id);
        var (result, _, blocked) = await PlanAsync([.. others, target], clock.UtcNow, ct);
        var mine = result.Entries.Where(e => e.Post.Plan.Campaign.Id == target.Campaign.Id)
            .OrderBy(e => e.Post.ScheduledUtc).ThenBy(e => e.Post.Destination.Title, StringComparer.CurrentCulture).Take(count).ToList();
        var notes = result.Notes.Concat(blocked.GetValueOrDefault(target.Campaign.Id) ?? []).Distinct().ToList();
        return result with { Entries = mine, Notes = notes };
    }

    private async Task<(PlanResult Result, Dictionary<string, IReadOnlyDictionary<string, double>> Deficits, Dictionary<string, List<string>> Blocked)>
        PlanAsync(IReadOnlyList<CampaignPlan> plans, DateTimeOffset now, CancellationToken ct)
    {
        var notes = new List<string>();
        var blockedNotes = new Dictionary<string, List<string>>();
        var deficitsOut = new Dictionary<string, IReadOnlyDictionary<string, double>>();
        if (plans.Count == 0) return (new PlanResult([], notes, 0), deficitsOut, blockedNotes);

        var dests = (await destinations.ListAsync(null, true, ct)).ToDictionary(d => d.Id);
        var conns = (await connections.ListAsync(ct)).ToDictionary(c => c.Id);
        var sourceNames = (await sources.ListAsync(ct)).ToDictionary(s => s.Id, s => s.Name);
        var items = await contents.ListReadyAsync(ct);
        var byId = items.ToDictionary(i => i.Id);
        var tags = items.ToDictionary(i => i.Id, i => (IReadOnlyList<string>)PublishText.ParseTags(i.TagsJson));
        var sigs = (await signatures.ListAsync(ct)).ToDictionary(kv => kv.Key, kv => new VideoSignature(kv.Value));
        var postsByDest = (await campaigns.ListChannelPostsAsync(ct)).ToLookup(p => p.DestinationId);
        var savedDeficits = await campaigns.LoadDeficitsAsync(ct);
        var counts = await campaigns.CountsAsync(now, ct);
        var requestCampaigns = await production.RequestCampaignsAsync(ct);
        var budget = plans.ToDictionary(p => p.Campaign.Id,
            p => p.Campaign.PostBudget is { } b ? b - (counts.GetValueOrDefault(p.Campaign.Id)?.Total ?? 0) : int.MaxValue);
        static bool Near(VideoSignature a, VideoSignature b) => VideoSignature.Similarity(a, b) >= RiskAssessor.Options.VideoSimilarityThreshold;

        var byDest = new SortedDictionary<string, List<CampaignPlan>>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            if (plan.Rules.Count == 0) notes.Add($"\"{plan.Campaign.Name}\": chưa có quy tắc chọn nội dung/kênh.");
            if (plan.Policy.Slots.Count == 0) notes.Add($"\"{plan.Campaign.Name}\": chưa có slot giờ đăng.");
            foreach (var destId in plan.Rules.SelectMany(r => r.DestinationIds).Distinct())
            {
                if (!byDest.TryGetValue(destId, out var list)) byDest[destId] = list = [];
                list.Add(plan);
            }
        }

        var planned = new List<PlannedPost>();
        foreach (var (destId, group) in byDest)
        {
            if (!dests.TryGetValue(destId, out var dest) || dest.RemovedUtc is not null || !dest.Enabled)
            {
                notes.Add($"Kênh {dest?.Title ?? destId} không còn khả dụng: bỏ qua.");
                continue;
            }
            if (!conns.TryGetValue(dest.ConnectionId, out var conn) || conn.ApiStatus != ApiStatus.Connected)
            {
                notes.Add($"Kênh \"{dest.Title}\": tài khoản chưa kết nối API, chưa xếp lịch.");
                continue;
            }
            if (registry.Find(dest.Platform) is not { } connector)
            {
                notes.Add($"Kênh \"{dest.Title}\": nền tảng chưa có connector.");
                continue;
            }
            // Vd Instagram chưa có media host, Page thiếu quyền tạo nội dung: chặn cả kênh, báo lý do cho từng chiến dịch.
            if (DestinationCapabilities.AutoPublishBlockedReason(dest.Platform, DestinationCapabilities.For(connector, conn, dest)) is { } blockedReason)
            {
                foreach (var plan in group.DistinctBy(p => p.Campaign.Id))
                    Note(blockedNotes, plan.Campaign.Id, $"Kênh \"{dest.Title}\" ({dest.Platform}) không đăng tự động được: {blockedReason}");
                continue;
            }

            var channel = postsByDest[destId].ToList();
            var occupied = channel.Select(p => p.AtUtc).ToList();
            var keys = channel.Where(p => p.PlanKey is not null).Select(p => p.PlanKey!).ToHashSet(StringComparer.Ordinal);
            var history = channel.Select(p => new ChannelPostRecord(p.ContentItemId, p.AtUtc, sigs.GetValueOrDefault(p.ContentItemId))).ToList();
            var used = new List<(string Id, VideoSignature? Sig)>();

            var states = new List<State>();
            foreach (var plan in group.DistinctBy(p => p.Campaign.Id))
            {
                var c = plan.Campaign;
                var candidates = items
                    .Where(i => plan.Rules.Any(r => r.DestinationIds.Contains(destId) && r.Matches(i, tags[i.Id])))
                    .Select(i => new ContentCandidate(i.Id, IsOwnRequest(i, c.Id, requestCampaigns) ? OwnRequestPriority : 0, i.ImportedUtc,
                        c.ContentMaxAgeDays is { } d ? i.ImportedUtc.AddDays(d) : null, c.Evergreen, sigs.GetValueOrDefault(i.Id)))
                    .ToList();
                var selected = ContentSelector.Select(candidates, history, now, SelectorOptions(c), Near, MaxCandidates);
                if (candidates.Count == 0) notes.Add($"\"{c.Name}\" → \"{dest.Title}\": không có nội dung khớp quy tắc.");
                else if (selected.Count == 0) notes.Add($"\"{c.Name}\" → \"{dest.Title}\": đã hết nội dung chưa đăng (hoặc đã hết hạn).");
                states.Add(new State(plan, CampaignSchedules.For(c, plan.Policy, destId), new Queue<string>(selected.Select(s => s.ContentId))));
            }

            var fair = new FairShare(savedDeficits.GetValueOrDefault(destId) ?? new Dictionary<string, double>());
            var events = states
                .SelectMany(s => SlotScheduler.GenerateSlots(s.Schedule, now, s.Plan.Policy.HorizonDays).Select(t => (Slot: t, State: s)))
                .GroupBy(e => e.Slot)
                .OrderBy(g => g.Key);
            foreach (var slot in events)
            {
                var tried = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    var eligible = new List<(State S, string Key, DateTimeOffset At)>();
                    foreach (var s in slot.Select(e => e.State).OrderBy(s => s.Id, StringComparer.Ordinal))
                    {
                        if (tried.Contains(s.Id) || s.Queue.Count == 0 || budget[s.Id] <= 0) continue;
                        var key = CampaignSchedules.PlanKey(s.Id, destId, slot.Key);
                        if (keys.Contains(key)) continue;
                        var at = slot.Key + TimeSpan.FromMinutes(SlotScheduler.JitterMinutes(key, s.Plan.Policy.JitterMinutes));
                        if (at <= now || !SlotScheduler.CanPlace(s.Schedule, occupied, at)) continue;
                        eligible.Add((s, key, at));
                    }
                    if (eligible.Count == 0) break;

                    // Chỉ dùng DRR khi thật sự tranh slot; một ứng viên thì không đụng deficit của chiến dịch khác.
                    var pick = eligible[0];
                    if (eligible.Count > 1)
                    {
                        var winner = fair.Pick(eligible.Select(e => new FairShareCandidate(e.S.Id, e.S.Plan.Campaign.Weight, NextExpiry(e.S, byId))).ToList());
                        pick = eligible.First(e => e.S.Id == winner);
                    }
                    tried.Add(pick.S.Id);

                    var post = await NextPostAsync(pick.S, dest, conn, connector, pick.Key, pick.At, used, byId, tags, sigs, sourceNames, blockedNotes, ct);
                    if (post is null) continue;
                    planned.Add(post);
                    occupied.Add(pick.At);
                    keys.Add(pick.Key);
                    used.Add((post.Content.Id, sigs.GetValueOrDefault(post.Content.Id)));
                    budget[pick.S.Id]--;
                }
            }
            deficitsOut[destId] = fair.Snapshot();
        }

        var entries = await AssessAsync(planned, blockedNotes, ct);
        return (new PlanResult(entries, notes.Distinct().ToList(), 0), deficitsOut, blockedNotes);
    }

    private static bool IsOwnRequest(FinishedContent item, string campaignId, IReadOnlyDictionary<string, string> requestCampaigns) =>
        item.RequestId is { } r && requestCampaigns.TryGetValue(r, out var owner) && owner == campaignId;

    private static DateTimeOffset? NextExpiry(State s, Dictionary<string, FinishedContent> byId) =>
        s.Plan.Campaign.ContentMaxAgeDays is { } d && s.Queue.TryPeek(out var id) && byId.TryGetValue(id, out var item)
            ? item.ImportedUtc.AddDays(d) : null;

    private static ContentSelectorOptions SelectorOptions(Campaign c) => new(
        // Không evergreen: không bao giờ đăng lại cùng nội dung trên một kênh.
        LruWindow: c.Evergreen ? TimeSpan.FromDays(c.EvergreenMinDays) : TimeSpan.FromDays(36500),
        EvergreenMinInterval: TimeSpan.FromDays(c.EvergreenMinDays),
        FreshnessHalfLife: TimeSpan.FromDays(7),
        UrgencyHorizon: TimeSpan.FromDays(2));

    /// <summary>Lấy nội dung kế tiếp hợp lệ của chiến dịch cho slot: chưa dùng trên kênh trong lượt này, không gần trùng, qua validate của connector.</summary>
    private async Task<PlannedPost?> NextPostAsync(State s, SocialDestination dest, SocialConnection conn, ISocialConnector connector,
        string key, DateTimeOffset at, List<(string Id, VideoSignature? Sig)> used, Dictionary<string, FinishedContent> byId,
        Dictionary<string, IReadOnlyList<string>> tags, Dictionary<string, VideoSignature> sigs, Dictionary<string, string> sourceNames,
        Dictionary<string, List<string>> blockedNotes, CancellationToken ct)
    {
        var c = s.Plan.Campaign;
        while (s.Queue.TryDequeue(out var contentId))
        {
            var item = byId[contentId];
            if (used.Any(u => u.Id == contentId)) continue;
            var sig = sigs.GetValueOrDefault(contentId);
            if (sig is not null && used.Any(u => u.Sig is { } other && VideoSignature.Similarity(sig, other) >= RiskAssessor.Options.VideoSimilarityThreshold)) continue;

            var itemTags = tags[contentId];
            var source = item.SourceId is null ? null : sourceNames.GetValueOrDefault(item.SourceId);
            var title = CaptionTemplate.Render(c.TitleTemplate, item, itemTags, source);
            if (title.Length == 0) title = item.Title;
            if (title.Length > MaxTitle) title = title[..MaxTitle].TrimEnd();
            var description = CaptionTemplate.Render(c.DescriptionTemplate, item, itemTags, source);
            if (description.Length > MaxDescription) description = description[..MaxDescription];

            var detail = await contents.GetAsync(contentId, ct);
            var video = detail?.Assets.FirstOrDefault(a => a.Kind == MediaKind.Video);
            if (video is null) continue;
            var size = File.Exists(video.StoredPath) ? new FileInfo(video.StoredPath).Length : video.SizeBytes;
            var platformJson = dest.Platform == SocialPlatform.TikTok
                ? PlatformPostSettings.Serialize(TikTokPostRules.Normalize(
                    PlatformPostSettings.TikTokOf(c.PlatformSettingsJson) ?? new TikTokPostSettingsDto(TikTokPostModes.Inbox)))
                : null;
            var post = new PlannedPost(Guid.NewGuid().ToString("N"), s.Plan, dest, conn, item, key, at, title, description, itemTags, platformJson);
            var variant = new PublishVariant(title, description, itemTags, post.Privacy, false, item.IsAiGenerated, null, item.DurationMs, platformJson);
            if (connector.Validate(variant, new MediaFile(video.StoredPath, size), PublicationService.ToInfo(dest)) is { Count: > 0 } issues)
            {
                Note(blockedNotes, c.Id, $"\"{item.Title}\" → \"{dest.Title}\": {string.Join(" ", issues.Select(i => i.Message))}");
                continue;
            }
            return post;
        }
        return null;
    }

    /// <summary>Block → không tạo (đánh giá lại phần còn lại vì bài bị loại không còn chiếm chỗ); Warning hoặc ManualApproval → chờ duyệt.</summary>
    private async Task<List<PlanEntry>> AssessAsync(List<PlannedPost> planned, Dictionary<string, List<string>> blockedNotes, CancellationToken ct)
    {
        var warningsById = new Dictionary<string, IReadOnlyList<RiskWarning>>();
        var blocked = new HashSet<string>();
        var remaining = planned;
        for (var round = 0; round < 5 && remaining.Count > 0; round++)
        {
            var warnings = await risk.AssessAsync(remaining.Select(p => new RiskItem(p.PublicationId, p.Destination.Id, p.Connection.Id, p.ScheduledUtc, p.Content,
                p.Description, p.Content.IsAiGenerated)).ToList(), ct);
            foreach (var p in remaining) warningsById[p.PublicationId] = warnings.Where(w => w.PublicationId == p.PublicationId).ToList();
            var newly = remaining.Where(p => PublishRiskAdvisor.IsBlocked(warnings, p.PublicationId)).Select(p => p.PublicationId).ToHashSet();
            if (newly.Count == 0) break;
            blocked.UnionWith(newly);
            remaining = remaining.Where(p => !newly.Contains(p.PublicationId)).ToList();
        }

        var audited = await PlatformFlags.TikTokAuditedAsync(settings, ct);
        var entries = new List<PlanEntry>();
        foreach (var p in planned)
        {
            var w = (warningsById.GetValueOrDefault(p.PublicationId) ?? []).Concat(PlatformWarnings(p, audited)).ToList();
            string outcome;
            if (blocked.Contains(p.PublicationId))
            {
                outcome = PlanOutcomes.Blocked;
                Note(blockedNotes, p.Plan.Campaign.Id, $"\"{p.Content.Title}\" → \"{p.Destination.Title}\" lúc {CampaignSchedules.LocalText(p.Plan.Campaign, p.ScheduledUtc)}: "
                    + string.Join(" ", w.Where(x => x.Level == RiskLevel.Block).Select(x => x.Message)));
            }
            else if (p.Plan.Campaign.SuggestOnly) outcome = PlanOutcomes.Suggested;
            else if (p.Plan.Campaign.Automation == AutomationLevel.ManualApproval || w.Any(x => x.Level == RiskLevel.Warning))
                outcome = PlanOutcomes.AwaitingApproval;
            else outcome = PlanOutcomes.Scheduled;
            entries.Add(new PlanEntry(p, outcome, w));
        }
        return entries;
    }

    /// <summary>
    /// TikTok Direct Post: form mặc định của chiến dịch thiếu/sai → cảnh báo để bài vào chờ duyệt và người dùng hoàn tất form;
    /// chưa audit → cảnh báo chỉ SELF_ONLY.
    /// </summary>
    private static IEnumerable<RiskWarning> PlatformWarnings(PlannedPost p, bool audited)
    {
        var tikTok = PlatformPostSettings.TikTokOf(p.PlatformSettingsJson);
        if (p.Destination.Platform != SocialPlatform.TikTok || !TikTokPostRules.IsDirectPost(tikTok)) yield break;
        if (TikTokPostRules.Validate(tikTok, audited, p.Connection.Scopes) is { Count: > 0 } errors)
            yield return new RiskWarning(p.PublicationId, RiskLevel.Warning, "TIKTOK_FORM_INCOMPLETE",
                "Cần hoàn tất form TikTok Direct Post trước khi duyệt: " + string.Join(" ", errors));
        if (!audited) yield return new RiskWarning(p.PublicationId, RiskLevel.Warning, "TIKTOK_UNAUDITED", TikTokPostRules.UnauditedWarning);
    }

    private static void Note(Dictionary<string, List<string>> notes, string campaignId, string message)
    {
        if (!notes.TryGetValue(campaignId, out var list)) notes[campaignId] = list = [];
        list.Add(message);
    }

    private async Task<int> CommitAsync(PlanResult result, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> deficits,
        Dictionary<string, List<string>> blocked, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var pubs = new List<Publication>();
        var approvals = new List<ActionRequired>();
        foreach (var e in result.Entries.Where(e => e.Outcome != PlanOutcomes.Blocked))
        {
            var p = e.Post;
            var c = p.Plan.Campaign;
            var awaiting = e.Outcome == PlanOutcomes.AwaitingApproval;
            // Chờ duyệt thì không có mốc điều phối: không thể upload cho tới khi được duyệt.
            pubs.Add(new Publication
            {
                Id = p.PublicationId, ContentItemId = p.Content.Id, DestinationId = p.Destination.Id, Title = p.Title, Description = p.Description,
                TagsJson = JsonSerializer.Serialize(p.Tags, PublishingJson.Options), Privacy = p.Privacy, MadeForKids = false,
                ContainsSyntheticMedia = p.Content.IsAiGenerated, ScheduledUtc = p.ScheduledUtc, UseNativeSchedule = false,
                PlatformSettingsJson = p.PlatformSettingsJson,
                Status = awaiting ? PublicationStatus.AwaitingApproval : PublicationStatus.Scheduled,
                IdempotencyKey = $"{p.Destination.Id}:{p.Content.Id}:{p.PublicationId}",
                CampaignId = c.Id, PlanKey = p.PlanKey, Origin = PublicationOrigins.Campaign, OriginRef = c.Id, CreatedUtc = now, UpdatedUtc = now
            });
            if (awaiting)
            {
                var why = e.Warnings.Count == 0 ? "Chiến dịch yêu cầu duyệt từng bài." : string.Join(" ", e.Warnings.Select(w => w.Message));
                approvals.Add(new ActionRequired
                {
                    Id = Guid.NewGuid().ToString("N"), Kind = ActionKinds.PublishAwaitingApproval, RefId = p.PublicationId,
                    Title = $"Duyệt \"{Short(p.Title)}\" → \"{p.Destination.Title}\" ({CampaignSchedules.LocalText(c, p.ScheduledUtc)})",
                    Detail = $"Chiến dịch \"{c.Name}\". {why}", CreatedUtc = now
                });
            }
        }

        if (!await campaigns.CommitPlanAsync(pubs, new Dictionary<string, DateTimeOffset>(), approvals, deficits, now, ct))
        {
            logger.LogWarning("Lượt planner trùng slot với lượt khác; bỏ qua, lần sau tính lại");
            return 0;
        }
        var names = result.Entries.Select(e => e.Post.Plan.Campaign).DistinctBy(c => c.Id).ToDictionary(c => c.Id, c => c.Name);
        foreach (var (campaignId, messages) in blocked)
            await actions.RaiseAsync(ActionKinds.CampaignPlanBlocked, campaignId,
                $"Chiến dịch \"{names.GetValueOrDefault(campaignId) ?? campaignId}\" có bài không được xếp lịch",
                string.Join(" | ", messages.Take(5)) + (messages.Count > 5 ? $" (+{messages.Count - 5} mục khác)" : ""), ct);
        if (pubs.Count > 0)
        {
            await audit.WriteAsync("planner", "campaign.plan", string.Join(',', pubs.Select(p => p.CampaignId).Distinct()),
                $"created={pubs.Count}; awaiting={approvals.Count}", ct);
            logger.LogInformation("Planner tạo {Count} bài ({Awaiting} chờ duyệt)", pubs.Count, approvals.Count);
        }
        return pubs.Count;
    }

    private static string Short(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
