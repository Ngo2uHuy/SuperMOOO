using System.Text.Json.Nodes;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.Meta;

/// <summary>
/// Thống kê Reels/video của Page: GET /{video-id}/video_insights (period=lifetime; cần read_insights + Page token của người có tác vụ ANALYZE)
/// và số cảm xúc/bình luận từ field summary của video. Followers của Page từ followers_count.
/// https://developers.facebook.com/docs/graph-api/reference/video/video_insights/
/// </summary>
public sealed partial class FacebookPageConnector : IMetricsConnector
{
    private const string InsightsSource = "graph/{video-id}/video_insights";
    private const string EngagementSource = "graph/{video-id}?fields=reactions,comments";
    private const string PageSource = "graph/{page-id}?fields=followers_count";
    // Insights của Reels có thể trễ vài giờ sau khi đăng.
    private static readonly TimeSpan InsightsGrace = TimeSpan.FromHours(48);

    /// <summary>Tên metric Graph → khóa SuperMOOO.</summary>
    private static readonly (string Graph, string Key)[] InsightMetrics =
    [
        ("blue_reels_play_count", MetricKeys.Plays),
        ("fb_reels_total_plays", MetricKeys.PlaysWithReplays),
        ("post_impressions_unique", MetricKeys.Reach),
        ("post_video_view_time", MetricKeys.WatchTimeMs),
        ("post_video_avg_time_watched", MetricKeys.AvgWatchTimeMs)
    ];

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<MetricValue>>> ReadPostMetricsAsync(
        ConnectorSession session, DestinationInfo destination, IReadOnlyList<PostMetricTarget> posts, MetricContext context,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<MetricValue>>();
        var canInsights = MetricParsing.HasScope(context.GrantedScopes, MetaOAuthProvider.ReadInsights);
        var canEngagement = MetricParsing.HasScope(context.GrantedScopes, MetaOAuthProvider.PagesReadEngagement);
        foreach (var post in posts)
        {
            var values = new List<MetricValue>();
            values.AddRange(canInsights
                ? await ReadInsightsAsync(session, post, nowUtc, ct)
                : MetricParsing.AllUnavailable(InsightMetrics.Select(m => m.Key), MetricStatus.MissingPermission, InsightsSource,
                    "Thiếu quyền read_insights: bật thống kê Meta trong Cài đặt rồi kết nối lại tài khoản."));
            values.AddRange(canEngagement
                ? await ReadEngagementAsync(session, post, ct)
                : MetricParsing.AllUnavailable([MetricKeys.Reactions, MetricKeys.Comments], MetricStatus.MissingPermission, EngagementSource,
                    "Thiếu quyền pages_read_engagement: kết nối lại tài khoản."));
            result[post.PublicationId] = values;
        }
        return result;
    }

    public async Task<IReadOnlyList<MetricValue>> ReadChannelMetricsAsync(
        ConnectorSession session, DestinationInfo destination, MetricContext context, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (!MetricParsing.HasScope(context.GrantedScopes, MetaOAuthProvider.PagesReadEngagement))
            return [.. MetricParsing.AllUnavailable([MetricKeys.Followers], MetricStatus.MissingPermission, PageSource,
                "Thiếu quyền pages_read_engagement: kết nối lại tài khoản.")];
        try
        {
            var json = await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{Uri.EscapeDataString(destination.RemoteId)}?fields=followers_count",
                options.ApiTimeout, Name, ct);
            return [MetricParsing.Long(json?["followers_count"]) is { } n
                ? MetricValue.Ok(MetricKeys.Followers, n, PageSource)
                : MetricValue.Unavailable(MetricKeys.Followers, MetricStatus.NotAvailable, PageSource, "Page không trả followers_count.")];
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Permission)
        {
            return [MetricValue.Unavailable(MetricKeys.Followers, MetricStatus.MissingPermission, PageSource, e.Message)];
        }
    }

    private async Task<IReadOnlyList<MetricValue>> ReadInsightsAsync(ConnectorSession session, PostMetricTarget post, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var url = $"{MetaGraphApi.Api}/{Uri.EscapeDataString(post.RemoteId)}/video_insights?" + ConnectorHttp.Query(
            ("metric", string.Join(',', InsightMetrics.Select(m => m.Graph))), ("period", "lifetime"));
        JsonNode? json;
        try { json = await MetaGraphApi.GetAsync(session, url, options.ApiTimeout, Name, ct); }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Permission)
        {
            return [.. MetricParsing.AllUnavailable(InsightMetrics.Select(m => m.Key), MetricStatus.MissingPermission, InsightsSource, e.Message)];
        }
        catch (ConnectorException e) when (e.Kind is ConnectorErrorKind.NotFound or ConnectorErrorKind.Validation)
        {
            return [.. MetricParsing.AllUnavailable(InsightMetrics.Select(m => m.Key), MetricStatus.NotAvailable, InsightsSource, e.Message)];
        }

        var byName = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var entry in json?["data"]?.AsArray() ?? [])
            if (ConnectorHttp.Str(entry?["name"]) is { } name && entry is not null) byName[name] = entry;

        var missing = MetricParsing.MissingStatus(post.PublishedUtc, nowUtc, InsightsGrace);
        return [.. InsightMetrics.Select(m =>
            byName.TryGetValue(m.Graph, out var entry) && MetricParsing.Number(entry["values"]?.AsArray().FirstOrDefault()?["value"]) is { } v
                ? MetricValue.Ok(m.Key, v, InsightsSource)
                : MetricValue.Unavailable(m.Key, missing, InsightsSource, "Facebook chưa trả số liệu này cho video."))];
    }

    private async Task<IReadOnlyList<MetricValue>> ReadEngagementAsync(ConnectorSession session, PostMetricTarget post, CancellationToken ct)
    {
        var url = $"{MetaGraphApi.Api}/{Uri.EscapeDataString(post.RemoteId)}?" + ConnectorHttp.Query(
            ("fields", "reactions.summary(total_count).limit(0),comments.summary(total_count).limit(0)"));
        JsonNode? json;
        try { json = await MetaGraphApi.GetAsync(session, url, options.ApiTimeout, Name, ct); }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Permission)
        {
            return [.. MetricParsing.AllUnavailable([MetricKeys.Reactions, MetricKeys.Comments], MetricStatus.MissingPermission, EngagementSource, e.Message)];
        }
        catch (ConnectorException e) when (e.Kind is ConnectorErrorKind.NotFound or ConnectorErrorKind.Validation)
        {
            return [.. MetricParsing.AllUnavailable([MetricKeys.Reactions, MetricKeys.Comments], MetricStatus.NotAvailable, EngagementSource, e.Message)];
        }
        return
        [
            Count(json?["reactions"]?["summary"]?["total_count"], MetricKeys.Reactions),
            Count(json?["comments"]?["summary"]?["total_count"], MetricKeys.Comments)
        ];

        static MetricValue Count(JsonNode? node, string key) => MetricParsing.Long(node) is { } n
            ? MetricValue.Ok(key, n, EngagementSource)
            : MetricValue.Unavailable(key, MetricStatus.NotAvailable, EngagementSource, "Facebook không trả tổng số.");
    }
}
