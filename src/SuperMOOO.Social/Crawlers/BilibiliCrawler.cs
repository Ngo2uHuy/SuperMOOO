using System.Text.RegularExpressions;

namespace SuperMOOO.Social.Crawlers;

/// <summary>Bilibili: xếp hạng/tìm kiếm qua API web (kèm cookie khách buvid để qua HTTP 412), kênh qua yt-dlp.</summary>
[Service(As = [typeof(IPlatformCrawler)])]
public sealed partial class BilibiliCrawler(CrawlHttp http, IYtDlpMetadata ytdlp, BilibiliApi api)
    : CrawlerBase("bilibili", CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Search, CrawlKind.Trending)
{
    internal static readonly IReadOnlyDictionary<string, int> Categories = new Dictionary<string, int>
    {
        ["all"] = 0, ["douga"] = 1, ["anime"] = 1, ["guochuang"] = 168, ["kichiku"] = 119, ["music"] = 3, ["game"] = 4, ["gaming"] = 4,
        // 电影(23)/电视剧(11) là khu PGC, API xếp hạng trả danh sách rỗng → dùng khu 影视 (181: bình phim, cắt phim của người dùng).
        ["entertainment"] = 5, ["movie"] = 181, ["tv"] = 181, ["tech"] = 188, ["knowledge"] = 36, ["dance"] = 129, ["life"] = 160,
        ["sports"] = 234, ["car"] = 223, ["food"] = 211, ["animal"] = 217, ["fashion"] = 155
    };

    private static readonly HashSet<string> SortModes = ["totalrank", "click", "pubdate", "dm"];

    [GeneratedRegex(@"space\.bilibili\.com/(\d+)|(\d+)")] private static partial Regex MidPattern();

    protected override async Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct) => request.Kind switch
    {
        CrawlKind.Trending => await RankingAsync(request, ct),
        CrawlKind.Search => await SearchAsync(request, ct),
        _ => await SpaceAsync(request, ct)
    };

    private Task<Dictionary<string, string>> HeadersAsync(CancellationToken ct) => api.HeadersAsync(ct);

    private async Task<CrawlResult> RankingAsync(CrawlRequest request, CancellationToken ct)
    {
        var category = (request.Get("category") ?? (string.IsNullOrWhiteSpace(request.Target) ? "all" : request.Target)).ToLowerInvariant();
        if (!Categories.TryGetValue(category, out var rid)) throw new ArgumentException($"Chuyên mục Bilibili không hỗ trợ: {category}");
        var headers = await HeadersAsync(ct);
        List<ContentItem> items = [];
        ProviderException? last = null;
        try
        {
            var data = await http.GetJsonAsync("bilibili", $"https://api.bilibili.com/x/web-interface/ranking?rid={rid}&day=3&type=1&arc_type=0", headers, ct: ct);
            items = ParseRanking(data, request.MaxItems, category);
        }
        catch (ProviderException ex)
        {
            last = ex;
        }

        // Popular không có chuyên mục nên chỉ làm dự phòng cho "all".
        if (items.Count == 0 && category == "all")
        {
            try
            {
                var data = await http.GetJsonAsync("bilibili", $"https://api.bilibili.com/x/web-interface/popular?ps={request.MaxItems}&pn=1", headers, ct: ct);
                items = ParsePopular(data, request.MaxItems);
            }
            catch (ProviderException ex)
            {
                last = ex;
            }
        }

        if (items.Count == 0) throw last ?? new ProviderException("bilibili", ProviderErrorKind.BadResponse, "Bảng xếp hạng Bilibili rỗng.");
        return new CrawlResult("bilibili", request.Kind, null, items);
    }

    internal static List<ContentItem> ParseRanking(JsonNode data, int max, string category) =>
        JsonX.Objects(data, "data", "list").Take(max).Select((v, i) => Video(v, i + 1, category, JsonX.Str(v, "author"), JsonX.Str(v, "mid"),
            JsonX.Long(v, "play"), null, null, ParseClock(JsonX.Str(v, "duration")))).ToList();

    internal static List<ContentItem> ParsePopular(JsonNode data, int max) =>
        JsonX.Objects(data, "data", "list").Take(max).Select((v, i) => Video(v, i + 1, "all", JsonX.Str(v, "owner", "name"),
            JsonX.Str(v, "owner", "mid"), JsonX.Long(v, "stat", "view"), null, null,
            JsonX.Long(v, "duration") is { } s ? TimeSpan.FromSeconds(s) : null)).ToList();

    private static ContentItem Video(JsonObject v, int rank, string category, string? author, string? mid, long? views, long? likes, long? comments, TimeSpan? duration)
    {
        var bvid = JsonX.Str(v, "bvid") ?? "";
        var pic = JsonX.Str(v, "pic");
        if (pic is not null && pic.StartsWith("//", StringComparison.Ordinal)) pic = "https:" + pic;
        return new ContentItem("bilibili", "video", bvid, rank > 0 ? $"#{rank} {JsonX.Str(v, "title")}" : JsonX.Str(v, "title"), mid, author,
            $"https://www.bilibili.com/video/{bvid}", pic, JsonX.Str(v, "description"), null, duration, views, likes, comments, Category: category);
    }

    private static TimeSpan? ParseClock(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split(':').Select(p => int.TryParse(p, out var n) ? n : -1).ToArray();
        if (parts.Any(p => p < 0)) return null;
        return parts.Length switch { 2 => new TimeSpan(0, parts[0], parts[1]), 3 => new TimeSpan(parts[0], parts[1], parts[2]), _ => null };
    }

    private async Task<CrawlResult> SearchAsync(CrawlRequest request, CancellationToken ct)
    {
        var sort = (request.Get("sort_by") ?? "totalrank").ToLowerInvariant();
        sort = sort switch { "latest" => "pubdate", "popular" => "click", _ => sort };
        if (!SortModes.Contains(sort)) sort = "totalrank";
        List<ContentItem> items = [];
        try
        {
            var url = $"https://api.bilibili.com/x/web-interface/search/type?search_type=video&keyword={JsonX.Quote(request.Target)}&order={sort}&page=1&page_size={request.MaxItems}";
            items = ParseSearch(await http.GetJsonAsync("bilibili", url, await HeadersAsync(ct), ct: ct), request.MaxItems);
        }
        catch (ProviderException)
        {
            // API web có thể bị chặn; thử yt-dlp trước khi báo lỗi.
        }

        if (items.Count == 0)
        {
            var info = await ytdlp.ExtractAsync("bilibili", new YtDlpQuery($"bilisearch{request.MaxItems}:{request.Target}", request.MaxItems,
                Headers: await HeadersAsync(ct)), ct);
            items = YtDlpListing.Parse("bilibili", info, id => $"https://www.bilibili.com/video/{id}", max: request.MaxItems).Items;
        }

        if (items.Count == 0) throw new ProviderException("bilibili", ProviderErrorKind.BadResponse, "Tìm kiếm Bilibili không có kết quả.");
        return new CrawlResult("bilibili", request.Kind, null, items);
    }

    internal static List<ContentItem> ParseSearch(JsonNode data, int max) =>
        JsonX.Objects(data, "data", "result").Take(max).Select(v =>
        {
            var arc = JsonX.Str(v, "arcurl");
            var bvid = JsonX.Str(v, "bvid") ?? arc?.TrimEnd('/').Split('/').Last() ?? "";
            var pic = JsonX.Str(v, "pic");
            if (pic is not null && pic.StartsWith("//", StringComparison.Ordinal)) pic = "https:" + pic;
            return new ContentItem("bilibili", "video", bvid, Regex.Replace(JsonX.Str(v, "title") ?? "", "<[^>]+>", ""), JsonX.Str(v, "mid"),
                JsonX.Str(v, "author"), arc ?? $"https://www.bilibili.com/video/{bvid}", pic, JsonX.Str(v, "description"), null, ParseClock(JsonX.Str(v, "duration")),
                JsonX.Long(v, "play"));
        }).Where(i => i.ExternalId.Length > 0).ToList();

    private async Task<CrawlResult> SpaceAsync(CrawlRequest request, CancellationToken ct)
    {
        var m = MidPattern().Match(request.Target);
        var mid = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : request.Target.Trim();
        // yt-dlp (và /x/space/wbi/arc/search) dính kiểm tra rủi ro -352/412 khi không đăng nhập; API danh sách theo từ khoá của kênh thì không.
        if (await ArchivesAsync(mid, request.MaxItems, ct) is { Items.Count: > 0 } viaApi) return viaApi with { Kind = request.Kind };
        var info = await ytdlp.ExtractAsync("bilibili", new YtDlpQuery($"https://space.bilibili.com/{mid}/video", request.MaxItems,
            Headers: await HeadersAsync(ct)), ct);
        var result = YtDlpListing.ToResult("bilibili", request.Kind, info, id => $"https://www.bilibili.com/video/{id}", request.MaxItems);
        return result with { Items = await EnrichAsync(result.Items, ct) };
    }

    private async Task<CrawlResult?> ArchivesAsync(string mid, int max, CancellationToken ct)
    {
        var headers = await HeadersAsync(ct);
        var items = new List<ContentItem>();
        try
        {
            for (var page = 1; items.Count < max; page++)
            {
                var data = await http.GetJsonAsync("bilibili", "https://api.bilibili.com/x/series/recArchivesByKeywords?" + JsonX.Query([new("mid", mid),
                    new("keywords", ""), new("orderby", "pubdate"), new("ps", Math.Min(max, 50).ToString()), new("pn", page.ToString())]), headers, ct: ct);
                var archives = JsonX.Objects(data, "data", "archives").ToList();
                items.AddRange(archives.Select(v => Video(v, 0, "", mid, mid, JsonX.Long(v, "stat", "view"), null, null,
                    JsonX.Long(v, "duration") is { } s and > 0 ? TimeSpan.FromSeconds(s) : null) with
                {
                    Category = null, PublishedAt = JsonX.FromUnixSeconds(JsonX.Long(v, "pubdate")), Content = JsonX.Str(v, "desc")
                }));
                if (archives.Count == 0 || JsonX.Long(data, "data", "page", "total") is { } total && items.Count >= total) break;
            }
        }
        catch (ProviderException) when (items.Count == 0)
        {
            return null;      // để yt-dlp thử
        }

        if (items.Count == 0) return null;
        string? name = null;
        try
        {
            name = JsonX.Str(await http.GetJsonAsync("bilibili", $"https://api.bilibili.com/x/web-interface/card?mid={JsonX.Quote(mid)}", headers, ct: ct), "data", "card", "name");
        }
        catch (ProviderException)
        {
            // tên kênh chỉ để hiển thị
        }

        var owner = new ContentItem("bilibili", "channel", mid, name ?? mid, mid, name, $"https://space.bilibili.com/{mid}");
        return new CrawlResult("bilibili", CrawlKind.Channel, owner, items.Take(max).Select(i => i with { AuthorName = name ?? i.AuthorName }).ToList());
    }

    /// <summary>yt-dlp liệt kê kênh Bilibili chỉ trả BV id (không tiêu đề/chỉ số); bổ sung bằng API chi tiết video, mục nào lỗi thì giữ nguyên.</summary>
    private async Task<List<ContentItem>> EnrichAsync(IReadOnlyList<ContentItem> items, CancellationToken ct)
    {
        var headers = await HeadersAsync(ct);
        var result = new List<ContentItem>(items.Count);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Title)) { result.Add(item); continue; }
            try
            {
                var data = (await http.GetJsonAsync("bilibili", $"https://api.bilibili.com/x/web-interface/wbi/view?bvid={JsonX.Quote(item.ExternalId)}", headers, ct: ct))["data"];
                if (data is null) { result.Add(item); continue; }
                result.Add(item with
                {
                    Title = JsonX.Str(data, "title"), AuthorId = JsonX.Str(data, "owner", "mid"), AuthorName = JsonX.Str(data, "owner", "name"),
                    ThumbnailUrl = JsonX.Str(data, "pic"), Content = JsonX.Str(data, "desc"), PublishedAt = JsonX.FromUnixSeconds(JsonX.Long(data, "pubdate")),
                    Duration = JsonX.Long(data, "duration") is { } d and > 0 ? TimeSpan.FromSeconds(d) : null, ViewCount = JsonX.Long(data, "stat", "view"),
                    LikeCount = JsonX.Long(data, "stat", "like"), CommentCount = JsonX.Long(data, "stat", "reply")
                });
            }
            catch (ProviderException)
            {
                result.Add(item);
            }
        }

        return result;
    }
}
