using System.Text.Json.Nodes;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.TikTok;

/// <summary>
/// Thống kê TikTok: POST /v2/video/query/ (scope video.list, tối đa 20 video id/lần) cho view/like/comment/share của từng video,
/// GET /v2/user/info/ (scope user.info.stats) cho follower/likes/video của kênh. Thiếu scope → MissingPermission, không phải 0.
/// Id đưa vào là video id (post id), KHÔNG phải publish_id.
/// https://developers.tiktok.com/doc/tiktok-api-v2-video-query · https://developers.tiktok.com/doc/tiktok-api-v2-get-user-info
/// </summary>
public sealed partial class TikTokConnector : IMetricsConnector
{
    public const int MaxIdsPerVideoQuery = 20;
    private const string VideoSource = "tiktok/v2/video/query";
    private const string UserSource = "tiktok/v2/user/info";
    private static readonly TimeSpan StatsGrace = TimeSpan.FromHours(24);

    private static readonly (string Field, string Key)[] VideoFields =
    [
        ("view_count", MetricKeys.Views), ("like_count", MetricKeys.Likes),
        ("comment_count", MetricKeys.Comments), ("share_count", MetricKeys.Shares)
    ];

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<MetricValue>>> ReadPostMetricsAsync(
        ConnectorSession session, DestinationInfo destination, IReadOnlyList<PostMetricTarget> posts, MetricContext context,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<MetricValue>>();
        var keys = VideoFields.Select(f => f.Key).ToList();
        if (!MetricParsing.HasScope(context.GrantedScopes, TikTokOAuthProvider.VideoList))
        {
            foreach (var post in posts)
                result[post.PublicationId] = [.. MetricParsing.AllUnavailable(keys, MetricStatus.MissingPermission, VideoSource,
                    "Thiếu quyền video.list: bật thống kê TikTok trong Cài đặt rồi kết nối lại tài khoản.")];
            return result;
        }

        foreach (var chunk in MetricParsing.Chunks(posts, MaxIdsPerVideoQuery))
        {
            var body = new JsonObject
            {
                ["filters"] = new JsonObject { ["video_ids"] = new JsonArray([.. chunk.Select(p => (JsonNode)p.RemoteId)]) }
            };
            JsonNode data;
            try
            {
                data = await TikTokApi.PostJsonAsync(session,
                    "/video/query/?fields=" + string.Join(',', ["id", .. VideoFields.Select(f => f.Field)]), body, options.ApiTimeout, ct);
            }
            catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Permission)
            {
                foreach (var post in chunk)
                    result[post.PublicationId] = [.. MetricParsing.AllUnavailable(keys, MetricStatus.MissingPermission, VideoSource, e.Message)];
                continue;
            }

            var byId = (data["videos"]?.AsArray() ?? []).Where(v => ConnectorHttp.Str(v?["id"]) is not null)
                .ToDictionary(v => ConnectorHttp.Str(v!["id"])!, v => v!, StringComparer.Ordinal);
            foreach (var post in chunk)
            {
                if (!byId.TryGetValue(post.RemoteId, out var video))
                {
                    result[post.PublicationId] = [.. MetricParsing.AllUnavailable(keys,
                        MetricParsing.MissingStatus(post.PublishedUtc, nowUtc, StatsGrace), VideoSource,
                        "TikTok không trả video này (đã xóa, riêng tư hoặc không thuộc tài khoản).")];
                    continue;
                }
                result[post.PublicationId] = [.. VideoFields.Select(f => MetricParsing.Long(video[f.Field]) is { } n
                    ? MetricValue.Ok(f.Key, n, VideoSource)
                    : MetricValue.Unavailable(f.Key, MetricStatus.NotAvailable, VideoSource, "TikTok không trả " + f.Field + "."))];
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<MetricValue>> ReadChannelMetricsAsync(
        ConnectorSession session, DestinationInfo destination, MetricContext context, DateTimeOffset nowUtc, CancellationToken ct)
    {
        string[] keys = [MetricKeys.Followers, MetricKeys.ChannelLikes, MetricKeys.VideoCount];
        if (!MetricParsing.HasScope(context.GrantedScopes, TikTokOAuthProvider.UserInfoStats))
            return [.. MetricParsing.AllUnavailable(keys, MetricStatus.MissingPermission, UserSource,
                "Thiếu quyền user.info.stats: bật thống kê TikTok trong Cài đặt rồi kết nối lại tài khoản.")];
        try
        {
            var data = await TikTokApi.GetAsync(session, "/user/info/?fields=follower_count,likes_count,video_count", options.ApiTimeout, ct);
            var user = data["user"];
            MetricValue At(string field, string key) => MetricParsing.Long(user?[field]) is { } n
                ? MetricValue.Ok(key, n, UserSource)
                : MetricValue.Unavailable(key, MetricStatus.NotAvailable, UserSource, "TikTok không trả " + field + ".");
            return [At("follower_count", MetricKeys.Followers), At("likes_count", MetricKeys.ChannelLikes), At("video_count", MetricKeys.VideoCount)];
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Permission)
        {
            return [.. MetricParsing.AllUnavailable(keys, MetricStatus.MissingPermission, UserSource, e.Message)];
        }
    }
}
