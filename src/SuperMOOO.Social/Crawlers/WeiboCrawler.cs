using System.Text.RegularExpressions;

namespace SuperMOOO.Social.Crawlers;

/// <summary>Weibo: hot search qua API công khai, dòng thời gian người dùng qua yt-dlp.</summary>
[Service(As = [typeof(IPlatformCrawler)])]
public sealed partial class WeiboCrawler(CrawlHttp http, IYtDlpMetadata ytdlp, PlatformCredentials credentials)
    : CrawlerBase("weibo", CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Trending)
{
    [GeneratedRegex(@"u/(\d+)|(\d{8,12})")] private static partial Regex UidPattern();

    protected override async Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct)
    {
        if (request.Kind == CrawlKind.Trending)
        {
            var headers = await credentials.HeadersAsync("weibo", new Dictionary<string, string> { ["Referer"] = "https://weibo.com/", ["Accept"] = "application/json" }, ct);
            JsonNode data;
            try
            {
                data = await http.GetJsonAsync("weibo", "https://weibo.com/ajax/side/hotSearch", headers, ct: ct);
            }
            catch (ProviderException ex) when (ex.Kind is ProviderErrorKind.Forbidden or ProviderErrorKind.KeyInvalid)
            {
                // Weibo trả 403 khi không có cookie phiên.
                throw new ProviderException("weibo", ProviderErrorKind.KeyMissing, "Weibo cần đăng nhập: hãy lưu cookie Weibo ở Cài đặt > Nền tảng.");
            }

            return new CrawlResult("weibo", request.Kind, null, ParseHotSearch(data, request.MaxItems));
        }

        var target = request.Target.Trim();
        var m = UidPattern().Match(target);
        var uid = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : target;
        var url = target.Contains("://") ? target : $"https://weibo.com/u/{uid}";
        var info = await ytdlp.ExtractAsync("weibo", new YtDlpQuery(url, request.MaxItems, CookiePlatform: "weibo"), ct);
        return YtDlpListing.ToResult("weibo", request.Kind, info, id => $"https://weibo.com/{uid}/{id}", request.MaxItems, "profile");
    }

    internal static List<ContentItem> ParseHotSearch(JsonNode data, int max)
    {
        var items = JsonX.Objects(data, "data", "realtime").Take(max).Select((w, i) =>
        {
            var word = JsonX.Str(w, "word") ?? "";
            var num = JsonX.Long(w, "num");
            return new ContentItem("weibo", "topic", word, $"#{i + 1} {word}", Url: $"https://s.weibo.com/weibo?q={Uri.EscapeDataString(word)}",
                ViewCount: num, Category: JsonX.Str(w, "category") ?? JsonX.Str(w, "label_name"));
        }).Where(i => i.ExternalId.Length > 0).ToList();
        if (items.Count == 0) throw new ProviderException("weibo", ProviderErrorKind.BadResponse, "Hot search Weibo rỗng.");
        return items;
    }
}
