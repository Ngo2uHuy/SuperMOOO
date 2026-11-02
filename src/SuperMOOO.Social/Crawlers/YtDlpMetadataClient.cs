using SuperMOOO.Infrastructure.Network;

namespace SuperMOOO.Social.Crawlers;

public sealed record YtDlpQuery(string Url, int MaxItems = 0, bool Flat = true, bool Comments = false, string? CookiePlatform = null,
    IReadOnlyList<string>? ExtraArgs = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>Lấy metadata bằng yt-dlp (-J). Tách interface để test dùng JSON ghi sẵn thay vì chạy tiến trình thật.</summary>
public interface IYtDlpMetadata
{
    Task<JsonObject> ExtractAsync(string platform, YtDlpQuery query, CancellationToken ct);
}

[Service(As = [typeof(IYtDlpMetadata)])]
public sealed class YtDlpMetadataClient(IProcessRunner runner, IToolLocator tools, INetworkRouter router, PlatformCredentials credentials,
    MachineRouteLock routeLock)
    : IYtDlpMetadata
{
    private sealed class Collector(List<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines) lines.Add(value);
        }
    }

    public async Task<JsonObject> ExtractAsync(string platform, YtDlpQuery query, CancellationToken ct)
    {
        string exe;
        try
        {
            exe = tools.Resolve(ToolKind.YtDlp);
        }
        catch (FileNotFoundException ex)
        {
            throw new ProviderException(platform, ProviderErrorKind.KeyMissing, ex.Message);
        }

        var args = new List<string> { "-J", "--no-warnings", "--ignore-errors", "--encoding", "utf-8" };
        if (query.Flat) args.Add("--flat-playlist");
        if (query.MaxItems > 0) args.AddRange(["--playlist-end", query.MaxItems.ToString()]);
        if (query.Comments) args.Add("--write-comments");
        if ((CrawlRoute.Current is { } lease ? lease.ToolsProxy : router.ProxyUriForTools()) is { } proxy) args.AddRange(["--proxy", proxy]);

        var headers = new Dictionary<string, string>(query.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (query.CookiePlatform is not null && !headers.ContainsKey("Cookie") && await credentials.CookieAsync(query.CookiePlatform, ct) is { } cookie)
            headers["Cookie"] = cookie;
        foreach (var (name, value) in headers) args.AddRange(["--add-headers", $"{name}:{value}"]);
        if (query.ExtraArgs is not null) args.AddRange(query.ExtraArgs);
        args.Add(query.Url);

        var lines = new List<string>();
        ProcessResult result;
        // Cookie tài khoản đi tuyến chung: chờ VPN cả máy của tài khoản khác tắt rồi mới chạy (xem CrawlHttp).
        await using (CrawlRoute.Current is null && CrawlHttp.HasIdentity(headers) ? await CrawlHttp.EnterSharedAsync(routeLock, platform, ct) : null)
            result = await runner.RunAsync(new ProcessSpec(exe, args), new Collector(lines), ct);

        string[] snapshot;
        lock (lines) snapshot = [.. lines];
        var json = snapshot.LastOrDefault(l => l.StartsWith('{'));
        if (json is not null && JsonNode.Parse(json) is JsonObject info) return info;

        var error = snapshot.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? $"yt-dlp thoát mã {result.ExitCode}";
        throw MapError(platform, error, query.CookiePlatform is not null && headers.ContainsKey("Cookie"));
    }

    private static readonly HashSet<string> LoginGated = ["facebook", "instagram", "douyin", "xiaohongshu", "weibo", "kuaishou"];

    /// <summary>Các nền tảng cần đăng nhập thường báo bằng lỗi trích xuất chung ("Unsupported URL", "Unable to extract"); đổi thành lỗi có kiểu, chỉ rõ cách xử lý.</summary>
    public static ProviderException MapError(string platform, string error, bool hadCookie)
    {
        var lower = error.ToLowerInvariant();
        if (lower.Contains("(412)") || lower.Contains("http error 429") || lower.Contains("too many requests"))
            return new ProviderException(platform, ProviderErrorKind.RateLimited, $"{platform} đang chặn tạm thời do gọi quá nhiều (HTTP 412/429): chờ vài phút hoặc lưu cookie ở Cài đặt > Nền tảng.");
        // yt-dlp chỉ có extractor cho video lẻ của Douyin/Facebook...; trang kênh/tìm kiếm báo "Unsupported URL" dù có cookie, không phải lỗi đăng nhập.
        if (lower.Contains("unsupported url"))
            return new ProviderException(platform, ProviderErrorKind.BadResponse,
                $"yt-dlp không đọc được loại liên kết này của {platform} (chỉ hỗ trợ liên kết video/bài đăng cụ thể, chưa hỗ trợ trang kênh/trang tìm kiếm). Hãy dán liên kết một video.");
        var loginHint = lower.Contains("login") || lower.Contains("cookies") || lower.Contains("sign in") || lower.Contains("private") || lower.Contains("log in");
        var opaque = LoginGated.Contains(platform) && (lower.Contains("unable to extract") || lower.Contains("empty media response")
            || lower.Contains("rate-limit") || lower.Contains("no video") || lower.Contains("requires authentication"));
        if (loginHint || opaque)
        {
            var kind = hadCookie ? ProviderErrorKind.Forbidden : ProviderErrorKind.KeyMissing;
            var detail = error.Length > 160 ? error[..160] : error;
            return new ProviderException(platform, kind, hadCookie
                ? $"{platform}: cookie đã lưu không đủ quyền hoặc hết hạn; đăng nhập lại ở Cài đặt > Nền tảng. ({detail})"
                : $"{platform} cần đăng nhập: lưu cookie ở Cài đặt > Nền tảng rồi thử lại. ({detail})");
        }

        return new ProviderException(platform, ProviderErrorKind.BadResponse, $"Không lấy được dữ liệu: {error}");
    }
}

/// <summary>Chuyển JSON của yt-dlp (playlist/channel phẳng) thành ContentItem dùng chung cho các crawler.</summary>
public static class YtDlpListing
{
    public static (ContentItem? Owner, List<ContentItem> Items) Parse(string platform, JsonObject info, Func<string, string> urlForId,
        string ownerKind = "channel", string itemKind = "video", int max = 0)
    {
        var ownerName = JsonX.Str(info, "uploader") ?? JsonX.Str(info, "channel") ?? JsonX.Str(info, "title");
        var ownerId = JsonX.Str(info, "channel_id") ?? JsonX.Str(info, "uploader_id") ?? JsonX.Str(info, "id");
        var owner = ownerName is null ? null : new ContentItem(platform, ownerKind, ownerId ?? ownerName, ownerName,
            ownerId, ownerName, JsonX.Str(info, "webpage_url") ?? JsonX.Str(info, "channel_url"),
            ViewCount: JsonX.Long(info, "channel_follower_count") ?? JsonX.Long(info, "follower_count"));      // chủ kênh: ViewCount = số người theo dõi

        var containerUrl = JsonX.Str(info, "webpage_url");
        var entries = info["entries"] is JsonArray array ? array.OfType<JsonObject>().ToList() : [info];
        var items = new List<ContentItem>();
        foreach (var e in entries)
        {
            var id = JsonX.Str(e, "id");
            // Một số extractor (Weibo user...) gán webpage_url của cả danh sách cho từng mục; mục flat (_type=url) thì url chính là trang video.
            var page = JsonX.Str(e, "webpage_url");
            if (info["entries"] is JsonArray && page is not null && page == containerUrl) page = null;
            var url = page ?? (JsonX.Str(e, "_type") == "url" ? JsonX.Str(e, "url") : null) ?? (id is null ? null : urlForId(id)) ?? JsonX.Str(e, "url");
            if (id is null && url is null) continue;
            var thumbnail = JsonX.Str(e, "thumbnail") ?? (e["thumbnails"] is JsonArray t ? JsonX.Str(t.OfType<JsonObject>().LastOrDefault(), "url") : null);
            var duration = JsonX.Double(e, "duration");
            items.Add(new ContentItem(platform, itemKind, id ?? url!, JsonX.Str(e, "title") ?? JsonX.Str(e, "description"),
                JsonX.Str(e, "uploader_id") ?? JsonX.Str(e, "channel_id"), JsonX.Str(e, "uploader") ?? ownerName, url, thumbnail,
                JsonX.Str(e, "description"), JsonX.ParseDate(JsonX.Str(e, "upload_date")) ?? JsonX.FromUnixSeconds(JsonX.Long(e, "timestamp")),
                duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : null,
                JsonX.Long(e, "view_count"), JsonX.Long(e, "like_count"), JsonX.Long(e, "comment_count")));
            if (max > 0 && items.Count >= max) break;
        }

        return (owner, items);
    }

    public static CrawlResult ToResult(string platform, CrawlKind kind, JsonObject info, Func<string, string> urlForId, int max, string ownerKind = "channel")
    {
        var (owner, items) = Parse(platform, info, urlForId, ownerKind, max: max);
        if (items.Count == 0) throw new ProviderException(platform, ProviderErrorKind.BadResponse, "Nguồn không trả về nội dung nào (kênh riêng tư hoặc cần đăng nhập?).");
        return new CrawlResult(platform, kind, owner, items);
    }
}
