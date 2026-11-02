using System.Globalization;
using System.Text.RegularExpressions;
using SuperMOOO.Social.Crawlers;
using SuperMOOO.Social.Platforms;

namespace SuperMOOO.Social.Comments;

public sealed record CommentFetch(string Platform, string ContentType, string Title, IReadOnlyList<RawComment> Comments);

/// <summary>
/// Lấy bình luận: TikTok qua /api/comment/list/ (đã xác nhận path) rồi tikwm (bên thứ ba, không kiểm chứng được);
/// nền tảng khác qua yt-dlp --write-comments. Không lấy trả lời (endpoint reply không xác nhận được).
/// </summary>
[Service]
public sealed partial class CommentCrawler(CrawlHttp http, IYtDlpMetadata ytdlp, BilibiliApi bilibili)
{
    [GeneratedRegex(@"video/(\d+)")] private static partial Regex VideoIdPattern();
    [GeneratedRegex(@"BV[0-9A-Za-z]{10}")] private static partial Regex BvPattern();

    public static string InferContentType(string url, string? platform)
    {
        var u = url.ToLowerInvariant();
        var reel = u.Contains("/reel/") || u.Contains("/reels/");
        return (platform ?? PlatformDetector.Detect(url) ?? "").ToLowerInvariant() switch
        {
            "tiktok" => u.Contains("live") ? "tiktok_live" : reel ? "tiktok_reel" : "tiktok_video",
            "facebook" => reel ? "facebook_reel" : u.Contains("live") ? "facebook_live" : "facebook_video",
            "xiaohongshu" => "xiaohongshu_note",
            "douyin" => "douyin_video",
            "weibo" => "weibo_post",
            "bilibili" => "bilibili_video",
            "kuaishou" => "kuaishou_video",
            var p when p.Length > 0 => $"{p}_video",
            _ => "unknown"
        };
    }

    public async Task<CommentFetch> FetchAsync(string url, int max = 5000, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        url = (url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("URL phải bắt đầu bằng http:// hoặc https://");

        var platform = PlatformDetector.Detect(url) ?? "unknown";
        var type = InferContentType(url, platform);
        progress?.Report(new ProgressInfo(-1, "Đang lấy bình luận…"));

        if (platform == "tiktok" && await TikTokVideoIdAsync(url, ct) is { } videoId)
        {
            var comments = await TikTokCommentsAsync(videoId, max, ct);
            if (comments.Count > 0) return new CommentFetch(platform, type, await TikTokTitleAsync(url, ct), comments);
        }

        // Extractor BiliBili của yt-dlp hay gãy ("Unable to extract initial state"); API bình luận công khai nhận thẳng BV, không cần ký.
        if (platform == "bilibili" && BvPattern().Match(url) is { Success: true } bv)
        {
            var comments = await BilibiliCommentsAsync(bv.Value, max, ct);
            if (comments.Count > 0) return new CommentFetch(platform, type, $"Bilibili {bv.Value}", comments);
        }

        var headers = platform == "bilibili"
            ? new Dictionary<string, string> { ["Referer"] = "https://www.bilibili.com/", ["Cookie"] = "buvid3=INFOC_BILIBILI_DEFAULT_V3; buvid4=INFOC_BILIBILI_DEFAULT_V4" }
            : null;
        var extra = platform == "youtube" ? new[] { "--extractor-args", $"youtube:max_comments={max}" } : null;
        var info = await ytdlp.ExtractAsync(platform, new YtDlpQuery(url, Flat: false, Comments: true, CookiePlatform: platform == "unknown" ? null : platform,
            ExtraArgs: extra, Headers: headers), ct);

        if (info["entries"] is JsonArray entries) info = entries.OfType<JsonObject>().FirstOrDefault()
            ?? throw new ProviderException(platform, ProviderErrorKind.BadResponse, "Danh sách không chứa nội dung có thể lấy bình luận.");

        return new CommentFetch(platform, type, JsonX.Str(info, "title") ?? JsonX.Str(info, "uploader") ?? "Untitled", FromYtDlp(info, max));
    }

    internal static List<RawComment> FromYtDlp(JsonObject info, int max) =>
        JsonX.Objects(info, "comments").Take(max).Select(c =>
        {
            var id = JsonX.Str(c, "author_id");
            return new RawComment(JsonX.Str(c, "author") ?? "Unknown", JsonX.Str(c, "author_url") ?? (id is null ? null : $"https://www.youtube.com/channel/{id}"),
                (JsonX.Str(c, "text") ?? "").Trim(), (int)(JsonX.Long(c, "like_count") ?? 0),
                JsonX.FromUnixSeconds(JsonX.Long(c, "timestamp"))?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? JsonX.Str(c, "time_text"));
        }).Where(c => c.Text.Length > 0).ToList();

    // ---- Bilibili ----

    private async Task<List<RawComment>> BilibiliCommentsAsync(string bvid, int max, CancellationToken ct)
    {
        // Khách chỉ nhận vài bình luận đầu; cookie/SESSDATA đã lưu (Cài đặt > Nền tảng > Bilibili) mới lấy đủ.
        var headers = await bilibili.HeadersAsync(ct);
        var comments = new List<RawComment>();
        for (var page = 1; comments.Count < max; page++)
        {
            JsonNode data;
            try
            {
                if (page > 1) await Task.Delay(TimeSpan.FromSeconds(1), ct);     // tránh HTTP 412 chống crawler khi lật trang liên tục
                data = await http.GetJsonAsync("bilibili", $"https://api.bilibili.com/x/v2/reply?type=1&sort=1&ps=20&pn={page}&oid={bvid}", headers,
                    TimeSpan.FromSeconds(8), ct);
            }
            catch (ProviderException) when (comments.Count > 0)
            {
                break;
            }
            catch (ProviderException)
            {
                return [];      // để yt-dlp thử
            }

            var replies = JsonX.Objects(data, "data", "replies").ToList();
            if (replies.Count == 0) break;
            comments.AddRange(replies.Select(r =>
            {
                var mid = JsonX.Str(r, "member", "mid");
                return new RawComment(JsonX.Str(r, "member", "uname") ?? "Unknown", mid is null ? null : $"https://space.bilibili.com/{mid}",
                    (JsonX.Str(r, "content", "message") ?? "").Trim(), (int)(JsonX.Long(r, "like") ?? 0),
                    JsonX.FromUnixSeconds(JsonX.Long(r, "ctime"))?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            }).Where(c => c.Text.Length > 0));
            if (page * 20 >= (JsonX.Long(data, "data", "page", "count") ?? 0)) break;
        }

        return comments.Take(max).ToList();
    }

    // ---- TikTok ----

    private async Task<string?> TikTokVideoIdAsync(string url, CancellationToken ct)
    {
        if (VideoIdPattern().Match(url) is { Success: true } m) return m.Groups[1].Value;
        try
        {
            var reply = await http.SendAsync("tiktok", HttpMethod.Get, url, timeout: TimeSpan.FromSeconds(5), ct: ct);
            return VideoIdPattern().Match(reply.FinalUri.ToString()) is { Success: true } r ? r.Groups[1].Value : null;
        }
        catch (ProviderException)
        {
            return null;      // link rút gọn không mở được: để yt-dlp thử
        }
    }

    private async Task<List<RawComment>> TikTokCommentsAsync(string videoId, int max, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["Referer"] = $"https://www.tiktok.com/video/{videoId}", ["Accept"] = "application/json" };
        var comments = new List<RawComment>();
        var cursor = 0L;
        try
        {
            while (comments.Count < max)
            {
                var url = "https://www.tiktok.com/api/comment/list/?" + JsonX.Query([new("aweme_id", videoId), new("cursor", cursor.ToString()),
                    new("count", Math.Min(50, max - comments.Count).ToString()), new("aid", "1988")]);
                var page = await http.GetJsonAsync("tiktok", url, headers, TimeSpan.FromSeconds(8), ct);
                var items = JsonX.Objects(page, "comments").ToList();
                if (items.Count == 0) break;
                comments.AddRange(items.Select(ParseTikTokComment));
                var next = JsonX.Long(page, "cursor");
                if (JsonX.Long(page, "has_more") != 1 || next is null || next == cursor) break;
                cursor = next.Value;
            }
        }
        catch (ProviderException) when (comments.Count == 0)
        {
            // Web API cần chữ ký có thể bị từ chối; dùng tikwm (bên thứ ba) làm dự phòng.
        }

        return comments.Count > 0 ? comments.Take(max).ToList() : await TikWmCommentsAsync(videoId, max, ct);
    }

    private async Task<List<RawComment>> TikWmCommentsAsync(string videoId, int max, CancellationToken ct)
    {
        var comments = new List<RawComment>();
        var cursor = 0L;
        while (comments.Count < max)
        {
            JsonNode page;
            try
            {
                page = await http.GetJsonAsync("tiktok",
                    $"https://www.tikwm.com/api/comment/list?url={Uri.EscapeDataString($"https://www.tiktok.com/video/{videoId}")}&count={Math.Min(50, max - comments.Count)}&cursor={cursor}",
                    timeout: TimeSpan.FromSeconds(8), ct: ct);
            }
            catch (ProviderException) when (comments.Count > 0)
            {
                break;
            }

            if (JsonX.Long(page, "code") != 0) break;
            var data = JsonX.At(page, "data");
            var items = JsonX.Objects(data, "comments").ToList();
            if (items.Count == 0) break;
            comments.AddRange(items.Select(ParseTikTokComment));
            var next = JsonX.Long(data, "cursor");
            if (JsonX.At(data, "has_more")?.ToString() is not ("true" or "1") || next is null || next == cursor) break;
            cursor = next.Value;
        }

        return comments.Take(max).ToList();
    }

    internal static RawComment ParseTikTokComment(JsonObject item)
    {
        var user = item["user"];
        var uniqueId = JsonX.Str(user, "unique_id");
        var profile = uniqueId is not null ? $"https://www.tiktok.com/@{uniqueId}"
            : JsonX.Str(user, "sec_uid") is { } sec ? $"https://www.tiktok.com/@{sec}" : null;
        var time = JsonX.Long(item, "create_time");
        return new RawComment(JsonX.Str(user, "nickname") ?? uniqueId ?? "Unknown", profile, (JsonX.Str(item, "text") ?? "").Trim(),
            (int)(JsonX.Long(item, "digg_count") ?? JsonX.Long(item, "likes") ?? 0),
            time is > 0 ? DateTimeOffset.FromUnixTimeSeconds(time.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null);
    }

    private async Task<string> TikTokTitleAsync(string url, CancellationToken ct)
    {
        try
        {
            var data = await http.GetJsonAsync("tiktok", $"https://www.tiktok.com/oembed?url={Uri.EscapeDataString(url)}", timeout: TimeSpan.FromSeconds(5), ct: ct);
            return JsonX.Str(data, "title") ?? JsonX.Str(data, "author_name") ?? "TikTok video";
        }
        catch (ProviderException)
        {
            return "TikTok video";      // tiêu đề chỉ để hiển thị
        }
    }
}
