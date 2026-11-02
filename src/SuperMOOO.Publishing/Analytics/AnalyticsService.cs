using SuperMOOO.Publishing.Contracts;

namespace SuperMOOO.Publishing.Analytics;

/// <param name="Views">Metric lượt xem chính của nền tảng (YouTube/Instagram/TikTok: views; Facebook: plays); null = chưa có số Ok.</param>
public sealed record TopPostDto(
    string PublicationId, string Title, string DestinationTitle, string Platform, DateTimeOffset PublishedUtc, double? Views, double? Likes,
    double? Comments, double? Shares, DateTimeOffset? CapturedUtc);

public sealed record BudgetUsageDto(string Platform, int Used, int Budget);

/// <summary>Use case của tab "Hiệu quả" (S14): báo cáo, top bài, giờ đăng tốt, sự kiện sức khỏe, đồng bộ ngay.</summary>
[Service]
public sealed class AnalyticsService(
    IAnalyticsRepository analytics,
    AnalyticsPlanner planner,
    BestTimeService bestTimes,
    IPublishSettingsStore settings,
    IQuotaLedger ledger,
    IAuditLog audit,
    IClock clock,
    AnalyticsOptions options)
{
    public const int MaxPeriodDays = 400;
    private static readonly SocialPlatform[] BudgetPlatforms = [SocialPlatform.YouTube, SocialPlatform.Facebook, SocialPlatform.Instagram, SocialPlatform.TikTok];

    public async Task<AnalyticsReportDto> GetReportAsync(AnalyticsReportRequest r, CancellationToken ct)
    {
        Validate(r.FromUtc, r.ToUtc);
        if (r.GroupBy is not (AnalyticsGroupBy.Post or AnalyticsGroupBy.Channel or AnalyticsGroupBy.Platform or AnalyticsGroupBy.Campaign or AnalyticsGroupBy.Source))
            throw Invalid("Nhóm báo cáo không hợp lệ.");
        var platform = ParsePlatform(r.Platform);

        var definitions = await analytics.ListDefinitionsAsync(ct);
        var posts = await analytics.ListPostsAsync(new PostFilter(platform, r.DestinationId, r.CampaignId, r.SourceId, r.PublicationId, r.ToUtc), ct);
        var destinations = (await analytics.ListDestinationsAsync(ct))
            .Where(d => !d.Removed && (platform is null || d.Platform == platform) && (r.DestinationId is null || d.DestinationId == r.DestinationId)).ToList();
        var length = r.ToUtc - r.FromUtc;
        var points = await analytics.ListPointsAsync([.. destinations.Select(d => d.DestinationId)], [.. posts.Select(p => p.PublicationId)],
            r.FromUtc - length, r.ToUtc, ct);
        return AnalyticsReportBuilder.Build(r, definitions, posts, destinations, points, clock.UtcNow);
    }

    /// <summary>Bài đăng trong kỳ, xếp theo lượt xem chính giảm dần (bài chưa có số xếp cuối), phân trang.</summary>
    public async Task<PagedDto<TopPostDto>> TopPostsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, string? platformName, string? destinationId,
        int page, int pageSize, CancellationToken ct, string? campaignId = null, string? sourceId = null)
    {
        Validate(fromUtc, toUtc);
        var platform = ParsePlatform(platformName);
        pageSize = Math.Clamp(pageSize, 1, PublishingJson.MaxPageSize);
        page = Math.Max(1, page);

        var posts = (await analytics.ListPostsAsync(new PostFilter(platform, destinationId, campaignId, sourceId, null, toUtc), ct))
            .Where(p => p.PublishedUtc >= fromUtc).ToList();
        var points = posts.Count == 0 ? []
            : await analytics.ListPointsAsync([.. posts.Select(p => p.DestinationId).Distinct()], [.. posts.Select(p => p.PublicationId)],
                posts.Min(p => p.PublishedUtc), toUtc, ct);
        var latest = points.Where(p => p.PublicationId is not null && p.Status == MetricStatus.Ok)
            .GroupBy(p => (p.PublicationId!, p.MetricKey)).ToDictionary(g => g.Key, g => g.MaxBy(p => p.CapturedUtc)!);

        var rows = posts.Select(p =>
        {
            MetricPoint? Get(string key) => latest.GetValueOrDefault((p.PublicationId, key));
            var views = Get(MetricKeys.PrimaryView(p.Platform));
            return new TopPostDto(p.PublicationId, p.Title, p.DestinationTitle, p.Platform.ToString(), p.PublishedUtc, views?.Value,
                Get(MetricKeys.Likes)?.Value ?? Get(MetricKeys.Reactions)?.Value, Get(MetricKeys.Comments)?.Value, Get(MetricKeys.Shares)?.Value, views?.CapturedUtc);
        }).OrderByDescending(r => r.Views.HasValue).ThenByDescending(r => r.Views).ThenByDescending(r => r.PublishedUtc).ToList();
        return new PagedDto<TopPostDto>([.. rows.Skip((page - 1) * pageSize).Take(pageSize)], rows.Count, page, pageSize);
    }

    public Task<BestTimesDto> GetBestTimesAsync(GetBestTimesRequest r, CancellationToken ct) => bestTimes.GetAsync(r, ct);

    public async Task<List<HealthEventDto>> ListHealthEventsAsync(int take, CancellationToken ct) =>
        [.. (await analytics.ListHealthEventsAsync(Math.Clamp(take, 1, 200), ct)).Select(x => new HealthEventDto(
            x.Event.Id, x.Event.ConnectionId, x.Event.DestinationId, x.DestinationTitle, x.Event.Kind, x.Event.Detail, x.Event.Observed,
            x.Event.Baseline, x.Event.ZScore, x.Event.CampaignsPaused, x.Event.CreatedUtc))];

    /// <summary>Số lượt đọc đã dùng / ngân sách hôm nay (ngày UTC) của từng nền tảng.</summary>
    public async Task<List<BudgetUsageDto>> BudgetUsageAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var result = new List<BudgetUsageDto>();
        foreach (var platform in BudgetPlatforms)
            result.Add(new BudgetUsageDto(platform.ToString(),
                await ledger.GetUsedAsync(AnalyticsSettingsStore.LedgerKey(platform), AnalyticsSettingsStore.DayStart(now), AnalyticsSettingsStore.DayEnd(now), ct),
                await AnalyticsSettingsStore.BudgetAsync(settings, options, platform, ct)));
        return result;
    }

    public async Task<AnalyticsSyncResultDto> RunSyncAsync(CancellationToken ct)
    {
        var result = await planner.RunAsync(ct);
        await audit.AddAsync("ui", "analytics.sync", null, "ok", result.JobsQueued, ct);
        return result;
    }

    private static void Validate(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) throw Invalid("Kỳ báo cáo không hợp lệ: ngày kết thúc phải sau ngày bắt đầu.");
        if (to - from > TimeSpan.FromDays(MaxPeriodDays)) throw Invalid($"Kỳ báo cáo tối đa {MaxPeriodDays} ngày.");
    }

    private static SocialPlatform? ParsePlatform(string? name) =>
        name is null ? null : Enum.TryParse<SocialPlatform>(name, true, out var p) && Enum.IsDefined(p) ? p : throw Invalid("Nền tảng không hợp lệ.");

    private static PublishingException Invalid(string message) => new(PublishingErrors.InvalidRequest, message);
}
