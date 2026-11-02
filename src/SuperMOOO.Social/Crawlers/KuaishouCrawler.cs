using System.Net;
using System.Text.RegularExpressions;

namespace SuperMOOO.Social.Crawlers;

/// <summary>Kuaishou/Kwai: trang explore (trending) và tách một video từ liên kết chia sẻ.</summary>
[Service(As = [typeof(IPlatformCrawler)])]
public sealed partial class KuaishouCrawler(CrawlHttp http) : CrawlerBase("kuaishou", CrawlKind.Channel, CrawlKind.Explore, CrawlKind.Trending)
{
    [GeneratedRegex("caption[:=]\"([^\"]+)\"")] private static partial Regex CaptionPattern();
    [GeneratedRegex("<title>(.*?)</title>", RegexOptions.Singleline)] private static partial Regex TitlePattern();
    [GeneratedRegex("src=\"(https?://[^\"]+\\.mp4[^\"]*)\"")] private static partial Regex Mp4Pattern();
    [GeneratedRegex(@"photo_id_str:""(\d+)""")] private static partial Regex PhotoIdPattern();
    [GeneratedRegex(@"user_name:""([^""]*)""")] private static partial Regex UserNamePattern();
    [GeneratedRegex(@"kwai_id:""([^""]*)""")] private static partial Regex KwaiIdPattern();
    [GeneratedRegex(@"main_mv_urls:\[\{cdn:\w+,url:""([^""]+)""")] private static partial Regex Mp4UrlPattern();
    [GeneratedRegex(@"cover_thumbnail_urls:\[\{cdn:\w+,url:""([^""]+)""")] private static partial Regex CoverPattern();
    [GeneratedRegex(@"timestamp:(\d{13})")] private static partial Regex TimestampPattern();
    [GeneratedRegex(@"view_count:(\d+)")] private static partial Regex ViewPattern();
    [GeneratedRegex(@"like_count:(\d+)")] private static partial Regex LikePattern();
    [GeneratedRegex(@"comment_count:(\d+)")] private static partial Regex CommentPattern();
    [GeneratedRegex("poster=\"(https?://[^\"]+)\"")] private static partial Regex PosterPattern();

    protected override async Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct)
    {
        var category = request.Get("category");
        if (category is not null && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Kuaishou không hỗ trợ quét theo chuyên mục từ xa; hãy lọc trên kết quả đã thu.");

        if (request.Kind == CrawlKind.Channel && Uri.TryCreate(request.Target, UriKind.Absolute, out var uri) && IsVideoUrl(uri))
        {
            var video = await ResolveVideoAsync(request.Target, ct);
            return new CrawlResult("kuaishou", request.Kind, null, [video]);
        }

        var html = await http.GetStringAsync("kuaishou", "https://www.kwai.com/explore", timeout: TimeSpan.FromSeconds(8), ct: ct);
        return new CrawlResult("kuaishou", request.Kind, null, ParseTrending(html, request.MaxItems));
    }

    private static bool IsVideoUrl(Uri uri) =>
        uri.AbsolutePath.Contains("short-video", StringComparison.Ordinal) || uri.Host.StartsWith("v.", StringComparison.Ordinal) || uri.Host.Contains("kwai.com", StringComparison.Ordinal);

    /// <summary>
    /// Trang explore nhúng `photoList` trong window.__NUXT__: mỗi bài có photo_id_str, user_name, kwai_id, like/view/comment_count, timestamp và link mp4.
    /// Chỉ đọc các số viết thẳng; giá trị bị nén thành biến (vd `comment_count:M`) thì để trống thay vì đoán.
    /// </summary>
    internal static List<ContentItem> ParseTrending(string html, int max)
    {
        var captions = CaptionPattern().Matches(html);
        var items = new List<ContentItem>();
        var seen = new HashSet<string>();
        for (var i = 0; i < captions.Count && items.Count < max; i++)
        {
            var caption = captions[i].Groups[1].Value.Replace("\\\"", "\"").Trim();
            if (caption.Length == 0) continue;
            var from = captions[i].Index;
            var to = i + 1 < captions.Count ? captions[i + 1].Index : Math.Min(html.Length, from + 30000);
            var block = html[from..to];
            var before = html[Math.Max(0, from - 100)..from];

            var photoId = Field(block, PhotoIdPattern());
            var mp4 = Unescape(Field(block, Mp4UrlPattern()));
            var id = photoId ?? "kwai_" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(caption)))[..12].ToLowerInvariant();
            if (!seen.Add(id)) continue;
            items.Add(new ContentItem("kuaishou", "video", id, caption.Length > 80 ? caption[..80] : caption,
                Field(block, KwaiIdPattern()), Field(block, UserNamePattern()), mp4 ?? "https://www.kwai.com/explore", Unescape(Field(block, CoverPattern())),
                mp4, JsonX.FromUnixSeconds(Number(before, TimestampPattern()) / 1000), null,
                Number(block, ViewPattern()), Number(block, LikePattern()), Number(block, CommentPattern()), Category: "trending"));
        }

        if (items.Count == 0) throw new ProviderException("kuaishou", ProviderErrorKind.BadResponse, "Không đọc được danh sách trending Kuaishou.");
        return items;
    }

    private static string? Field(string text, Regex pattern) => pattern.Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    private static long? Number(string text, Regex pattern) => Field(text, pattern) is { } v && long.TryParse(v, out var n) ? n : null;

    private static string? Unescape(string? value) => value?.Replace("\\u002F", "/");

    /// <summary>Tách tiêu đề, link mp4 (nếu có) từ trang chia sẻ; link mp4 nằm trong `Content`.</summary>
    public async Task<ContentItem> ResolveVideoAsync(string url, CancellationToken ct = default)
    {
        var reply = await http.SendAsync("kuaishou", HttpMethod.Get, url, new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15", ["Referer"] = "https://v.kuaishou.com/"
        }, timeout: TimeSpan.FromSeconds(8), ct: ct);
        return ParseVideoPage(reply.Body, reply.FinalUri.ToString());
    }

    internal static ContentItem ParseVideoPage(string html, string finalUrl)
    {
        var title = TitlePattern().Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value.Replace(" - 快手", "")).Trim() : "Kuaishou video";
        var mp4 = Mp4Pattern().Match(html);
        if (!mp4.Success) throw new ProviderException("kuaishou", ProviderErrorKind.BadResponse, "Trang không chứa link video (có thể cần cookie hoặc bị chặn).");
        var id = finalUrl.TrimEnd('/').Split('/', '?').Last(s => s.Length > 0);
        return new ContentItem("kuaishou", "video", id, title, Url: finalUrl, ThumbnailUrl: PosterPattern().Match(html) is { Success: true } p ? p.Groups[1].Value : null,
            Content: mp4.Groups[1].Value);
    }
}
