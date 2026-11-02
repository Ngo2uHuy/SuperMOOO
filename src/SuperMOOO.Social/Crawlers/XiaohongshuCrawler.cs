using System.Text.RegularExpressions;

namespace SuperMOOO.Social.Crawlers;

/// <summary>Xiaohongshu: đọc `window.__INITIAL_STATE__` của trang explore/search/hồ sơ.</summary>
[Service(As = [typeof(IPlatformCrawler)])]
public sealed partial class XiaohongshuCrawler(CrawlHttp http, PlatformCredentials credentials)
    : CrawlerBase("xiaohongshu", CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Explore, CrawlKind.Search, CrawlKind.Trending)
{
    internal static readonly IReadOnlyDictionary<string, string> TopicKeywords = new Dictionary<string, string>
    {
        ["lifestyle"] = "生活记录", ["beauty"] = "美女", ["fashion"] = "穿搭", ["travel"] = "旅游攻略", ["review"] = "好物测评",
        ["food"] = "美食探店", ["photography"] = "摄影", ["fitness"] = "健身", ["makeup"] = "彩妆", ["movie"] = "影视", ["career"] = "职场",
        ["emotion"] = "情感", ["home"] = "家居", ["game"] = "游戏"
    };

    // channel_id của thanh chuyên mục trang explore (web xiaohongshu.com); chuyên mục không có kênh thì đi qua tìm kiếm.
    internal static readonly IReadOnlyDictionary<string, string> TopicChannels = new Dictionary<string, string>
    {
        ["fashion"] = "homefeed.fashion_v3", ["food"] = "homefeed.food_v3", ["makeup"] = "homefeed.cosmetics_v3", ["movie"] = "homefeed.movie_and_tv_v3",
        ["career"] = "homefeed.career_v3", ["emotion"] = "homefeed.love_v3", ["home"] = "homefeed.household_product_v3", ["game"] = "homefeed.gaming_v3",
        ["travel"] = "homefeed.travel_v3", ["fitness"] = "homefeed.fitness_v3"
    };

    [GeneratedRegex(@"window\.__INITIAL_STATE__\s*=\s*(\{.+?\})</script>", RegexOptions.Singleline)] private static partial Regex StatePattern();
    [GeneratedRegex(@"profile/([0-9a-fA-F]+)")] private static partial Regex UidPattern();

    protected override async Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct)
    {
        var headers = await credentials.HeadersAsync("xiaohongshu", new Dictionary<string, string>
        {
            ["Referer"] = "https://www.xiaohongshu.com/", ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
            ["Accept-Language"] = "zh-CN,zh;q=0.9"
        }, ct);

        if (request.Kind is CrawlKind.Channel or CrawlKind.Profile)
        {
            var m = UidPattern().Match(request.Target);
            var uid = m.Success ? m.Groups[1].Value : request.Target.Trim();
            var html = await PageAsync($"https://www.xiaohongshu.com/user/profile/{uid}", headers, ct);
            return ParseProfile(html, uid, request.Kind, request.MaxItems);
        }

        var topic = (request.Get("topic") ?? (string.IsNullOrWhiteSpace(request.Target) ? "all" : request.Target)).Trim();
        var all = topic.Equals("all", StringComparison.OrdinalIgnoreCase);
        var keyword = TopicKeywords.TryGetValue(topic.ToLowerInvariant(), out var k) ? k : topic;
        // Chuyên mục có kênh riêng trên trang explore thì đọc kênh đó; trang tìm kiếm chỉ có ghi chú khi đã đăng nhập.
        var channel = all ? null : TopicChannels.GetValueOrDefault(topic.ToLowerInvariant());
        var url = all ? "https://www.xiaohongshu.com/explore"
            : channel is not null ? $"https://www.xiaohongshu.com/explore?channel_id={channel}"
            // Không có dấu "/" thì máy chủ chuyển hướng sang http://, HttpClient không theo (HTTPS → HTTP) nên nhận 301.
            : "https://www.xiaohongshu.com/search_result/?" + JsonX.Query([new("keyword", keyword), new("type", "51")]);
        var page = await PageAsync(url, headers, ct);
        return ParseExplore(page, all || channel is not null, request.Kind, request.MaxItems);
    }

    /// <summary>Xiaohongshu chuyển khách sang /login cho nhiều trang (kênh explore, đôi khi cả trang chủ); báo rõ cần cookie thay vì "không có dữ liệu".</summary>
    private async Task<string> PageAsync(string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var reply = await http.SendAsync("xiaohongshu", HttpMethod.Get, url, headers, timeout: TimeSpan.FromSeconds(10), ct: ct);
        if (!reply.FinalUri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase)) return reply.Body;
        throw headers.ContainsKey("Cookie")
            ? new ProviderException("xiaohongshu", ProviderErrorKind.Forbidden, "Cookie Xiaohongshu đã hết hạn: đăng nhập lại xiaohongshu.com rồi lưu cookie mới ở Cài đặt > Nền tảng > Xiaohongshu.")
            : new ProviderException("xiaohongshu", ProviderErrorKind.KeyMissing, "Xiaohongshu yêu cầu đăng nhập cho trang này: lưu cookie xiaohongshu.com (có web_session) ở Cài đặt > Nền tảng > Xiaohongshu.");
    }

    internal static CrawlResult ParseExplore(string html, bool all, CrawlKind kind, int max)
    {
        var state = State(html);
        var feeds = all ? JsonX.At(state, "feed", "feeds") : JsonX.At(state, "search", "feeds") ?? JsonX.At(state, "search", "notes");
        var items = Notes(feeds as JsonArray, null, max);
        if (items.Count == 0)
            throw new ProviderException("xiaohongshu", all ? ProviderErrorKind.BadResponse : ProviderErrorKind.KeyMissing, all
                ? "Trang không có ghi chú nào (cần cookie đăng nhập hoặc bị chặn)."
                : "Trang tìm kiếm Xiaohongshu không trả ghi chú cho khách: lưu cookie xiaohongshu.com ở Cài đặt > Nền tảng > Xiaohongshu, hoặc chọn \"Tất cả\".");
        return new CrawlResult("xiaohongshu", kind, null, items);
    }

    internal static CrawlResult ParseProfile(string html, string uid, CrawlKind kind, int max)
    {
        var state = State(html);
        var name = JsonX.Str(state, "user", "userPageData", "basicInfo", "nickname") ?? JsonX.Str(state, "user", "userPageData", "basicInfo", "nickName");
        var items = Notes(JsonX.At(state, "user", "notes") as JsonArray, name, max);
        if (items.Count == 0) throw new ProviderException("xiaohongshu", ProviderErrorKind.BadResponse, "Hồ sơ không có ghi chú nào (riêng tư hoặc cần đăng nhập).");
        var owner = new ContentItem("xiaohongshu", "profile", uid, name ?? uid, uid, name, $"https://www.xiaohongshu.com/user/profile/{uid}");
        return new CrawlResult("xiaohongshu", kind, owner, items);
    }

    private static JsonNode State(string html)
    {
        var m = StatePattern().Match(html);
        if (!m.Success) throw new ProviderException("xiaohongshu", ProviderErrorKind.BadResponse, "Không tìm thấy dữ liệu trang (có thể bị chặn chống bot).");
        return CrawlHttp.ParseJson("xiaohongshu", m.Groups[1].Value.Replace("undefined", "null"));
    }

    private static List<ContentItem> Notes(JsonArray? array, string? author, int max)
    {
        var items = new List<ContentItem>();
        // user.notes của trang hồ sơ là mảng theo tab (mảng lồng mảng), feed/search là mảng phẳng.
        foreach (var n in array?.SelectMany(n => n is JsonArray tab ? tab.AsEnumerable() : [n]).OfType<JsonObject>() ?? [])
        {
            var card = n["noteCard"] as JsonObject;
            if (card is null) continue;
            var id = JsonX.Str(n, "id") ?? JsonX.Str(card, "noteId");
            if (id is null) continue;
            var token = JsonX.Str(n, "xsecToken") ?? JsonX.Str(card, "xsecToken");
            var cover = JsonX.At(card, "cover");
            var thumb = JsonX.Str(cover, "urlDefault") ?? JsonX.Str(cover, "urlPre")
                ?? (JsonX.At(cover, "infoList") is JsonArray info ? JsonX.Str(info.OfType<JsonObject>().LastOrDefault(), "url") : null);
            var user = JsonX.At(card, "user");
            items.Add(new ContentItem("xiaohongshu", "note", id, JsonX.Str(card, "displayTitle") ?? "Xiaohongshu note", JsonX.Str(user, "userId"),
                JsonX.Str(user, "nickname") ?? JsonX.Str(user, "nickName") ?? author,
                $"https://www.xiaohongshu.com/explore/{id}" + (token is null ? "" : $"?xsec_token={Uri.EscapeDataString(token)}"), thumb,
                LikeCount: JsonX.Compact(JsonX.Str(card, "interactInfo", "likedCount")), Category: JsonX.Str(card, "type")));
            if (max > 0 && items.Count >= max) break;
        }

        return items;
    }
}
