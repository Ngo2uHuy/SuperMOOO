using System.Globalization;
using System.Text.Json.Nodes;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.YouTube;

/// <summary>
/// Thống kê YouTube: videos.list part=statistics (1 unit/lần gọi, tối đa 50 id; đủ với youtube.readonly) và YouTube Analytics API
/// reports.query (watch time, chia sẻ; cần scope yt-analytics.readonly, người dùng bật trong Cài đặt rồi kết nối lại).
/// https://developers.google.com/youtube/v3/docs/videos/list · https://developers.google.com/youtube/analytics/reference/reports/query
/// </summary>
public sealed partial class YouTubeConnector : IMetricsConnector
{
    public const int MaxIdsPerVideosList = 50;
    public const string AnalyticsApi = "https://youtubeanalytics.googleapis.com/v2/reports";
    private const string VideosSource = "youtube/v3/videos?part=statistics";
    private const string ChannelsSource = "youtube/v3/channels?part=statistics";
    private const string AnalyticsSource = "youtubeanalytics/v2/reports";
    private static readonly string[] AnalyticsKeys = [MetricKeys.WatchTimeMinutes, MetricKeys.AvgViewDurationSec, MetricKeys.Shares];
    /// <summary>Số liệu Analytics có độ trễ vài ngày: bài mới chưa có hàng nào là bình thường.</summary>
    private static readonly TimeSpan AnalyticsGrace = TimeSpan.FromDays(3);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<MetricValue>>> ReadPostMetricsAsync(
        ConnectorSession session, DestinationInfo destination, IReadOnlyList<PostMetricTarget> posts, MetricContext context,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        var result = new Dictionary<string, List<MetricValue>>();
        foreach (var chunk in MetricParsing.Chunks(posts, MaxIdsPerVideosList))
        {
            var ids = string.Join(',', chunk.Select(p => Uri.EscapeDataString(p.RemoteId)));
            var json = await GetJsonAsync(session, $"{Api}/videos?part=statistics&id={ids}", ct);
            var byId = (json?["items"]?.AsArray() ?? []).Where(i => ConnectorHttp.Str(i?["id"]) is not null)
                .ToDictionary(i => ConnectorHttp.Str(i!["id"])!, i => i!, StringComparer.Ordinal);
            foreach (var post in chunk)
            {
                var values = result[post.PublicationId] = [];
                if (!byId.TryGetValue(post.RemoteId, out var item))
                {
                    values.AddRange(MetricParsing.AllUnavailable([MetricKeys.Views, MetricKeys.Likes, MetricKeys.Comments],
                        MetricStatus.NotAvailable, VideosSource, "Không tìm thấy video (đã xóa hoặc không truy cập được)."));
                    continue;
                }
                var stats = item["statistics"];
                values.Add(Stat(stats, "viewCount", MetricKeys.Views, "Nền tảng không trả số lượt xem."));
                values.Add(Stat(stats, "likeCount", MetricKeys.Likes, "Chủ video đã tắt hiển thị lượt thích."));
                values.Add(Stat(stats, "commentCount", MetricKeys.Comments, "Bình luận bị tắt."));
            }
        }

        if (context.AnalyticsEnabled)
            foreach (var post in posts)
                result[post.PublicationId].AddRange(await ReadAnalyticsAsync(session, post, context, nowUtc, ct));

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<MetricValue>)kv.Value);
    }

    public async Task<IReadOnlyList<MetricValue>> ReadChannelMetricsAsync(
        ConnectorSession session, DestinationInfo destination, MetricContext context, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var json = await GetJsonAsync(session, $"{Api}/channels?part=statistics&id={Uri.EscapeDataString(destination.RemoteId)}", ct);
        var stats = json?["items"]?.AsArray().FirstOrDefault()?["statistics"];
        if (stats is null)
            return [.. MetricParsing.AllUnavailable([MetricKeys.Subscribers, MetricKeys.ChannelViews, MetricKeys.VideoCount],
                MetricStatus.NotAvailable, ChannelsSource, "Không đọc được thống kê kênh.")];

        var subscribers = stats["hiddenSubscriberCount"] is JsonValue v && v.TryGetValue<bool>(out var hidden) && hidden
            ? MetricValue.Unavailable(MetricKeys.Subscribers, MetricStatus.NotAvailable, ChannelsSource, "Kênh ẩn số người đăng ký.")
            : Stat(stats, "subscriberCount", MetricKeys.Subscribers, "Nền tảng không trả số người đăng ký.", ChannelsSource);
        return
        [
            subscribers,
            Stat(stats, "viewCount", MetricKeys.ChannelViews, "Nền tảng không trả tổng lượt xem.", ChannelsSource),
            Stat(stats, "videoCount", MetricKeys.VideoCount, "Nền tảng không trả số video.", ChannelsSource)
        ];
    }

    private static MetricValue Stat(JsonNode? stats, string field, string key, string missing, string source = VideosSource) =>
        MetricParsing.Long(stats?[field]) is { } n
            ? MetricValue.Ok(key, n, source)
            : MetricValue.Unavailable(key, MetricStatus.NotAvailable, source, missing);

    private async Task<IReadOnlyList<MetricValue>> ReadAnalyticsAsync(
        ConnectorSession session, PostMetricTarget post, MetricContext context, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (!MetricParsing.HasScope(context.GrantedScopes, GoogleOAuthProvider.AnalyticsScope))
            return [.. MetricParsing.AllUnavailable(AnalyticsKeys, MetricStatus.MissingPermission, AnalyticsSource,
                "Thiếu quyền yt-analytics.readonly: bật \"YouTube Analytics\" trong Cài đặt rồi kết nối lại tài khoản.")];

        var start = post.PublishedUtc.UtcDateTime.Date;
        var end = nowUtc.UtcDateTime.Date;
        if (end < start) end = start;
        var url = AnalyticsApi + "?" + ConnectorHttp.Query(
            ("ids", "channel==MINE"), ("metrics", "estimatedMinutesWatched,averageViewDuration,shares"),
            ("startDate", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("endDate", end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("filters", "video==" + post.RemoteId));
        JsonNode? json;
        try { json = await GetJsonAsync(session, url, ct); }
        catch (ConnectorException e) when (e.Kind is ConnectorErrorKind.Permission)
        {
            return [.. MetricParsing.AllUnavailable(AnalyticsKeys, MetricStatus.MissingPermission, AnalyticsSource, e.Message)];
        }
        catch (ConnectorException e) when (e.Kind is ConnectorErrorKind.Validation or ConnectorErrorKind.NotFound)
        {
            return [.. MetricParsing.AllUnavailable(AnalyticsKeys, MetricStatus.NotAvailable, AnalyticsSource, e.Message)];
        }

        var row = json?["rows"]?.AsArray().FirstOrDefault()?.AsArray();
        if (row is null)
            return [.. MetricParsing.AllUnavailable(AnalyticsKeys, MetricParsing.MissingStatus(post.PublishedUtc, nowUtc, AnalyticsGrace),
                AnalyticsSource, "YouTube Analytics chưa có dữ liệu cho video này.")];

        // Cột theo thứ tự metrics của yêu cầu; đọc theo tên cột để không phụ thuộc thứ tự nếu YouTube đổi.
        var names = (json?["columnHeaders"]?.AsArray() ?? []).Select(h => ConnectorHttp.Str(h?["name"])).ToList();
        MetricValue At(string name, string key)
        {
            var i = names.IndexOf(name);
            return i >= 0 && i < row.Count && MetricParsing.Number(row[i]) is { } d
                ? MetricValue.Ok(key, d, AnalyticsSource)
                : MetricValue.Unavailable(key, MetricStatus.NotAvailable, AnalyticsSource, "Phản hồi thiếu cột " + name + ".");
        }
        return
        [
            At("estimatedMinutesWatched", MetricKeys.WatchTimeMinutes),
            At("averageViewDuration", MetricKeys.AvgViewDurationSec),
            At("shares", MetricKeys.Shares)
        ];
    }
}
