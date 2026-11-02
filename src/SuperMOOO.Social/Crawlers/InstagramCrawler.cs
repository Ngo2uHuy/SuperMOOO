using System.Text.RegularExpressions;

namespace SuperMOOO.Social.Crawlers;

/// <summary>Instagram: hồ sơ qua web API của instagram.com bằng cookie tài khoản cá nhân đã lưu (yt-dlp không còn đọc được trang hồ sơ); hashtag vẫn qua yt-dlp.</summary>
[Service(As = [typeof(IPlatformCrawler)])]
public sealed partial class InstagramCrawler(IYtDlpMetadata ytdlp, CrawlHttp http, PlatformCredentials credentials)
    : CrawlerBase("instagram", CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Hashtag)
{
    private const string AppId = "936619743392459";
    private const string NeedCookie = "Cần cookie Instagram: đăng nhập instagram.com trên trình duyệt, chép cookie vào Cài đặt > Nền tảng > Instagram > cookie.";
    private const string Limited = "Instagram đang giới hạn tạm thời (quá nhiều yêu cầu): lần quét sau sẽ thử lại, đừng đặt chu kỳ quét quá dày.";
    private const int PageSize = 12;
    private const int MaxPages = 10;

    // Tài khoản cá nhân dễ bị khoá khi gọi dồn: mọi yêu cầu API (kể cả của các luồng khác nhau) cách nhau tối thiểu.
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(4);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    [GeneratedRegex(@"^[A-Za-z0-9._]{1,30}$")]
    private static partial Regex UserName();

    [GeneratedRegex(@"(?:^|;\s*)csrftoken=([^;]+)")]
    private static partial Regex Csrf();

    protected override async Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct)
    {
        var target = request.Target.Trim();
        if (request.Kind == CrawlKind.Hashtag)
        {
            var url = target.Contains("://") ? target : $"https://www.instagram.com/explore/tags/{target.TrimStart('#')}/";
            var info = await ytdlp.ExtractAsync("instagram", new YtDlpQuery(url, request.MaxItems, CookiePlatform: "instagram"), ct);
            return YtDlpListing.ToResult("instagram", request.Kind, info, id => $"https://www.instagram.com/p/{id}", request.MaxItems, "profile");
        }

        var user = ExtractUser(target);
        var cookie = await credentials.CookieAsync("instagram", ct) ?? throw new ProviderException("instagram", ProviderErrorKind.KeyMissing, NeedCookie);
        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = cookie, ["x-ig-app-id"] = AppId, ["x-requested-with"] = "XMLHttpRequest",
            ["Referer"] = $"https://www.instagram.com/{user}/", ["Accept"] = "*/*"
        };
        if (Csrf().Match(cookie) is { Success: true } csrf) headers["x-csrftoken"] = csrf.Groups[1].Value;

        var profile = await GetAsync($"https://www.instagram.com/api/v1/users/web_profile_info/?username={Uri.EscapeDataString(user)}", headers, ct);
        var owner = JsonX.At(profile, "data", "user") as JsonObject
            ?? throw new ProviderException("instagram", ProviderErrorKind.BadResponse, $"Không tìm thấy hồ sơ Instagram \"{user}\" hoặc hồ sơ riêng tư.");
        var userId = JsonX.Str(owner, "id");
        var ownerName = JsonX.Str(owner, "full_name") ?? JsonX.Str(owner, "username") ?? user;
        var items = new List<ContentItem>();
        foreach (var edge in JsonX.Objects(owner, "edge_owner_to_timeline_media", "edges"))
            if (JsonX.At(edge, "node") is JsonObject node && FromWebNode(node, ownerName) is { } item) items.Add(item);

        // Trang đầu của web_profile_info chỉ có 12 bài; muốn nhiều hơn thì phân trang bằng feed/user.
        var more = JsonX.At(owner, "edge_owner_to_timeline_media", "page_info", "has_next_page") is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        // end_cursor của GraphQL không dùng được làm max_id của feed/user: trang đầu của feed lặp lại 12 bài đã có (khử trùng bên dưới).
        string? maxId = null;
        for (var page = 0; more && userId is not null && items.Count < request.MaxItems && page < MaxPages; page++)
        {
            var url = $"https://www.instagram.com/api/v1/feed/user/{userId}/?count={PageSize}" + (maxId is null ? "" : $"&max_id={Uri.EscapeDataString(maxId)}");
            var feed = await GetAsync(url, headers, ct);
            foreach (var node in JsonX.Objects(feed, "items"))
                if (FromFeedItem(node, ownerName) is { } item) items.Add(item);
            more = JsonX.At(feed, "more_available") is JsonValue m && m.TryGetValue<bool>(out var ma) && ma;
            maxId = JsonX.Str(feed, "next_max_id");
            if (maxId is null) break;
        }

        var ownerItem = new ContentItem("instagram", "profile", userId ?? user, ownerName, userId, ownerName, $"https://www.instagram.com/{user}/",
            ViewCount: JsonX.Long(owner, "edge_followed_by", "count"));
        var videos = items.GroupBy(x => x.ExternalId).Select(g => g.First()).Take(request.MaxItems).ToList();
        if (videos.Count == 0) throw new ProviderException("instagram", ProviderErrorKind.BadResponse, "Hồ sơ không có video/reel nào trong các bài gần nhất.");
        return new CrawlResult("instagram", request.Kind, ownerItem, videos);
    }

    internal static string ExtractUser(string target)
    {
        var value = target.Trim();
        if (value.Contains("://"))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new ArgumentException("Liên kết Instagram không hợp lệ.");
            value = uri.AbsolutePath.Trim('/').Split('/')[0];
        }

        value = value.TrimStart('@', '/').TrimEnd('/');
        if (!UserName().IsMatch(value)) throw new ArgumentException("Nhập tên người dùng hoặc liên kết hồ sơ Instagram (instagram.com/<tên>).");
        return value;
    }

    private async Task<JsonNode> GetAsync(string url, Dictionary<string, string> headers, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var wait = _lastCall + MinGap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            try
            {
                return await http.GetJsonAsync("instagram", url, headers, ct: ct);
            }
            finally
            {
                _lastCall = DateTime.UtcNow;
            }
        }
        catch (ProviderException ex)
        {
            throw MapError(ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static ProviderException MapError(ProviderException ex)
    {
        var text = ex.Message.ToLowerInvariant();
        if (ex.Kind == ProviderErrorKind.RateLimited || text.Contains("please wait a few minutes") || text.Contains("rate limit"))
            return new ProviderException("instagram", ProviderErrorKind.RateLimited, Limited);
        if (ex.Kind is ProviderErrorKind.KeyInvalid or ProviderErrorKind.Forbidden || text.Contains("login_required") || text.Contains("checkpoint")
            || text.Contains("consent_required") || text.Contains("không phải json"))
            return new ProviderException("instagram", ProviderErrorKind.KeyInvalid, NeedCookie + " Nếu đã có cookie, có thể nó hết hạn hoặc Instagram yêu cầu xác minh tài khoản trên trình duyệt.");
        return ex;
    }

    private static ContentItem? FromWebNode(JsonObject node, string ownerName)
    {
        if (JsonX.At(node, "is_video") is not JsonValue v || !v.TryGetValue<bool>(out var video) || !video) return null;
        var code = JsonX.Str(node, "shortcode");
        if (code is null) return null;
        var duration = JsonX.Double(node, "video_duration");
        return new ContentItem("instagram", "video", code, Caption(node),
            AuthorName: ownerName, Url: PostUrl(code, JsonX.Str(node, "product_type")), ThumbnailUrl: JsonX.Str(node, "thumbnail_src") ?? JsonX.Str(node, "display_url"),
            Content: Caption(node), PublishedAt: JsonX.FromUnixSeconds(JsonX.Long(node, "taken_at_timestamp")),
            Duration: duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : null, ViewCount: JsonX.Long(node, "video_view_count"),
            LikeCount: JsonX.Long(node, "edge_liked_by", "count"), CommentCount: JsonX.Long(node, "edge_media_to_comment", "count"));
    }

    private static string? Caption(JsonObject node) =>
        JsonX.Objects(node, "edge_media_to_caption", "edges").Select(e => JsonX.Str(e, "node", "text")).FirstOrDefault(t => t is not null);

    private static ContentItem? FromFeedItem(JsonObject item, string ownerName)
    {
        // media_type 2 = video/reel; 1 = ảnh, 8 = album.
        if (JsonX.Long(item, "media_type") != 2) return null;
        var code = JsonX.Str(item, "code");
        if (code is null) return null;
        var caption = JsonX.Str(item, "caption", "text");
        var duration = JsonX.Double(item, "video_duration");
        return new ContentItem("instagram", "video", code, caption, AuthorName: ownerName, Url: PostUrl(code, JsonX.Str(item, "product_type")),
            ThumbnailUrl: JsonX.Str(JsonX.Objects(item, "image_versions2", "candidates").FirstOrDefault(), "url"), Content: caption,
            PublishedAt: JsonX.FromUnixSeconds(JsonX.Long(item, "taken_at")), Duration: duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : null,
            ViewCount: JsonX.Long(item, "play_count") ?? JsonX.Long(item, "view_count"), LikeCount: JsonX.Long(item, "like_count"),
            CommentCount: JsonX.Long(item, "comment_count"));
    }

    private static string PostUrl(string code, string? productType) =>
        $"https://www.instagram.com/{(productType == "clips" ? "reel" : "p")}/{code}/";
}
