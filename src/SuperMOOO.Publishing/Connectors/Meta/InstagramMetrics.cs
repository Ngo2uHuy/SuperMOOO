using System.Text.Json.Nodes;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.Meta;

/// <summary>
/// Thống kê Reels Instagram: GET /{ig-media-id}/insights (period luôn lifetime; cần instagram_manage_insights + pages_read_engagement
/// khi đăng nhập qua Facebook). Không có số liệu → API trả data rỗng (không phải 0); số liệu có thể trễ tới 48 giờ.
/// https://developers.facebook.com/docs/instagram-platform/reference/instagram-media/insights
/// </summary>
public sealed partial class InstagramConnector : IMetricsConnector
{
    private const string InsightsSource = "graph/{ig-media-id}/insights";
    private const string UserSource = "graph/{ig-user-id}?fields=followers_count";
    private static readonly TimeSpan InsightsGrace = TimeSpan.FromHours(48);

    private static readonly (string Graph, string Key)[] InsightMetrics =
    [
        ("views", MetricKeys.Views), ("reach", MetricKeys.Reach), ("likes", MetricKeys.Likes), ("comments", MetricKeys.Comments),
        ("shares", MetricKeys.Shares), ("saved", MetricKeys.Saved), ("total_interactions", MetricKeys.TotalInteractions),
        ("ig_reels_avg_watch_time", MetricKeys.AvgWatchTimeMs)
    ];

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<MetricValue>>> ReadPostMetricsAsync(
        ConnectorSession session, DestinationInfo destination, IReadOnlyList<PostMetricTarget> posts, MetricContext context,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<MetricValue>>();
        var allowed = MetricParsing.HasScope(context.GrantedScopes, MetaOAuthProvider.InstagramManageInsights);
        foreach (var post in posts)
            result[post.PublicationId] = allowed
                ? await ReadInsightsAsync(session, post, nowUtc, ct)
                : [.. MetricParsing.AllUnavailable(InsightMetrics.Select(m => m.Key), MetricStatus.MissingPermission, InsightsSource,
                    "Thiếu quyền instagram_manage_insights: bật thống kê Meta trong Cài đặt rồi kết nối lại tài khoản.")];
        return result;
    }

    public async Task<IReadOnlyList<MetricValue>> ReadChannelMetricsAsync(
        ConnectorSession session, DestinationInfo destination, MetricContext context, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (!MetricParsing.HasScope(context.GrantedScopes, MetaOAuthProvider.InstagramBasic))
            return [.. MetricParsing.AllUnavailable([MetricKeys.Followers], MetricStatus.MissingPermission, UserSource,
                "Thiếu quyền instagram_basic: kết nối lại tài khoản.")];
        try
        {
            var json = await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{Uri.EscapeDataString(destination.RemoteId)}?fields=followers_count",
                options.ApiTimeout, Name, ct);
            return [MetricParsing.Long(json?["followers_count"]) is { } n
                ? MetricValue.Ok(MetricKeys.Followers, n, UserSource)
                : MetricValue.Unavailable(MetricKeys.Followers, MetricStatus.NotAvailable, UserSource, "Instagram không trả followers_count.")];
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Permission)
        {
            return [MetricValue.Unavailable(MetricKeys.Followers, MetricStatus.MissingPermission, UserSource, e.Message)];
        }
    }

    private async Task<IReadOnlyList<MetricValue>> ReadInsightsAsync(ConnectorSession session, PostMetricTarget post, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var missing = MetricParsing.MissingStatus(post.PublishedUtc, nowUtc, InsightsGrace);
        var found = new Dictionary<string, double>(StringComparer.Ordinal);
        string? failure = null;
        try { Collect(await GetInsightsAsync(session, post.RemoteId, string.Join(',', InsightMetrics.Select(m => m.Graph)), ct), found); }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Validation)
        {
            // Một metric không hợp lệ làm hỏng cả yêu cầu: thử từng metric để không mất các số còn lại.
            foreach (var (graph, _) in InsightMetrics)
                try { Collect(await GetInsightsAsync(session, post.RemoteId, graph, ct), found); }
                catch (ConnectorException inner) when (inner.Kind == ConnectorErrorKind.Validation) { failure = inner.Message; }
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Permission)
        {
            return [.. MetricParsing.AllUnavailable(InsightMetrics.Select(m => m.Key), MetricStatus.MissingPermission, InsightsSource, e.Message)];
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.NotFound)
        {
            return [.. MetricParsing.AllUnavailable(InsightMetrics.Select(m => m.Key), MetricStatus.NotAvailable, InsightsSource, e.Message)];
        }

        return [.. InsightMetrics.Select(m => found.TryGetValue(m.Graph, out var v)
            ? MetricValue.Ok(m.Key, v, InsightsSource)
            : MetricValue.Unavailable(m.Key, missing, InsightsSource, failure ?? "Instagram chưa trả số liệu này (data rỗng)."))];
    }

    private async Task<JsonNode?> GetInsightsAsync(ConnectorSession session, string mediaId, string metrics, CancellationToken ct) =>
        await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{Uri.EscapeDataString(mediaId)}/insights?" + ConnectorHttp.Query(("metric", metrics)),
            options.ApiTimeout, Name, ct);

    private static void Collect(JsonNode? json, Dictionary<string, double> into)
    {
        foreach (var entry in json?["data"]?.AsArray() ?? [])
            if (ConnectorHttp.Str(entry?["name"]) is { } name
                && MetricParsing.Number(entry?["values"]?.AsArray().FirstOrDefault()?["value"] ?? entry?["total_value"]?["value"]) is { } v)
                into[name] = v;
    }
}
