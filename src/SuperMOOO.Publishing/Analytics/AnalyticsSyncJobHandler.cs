using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Http;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.Publishing.Analytics;

/// <summary>
/// Đọc thống kê của một đích (bài + kênh) rồi ghi snapshot (lane Network, ưu tiên thấp). Đi qua tuyến/rate limit/breaker của tài khoản
/// (<see cref="PublishHttpClients"/>). Bị chặn (quota, ngân sách, breaker, Retry-After, VPN) thì dời job, không ngủ chờ.
/// Thiếu quyền → MissingPermission, token hỏng → TokenExpired (dùng mục ConnectionNeedsReauth có sẵn, không tạo mục cho từng bài).
/// Lỗi tạm thời ghi NotAvailable kèm lý do: lịch đồng bộ tự giãn ra thay vì gọi lại ngay.
/// </summary>
[Service]
public sealed class AnalyticsSyncJobHandler(
    IAnalyticsRepository analytics,
    IPublicationRepository publications,
    IDestinationRepository destinations,
    IConnectionRepository connections,
    ConnectorRegistry registry,
    OAuthTokenService tokens,
    PublishHttpClients http,
    QuotaGuard quota,
    IQuotaLedger ledger,
    IPublishSettingsStore settings,
    IClock clock,
    AnalyticsOptions options,
    ILogger<AnalyticsSyncJobHandler> logger) : IJobHandler
{
    private const int MaxDetailChars = 300;

    public string Type => PublishJobTypes.AnalyticsSync;

    public static AnalyticsJobPayload? Payload(JsonNode? json)
    {
        try { return json?.Deserialize<AnalyticsJobPayload>(PublishingJson.Options); }
        catch (JsonException) { return null; }
    }

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = Payload(context.Payload) ?? throw new InvalidOperationException("Payload job thống kê không hợp lệ.");
        var connection = await connections.GetAsync(payload.ConnectionId, ct);
        var destination = await destinations.GetAsync(payload.DestinationId, ct);
        if (connection is null || destination is null || destination.RemovedUtc is not null) return null;

        var targets = await LoadTargetsAsync(payload, ct);
        var now = clock.UtcNow;
        var analyticsEnabled = await PlatformFlags.YouTubeAnalyticsEnabledAsync(settings, ct);
        var definitions = await analytics.ListDefinitionsAsync(ct);

        if (connection.ApiStatus != ApiStatus.Connected)
        {
            // Mục ConnectionNeedsReauth đã do nơi đánh dấu hỏng tạo: ở đây chỉ ghi lý do vào số liệu, không báo thêm.
            await WriteUnavailableAsync(destination, targets.Pending, payload.IncludeChannel, definitions, analyticsEnabled, MetricStatus.TokenExpired,
                $"Tài khoản \"{connection.DisplayName}\" cần kết nối lại.", now, ct);
            return null;
        }
        if (registry.Find(destination.Platform) is not IMetricsConnector connector)
        {
            await WriteUnavailableAsync(destination, targets.Pending, payload.IncludeChannel, definitions, analyticsEnabled, MetricStatus.NotAvailable,
                "Nền tảng chưa hỗ trợ đọc thống kê.", now, ct);
            return null;
        }

        // TikTok: bài chưa có post id (còn trong hộp thư) thì chưa có số, không gọi API.
        await WriteUnavailableAsync(destination, targets.NoRemoteId, false, definitions, analyticsEnabled, MetricStatus.NotYetAvailable,
            "TikTok chưa trả post id của video (chưa hoàn tất đăng).", now, ct);

        var readable = targets.Pending.Where(p => !targets.NoRemoteId.Contains(p)).ToList();
        if (readable.Count == 0 && !payload.IncludeChannel) return null;

        try
        {
            await ReserveBudgetAsync(connection, destination.Platform, readable.Count + (payload.IncludeChannel ? 1 : 0), now, ct);
            using var lease = await http.AcquireAsync(connection.Id, ct);
            var session = new ConnectorSession(connection.Id, lease.Client, tokens.SourceFor(connection, destination));
            var info = PublicationService.ToInfo(destination);
            var context2 = new MetricContext(connection.Scopes, analyticsEnabled);
            if (readable.Count > 0)
            {
                var map = readable.ToDictionary(p => p.Publication.Id);
                var byPost = await connector.ReadPostMetricsAsync(session, info,
                    [.. readable.Select(p => new PostMetricTarget(p.Publication.Id, p.RemoteId, p.PublishedUtc))], context2, now, ct);
                var snapshots = new List<MetricSnapshot>();
                foreach (var (publicationId, values) in byPost)
                    if (map.TryGetValue(publicationId, out var post))
                        snapshots.AddRange(values.Select(v => ToSnapshot(destination, post.Publication.Id, post.RemoteId, v, now)));
                await analytics.AddSnapshotsAsync(snapshots, ct);
            }
            if (payload.IncludeChannel)
            {
                var values = await connector.ReadChannelMetricsAsync(session, info, context2, now, ct);
                await analytics.AddSnapshotsAsync([.. values.Select(v => ToSnapshot(destination, null, null, v, now))], ct);
            }
        }
        catch (ConnectorException e)
        {
            await HandleErrorAsync(e, connection, destination, readable, payload.IncludeChannel, definitions, analyticsEnabled, now, ct);
        }
        catch (NetworkRequestBlockedException b)
        {
            throw new JobDeferredException(b.RetryAt, b.Message);
        }
        catch (AccountRouteUnavailableException r)
        {
            throw new JobDeferredException(r.RetryAt, r.Message);
        }
        return new JsonObject { ["posts"] = readable.Count, ["channel"] = payload.IncludeChannel };
    }

    private sealed record Target(Publication Publication, string RemoteId, DateTimeOffset PublishedUtc);

    private sealed record Targets(List<Target> Pending, HashSet<Target> NoRemoteId);

    private async Task<Targets> LoadTargetsAsync(AnalyticsJobPayload payload, CancellationToken ct)
    {
        var pending = new List<Target>();
        var missing = new HashSet<Target>();
        foreach (var id in payload.PublicationIds)
        {
            var pub = await publications.GetAsync(id, ct);
            if (pub is null || pub.Status != PublicationStatus.Published || pub.RemoteId is null) continue;
            var published = pub.PublishedUtc ?? pub.UpdatedUtc;
            var remote = payload.Platform == nameof(SocialPlatform.TikTok) ? TikTokVideoId(pub.RemoteUrl) : pub.RemoteId;
            var target = new Target(pub, remote ?? "", published);
            pending.Add(target);
            if (remote is null) missing.Add(target);
        }
        return new Targets(pending, missing);
    }

    /// <summary>TikTok chỉ trả publish_id lúc đăng; id video thật nằm trong URL "…/video/{id}" do bước kiểm tra trạng thái lưu lại.</summary>
    internal static string? TikTokVideoId(string? remoteUrl)
    {
        if (remoteUrl is null || !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.IndexOf(segments, "video");
        return i >= 0 && i + 1 < segments.Length && segments[i + 1].All(char.IsAsciiDigit) ? segments[i + 1] : null;
    }

    /// <summary>
    /// Ngân sách ngày của nền tảng và (YouTube) phần quota Data API chừa cho upload; hết thì dời job. Units YouTube trừ vào bộ đếm chung
    /// của project OAuth (cùng sổ với S11 và upload).
    /// </summary>
    private async Task ReserveBudgetAsync(SocialConnection connection, SocialPlatform platform, int units, DateTimeOffset now, CancellationToken ct)
    {
        if (units <= 0) return;
        var budget = await AnalyticsSettingsStore.BudgetAsync(settings, options, platform, ct);
        var key = AnalyticsSettingsStore.LedgerKey(platform);
        var tomorrow = AnalyticsSettingsStore.DayEnd(now);
        if (!await ledger.TryConsumeAsync("analytics", key, "sync", units, budget, AnalyticsSettingsStore.DayStart(now), tomorrow, ct))
            throw new JobDeferredException(tomorrow, $"Hết ngân sách đồng bộ thống kê {platform} trong ngày ({budget} lượt đọc).");

        if (platform != SocialPlatform.YouTube) return;
        var (used, limit) = await quota.GetUsageAsync(ct);
        if (limit > 0 && used + units > limit - options.YouTubeQuotaReserve)
            throw new JobDeferredException(PacificTime.NextMidnight(now),
                "Chừa quota YouTube cho việc đăng bài: dời đồng bộ thống kê tới lúc quota reset (nửa đêm Pacific).");
        await quota.ConsumeAsync(connection, units, ct, "analytics");
    }

    private async Task HandleErrorAsync(ConnectorException e, SocialConnection connection, SocialDestination destination, List<Target> posts,
        bool channel, IReadOnlyList<MetricDefinition> definitions, bool analyticsEnabled, DateTimeOffset now, CancellationToken ct)
    {
        switch (e.Kind)
        {
            case ConnectorErrorKind.Auth:
                // MarkNeedsReauth chỉ giữ một mục mở cho mỗi kết nối: hàng trăm bài lỗi cũng chỉ một thông báo.
                await tokens.MarkNeedsReauthAsync(connection, e.Message, ct);
                await WriteUnavailableAsync(destination, posts, channel, definitions, analyticsEnabled, MetricStatus.TokenExpired, e.Message, now, ct);
                return;
            case ConnectorErrorKind.QuotaExceeded when destination.Platform == SocialPlatform.YouTube:
                await quota.MarkExhaustedAsync(connection, ct);
                throw new JobDeferredException(PacificTime.NextMidnight(now), e.Message);
            case ConnectorErrorKind.QuotaExceeded:
                throw new JobDeferredException(now + (e.RetryAfter ?? TimeSpan.FromHours(1)), e.Message);
            case ConnectorErrorKind.RateLimited when e.RetryAfter is { } wait:
                throw new JobDeferredException(now + wait, e.Message);
            case ConnectorErrorKind.RateLimited or ConnectorErrorKind.Transient or ConnectorErrorKind.Timeout or ConnectorErrorKind.BadResponse:
                logger.LogWarning("Đồng bộ thống kê {Platform} tạm lỗi: {Error}", destination.Platform, e.Message);
                await WriteUnavailableAsync(destination, posts, channel, definitions, analyticsEnabled, MetricStatus.NotAvailable, "Đồng bộ tạm lỗi: " + e.Message, now, ct);
                return;
            case ConnectorErrorKind.Permission or ConnectorErrorKind.Policy:
                await WriteUnavailableAsync(destination, posts, channel, definitions, analyticsEnabled, MetricStatus.MissingPermission, e.Message, now, ct);
                return;
            default:
                await WriteUnavailableAsync(destination, posts, channel, definitions, analyticsEnabled, MetricStatus.NotAvailable, e.Message, now, ct);
                return;
        }
    }

    /// <summary>Ghi mọi metric kỳ vọng của nền tảng với cùng một trạng thái (không giá trị): người dùng thấy lý do thay vì ô trống hay 0.</summary>
    private async Task WriteUnavailableAsync(SocialDestination destination, IEnumerable<Target> posts, bool channel,
        IReadOnlyList<MetricDefinition> definitions, bool analyticsEnabled, MetricStatus status, string detail, DateTimeOffset now, CancellationToken ct)
    {
        var platform = destination.Platform.ToString();
        bool Expected(MetricDefinition d) => d.Platform == platform
            && (d.RequiredScope != "https://www.googleapis.com/auth/yt-analytics.readonly" || analyticsEnabled);
        var postKeys = definitions.Where(d => d.Scope == MetricScope.Post && Expected(d)).Select(d => d.Key).ToList();
        var channelKeys = definitions.Where(d => d.Scope == MetricScope.Channel && Expected(d)).Select(d => d.Key).ToList();

        var snapshots = new List<MetricSnapshot>();
        foreach (var post in posts)
            snapshots.AddRange(postKeys.Select(k => ToSnapshot(destination, post.Publication.Id, post.RemoteId,
                MetricValue.Unavailable(k, status, "worker", detail), now)));
        if (channel)
            snapshots.AddRange(channelKeys.Select(k => ToSnapshot(destination, null, null, MetricValue.Unavailable(k, status, "worker", detail), now)));
        if (snapshots.Count > 0) await analytics.AddSnapshotsAsync(snapshots, ct);
    }

    private static MetricSnapshot ToSnapshot(SocialDestination destination, string? publicationId, string? remoteId, MetricValue v, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Platform = destination.Platform, DestinationId = destination.Id, PublicationId = publicationId,
        RemotePostId = string.IsNullOrEmpty(remoteId) ? null : remoteId, MetricKey = v.Key, Value = v.Status == MetricStatus.Ok ? v.Value : null,
        CapturedUtc = now, Source = v.Source, Status = v.Status,
        Detail = v.Detail is { Length: > MaxDetailChars } d ? d[..MaxDetailChars] : v.Detail
    };
}

/// <summary>Trước khi chạy job thống kê: breaker của tài khoản đang mở thì dời job (không tốn lượt thử, không chiếm lane).</summary>
[Service(As = [typeof(IJobPreRunCheck)])]
public sealed class AnalyticsPreRunCheck(ICircuitBreaker breaker, IClock clock) : IJobPreRunCheck
{
    public bool AppliesTo(string jobType) => jobType == PublishJobTypes.AnalyticsSync;

    public Task<JobPreRunDecision?> CheckAsync(JobPreRunContext job, CancellationToken ct)
    {
        if (AnalyticsSyncJobHandler.Payload(job.Payload) is not { } payload) return Task.FromResult<JobPreRunDecision?>(null);
        var circuit = breaker.Peek(payload.Platform, payload.ConnectionId);
        return Task.FromResult<JobPreRunDecision?>(circuit.Allowed ? null
            : new JobPreRunDecision(circuit.RetryAt ?? clock.UtcNow + TimeSpan.FromMinutes(5), circuit.Reason ?? "Tài khoản đang tạm ngắt."));
    }
}
