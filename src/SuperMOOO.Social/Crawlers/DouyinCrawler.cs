namespace SuperMOOO.Social.Crawlers;

/// <summary>Douyin: bảng nổi bật qua API công khai, kênh/tìm kiếm qua yt-dlp.</summary>
[Service(As = [typeof(IPlatformCrawler)])]
public sealed class DouyinCrawler(CrawlHttp http, IYtDlpMetadata ytdlp, PlatformCredentials credentials)
    : CrawlerBase("douyin", CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Search, CrawlKind.Trending)
{
    private const string HotBoardUrl = "https://aweme.snssdk.com/aweme/v1/hot/search/list/";
    private const string HotBoardFallbackUrl = "https://www.douyin.com/aweme/v1/web/hot/search/list/?device_platform=webapp&aid=6383";

    internal static readonly IReadOnlyDictionary<string, string> CategoryKeywords = new Dictionary<string, string>
    {
        ["open_class"] = "公开课", ["game"] = "游戏", ["acg"] = "二次元", ["music"] = "音乐", ["movie"] = "影视", ["food"] = "美食", ["knowledge"] = "知识",
        ["drama"] = "小剧场", ["vlog"] = "生活vlog", ["sports"] = "体育", ["travel"] = "旅行", ["parenting"] = "亲子", ["animal"] = "动物",
        ["rural"] = "三农", ["car"] = "汽车", ["beauty_fashion"] = "美妆穿搭"
    };

    protected override async Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct) => request.Kind switch
    {
        CrawlKind.Trending when request.Get("category") is { } c && CategoryKeywords.TryGetValue(c.ToLowerInvariant(), out var keyword) =>
            await HotMatchAsync(request with { Target = keyword }, ct) ?? throw new ProviderException("douyin", ProviderErrorKind.BadResponse,
                $"Douyin không có nguồn công khai theo chuyên mục; bảng nổi bật hiện không có chủ đề nào chứa \"{keyword}\". Chọn \"Tất cả\" để xem toàn bộ bảng nổi bật."),
        CrawlKind.Trending => new CrawlResult("douyin", request.Kind, null, await HotBoardAsync(request.MaxItems, ct)),
        CrawlKind.Search => await SearchAsync(request, ct),
        _ => await ChannelAsync(request.Target, request, ct)
    };

    internal async Task<List<ContentItem>> HotBoardAsync(int max, CancellationToken ct)
    {
        var headers = await credentials.HeadersAsync("douyin", new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15", ["Referer"] = "https://www.douyin.com/"
        }, ct);
        try
        {
            return ParseHotBoard(await http.GetJsonAsync("douyin", HotBoardUrl, headers, ct: ct), max);
        }
        catch (ProviderException)
        {
            return ParseHotBoard(await http.GetJsonAsync("douyin", HotBoardFallbackUrl, headers, ct: ct), max);
        }
    }

    internal static List<ContentItem> ParseHotBoard(JsonNode data, int max)
    {
        var items = JsonX.Objects(data, "data", "word_list").Take(max).Select((w, i) =>
        {
            var word = JsonX.Str(w, "word") ?? "";
            var label = JsonX.Long(w, "label") switch { 3 => "HOT", 1 => "NEW", _ => "TREND" };
            return new ContentItem("douyin", "topic", word, $"#{i + 1} {word}", Url: $"https://www.douyin.com/search/{Uri.EscapeDataString(word)}",
                ViewCount: JsonX.Long(w, "hot_value"), Category: label);
        }).Where(i => i.ExternalId.Length > 0).ToList();
        if (items.Count == 0) throw new ProviderException("douyin", ProviderErrorKind.BadResponse, "Bảng nổi bật Douyin rỗng.");
        return items;
    }

    // Tìm kiếm web Douyin cần chữ ký a_bogus và yt-dlp không có extractor trang tìm kiếm, nên chỉ tìm được trong bảng nổi bật.
    private async Task<CrawlResult> SearchAsync(CrawlRequest request, CancellationToken ct) =>
        await HotMatchAsync(request, ct) ?? throw new ProviderException("douyin", ProviderErrorKind.BadResponse,
            $"Bảng nổi bật Douyin không có chủ đề nào chứa \"{request.Target}\". Tìm kiếm toàn Douyin theo từ khoá chưa hỗ trợ (web Douyin yêu cầu chữ ký chống bot); hãy dán liên kết video cụ thể.");

    private async Task<CrawlResult?> HotMatchAsync(CrawlRequest request, CancellationToken ct)
    {
        var hot = await HotBoardAsync(50, ct);
        var matched = hot.Where(h => h.ExternalId.Contains(request.Target, StringComparison.OrdinalIgnoreCase)).Take(request.MaxItems).ToList();
        return matched.Count > 0 ? new CrawlResult("douyin", request.Kind, null, matched) : null;
    }

    private async Task<CrawlResult> ChannelAsync(string target, CrawlRequest request, CancellationToken ct)
    {
        var url = target.Contains("://") ? target.Trim() : $"https://www.douyin.com/user/{target.Trim()}";
        if (!url.Contains("/video/", StringComparison.Ordinal) && !url.Contains("v.douyin.com", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("douyin", ProviderErrorKind.BadResponse,
                "Quét trang kênh Douyin chưa hỗ trợ (yt-dlp chỉ đọc từng video, web Douyin yêu cầu chữ ký chống bot). Hãy dán liên kết video douyin.com/video/… hoặc v.douyin.com/…; video Douyin thường cần cookie (Cài đặt > Nền tảng > Douyin).");
        var info = await ytdlp.ExtractAsync("douyin", new YtDlpQuery(url, request.MaxItems, CookiePlatform: "douyin"), ct);
        return YtDlpListing.ToResult("douyin", request.Kind, info, id => $"https://www.douyin.com/video/{id}", request.MaxItems);
    }
}
