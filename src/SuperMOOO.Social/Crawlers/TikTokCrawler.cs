using System.Globalization;
using System.Text.RegularExpressions;
using SuperMOOO.Social.Listening;

namespace SuperMOOO.Social.Crawlers;

/// <summary>
/// TikTok: nhà cung cấp bên thứ ba (nếu cấu hình) → tikwm → yt-dlp. Không dùng API web đã ký (cần chữ ký động, hay gãy).
/// </summary>
[Service(As = [typeof(IPlatformCrawler)])]
public sealed partial class TikTokCrawler(CrawlHttp http, IYtDlpMetadata ytdlp, PlatformCredentials credentials, ISettingsStore settings, QuotaService quota)
    : CrawlerBase("tiktok", CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Hashtag, CrawlKind.Explore, CrawlKind.Search)
{
    private const string TikWm = "https://www.tikwm.com/api";

    internal static readonly IReadOnlyDictionary<string, string[]> CategorySearchTerms = new Dictionary<string, string[]>
    {
        ["singing_dancing"] = ["dance trend", "singing cover", "choreography"],
        ["comedy"] = ["funny comedy", "prank", "skit"],
        ["sports"] = ["sports football", "workout fitness", "basketball"],
        ["anime"] = ["anime manga", "cosplay anime", "genshin anime"],
        ["relationship"] = ["relationship love", "couple", "family life"],
        ["shows"] = ["movie drama", "series trailer", "film review"],
        ["lipsync"] = ["lipsync acting", "pov acting", "transformation"],
        ["daily_life"] = ["daily vlog", "day in my life", "lifestyle vlog"],
        ["beauty"] = ["makeup skincare", "beauty tips", "cosmetics review"],
        ["games"] = ["gaming gameplay", "esports", "game highlight"],
        ["society"] = ["news society", "current events", "community news"],
        ["outfit"] = ["ootd fashion", "outfit ideas", "street style"],
        ["cars"] = ["car review", "motorcycle", "supercar"],
        ["food"] = ["food cooking", "street food", "mukbang"],
        ["animals"] = ["cute pets", "cat dog", "animals"],
        ["tech"] = ["tech review", "gadgets", "smartphone"],
        ["fitness"] = ["fitness workout", "gym", "weight loss"]
    };

    // tikwm chặn Cloudflare với /feed/search nhưng vẫn mở /challenge/*, nên chuyên mục đi qua hashtag phổ biến trước.
    internal static readonly IReadOnlyDictionary<string, string[]> CategoryHashtags = new Dictionary<string, string[]>
    {
        ["singing_dancing"] = ["dance", "singing"], ["comedy"] = ["comedy", "funny"], ["sports"] = ["sports", "football"],
        ["anime"] = ["anime", "cosplay"], ["relationship"] = ["relationship", "couple"], ["shows"] = ["movie", "drama"],
        ["lipsync"] = ["lipsync", "pov"], ["daily_life"] = ["dayinmylife", "vlog"], ["beauty"] = ["makeup", "skincare"],
        ["games"] = ["gaming", "gameplay"], ["society"] = ["news", "community"], ["outfit"] = ["ootd", "outfit"], ["cars"] = ["cars", "carsoftiktok"],
        ["food"] = ["food", "cooking"], ["animals"] = ["pets", "animals"], ["tech"] = ["tech", "gadgets"], ["fitness"] = ["fitness", "gym"]
    };

    [GeneratedRegex(@"@([a-zA-Z0-9_.\-]+)")] private static partial Regex HandlePattern();
    [GeneratedRegex("\"id\":\"(\\d{12,})\"")] private static partial Regex ExploreIdPattern();

    public async Task<ContentItem> GetProfileAsync(string target, CancellationToken ct = default) =>
        (await ProfileAsync(Username(target), ct)) ?? new ContentItem("tiktok", "channel", Username(target), Username(target),
            Username(target), Username(target), $"https://www.tiktok.com/@{Username(target)}");

    protected override async Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct) => request.Kind switch
    {
        CrawlKind.Hashtag => await HashtagAsync(request, ct),
        CrawlKind.Explore => await ExploreAsync(request, ct),
        CrawlKind.Search => await SearchAsync(request, ct),
        CrawlKind.Profile => new CrawlResult("tiktok", request.Kind, await GetProfileAsync(request.Target, ct), []),
        _ => await ChannelAsync(request, ct)
    };

    internal static string Username(string target)
    {
        var t = target.Trim();
        var m = HandlePattern().Match(t);
        if (m.Success) return m.Groups[1].Value;
        return t.Split('/', '?')[0].TrimStart('@');
    }

    // ---- Kênh ----

    private async Task<CrawlResult> ChannelAsync(CrawlRequest request, CancellationToken ct)
    {
        var user = Username(request.Target);
        var mode = settings.Get<TikTokSettings>("social.tiktok").EngineMode.Trim().ToLowerInvariant();
        string? warning = null;
        var providerKey = await credentials.GetAsync("tiktok", "provider_api_key", ct) ?? await credentials.TokenAsync("tiktok", ct);

        if (request.Source != CrawlSource.Scrape && (mode == "provider" || (mode == "auto" && providerKey is not null)))
        {
            try
            {
                var viaProvider = await ProviderChannelAsync(user, request.MaxItems, providerKey, ct);
                if (viaProvider.Count > 0)
                    return new CrawlResult("tiktok", request.Kind, new ContentItem("tiktok", "channel", user, user, user, user, $"https://www.tiktok.com/@{user}"), viaProvider);
            }
            catch (ProviderException ex) when (mode == "auto" || request.Source == CrawlSource.Auto)
            {
                warning = $"Nhà cung cấp TikTok lỗi ({ex.Message}); dùng bộ quét cục bộ.";
            }
        }

        var items = await TikWmListAsync($"{TikWm}/user/posts?unique_id={JsonX.Quote(user)}&count={Math.Min(request.MaxItems, 50)}&cursor=0", user, ct);
        if (items.Count == 0)
        {
            var info = await ytdlp.ExtractAsync("tiktok", new YtDlpQuery($"https://www.tiktok.com/@{user}", request.MaxItems, CookiePlatform: "tiktok"), ct);
            var (_, scraped) = YtDlpListing.Parse("tiktok", info, id => $"https://www.tiktok.com/@{user}/video/{id}", max: request.MaxItems);
            items = scraped;
        }

        if (items.Count == 0) throw new ProviderException("tiktok", ProviderErrorKind.BadResponse, "Không lấy được video nào của kênh.");
        var owner = await ProfileAsync(user, ct) ?? new ContentItem("tiktok", "channel", user, user, user, user, $"https://www.tiktok.com/@{user}");
        return new CrawlResult("tiktok", request.Kind, owner, items.Take(request.MaxItems).ToList(), warning);
    }

    private async Task<List<ContentItem>> ProviderChannelAsync(string user, int max, string? key, CancellationToken ct)
    {
        if (key is null) throw new ProviderException("tiktok", ProviderErrorKind.KeyMissing, "Chưa cấu hình TikTok provider API key.");
        var url = $"{settings.Get<TikTokSettings>("social.tiktok").ProviderUrl}?{JsonX.Query([new("username", user), new("count", max.ToString())])}";
        var data = await http.GetJsonAsync("tiktok", url, new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}", ["X-API-Key"] = key }, ct: ct);
        await quota.RecordAsync("tiktok", "provider_channel", QuotaCost.TikTokProviderCall, "tiktok_crawler", ct);

        var list = JsonX.At(data, "itemList") as JsonArray ?? JsonX.At(data, "videos") as JsonArray ?? [];
        return list.OfType<JsonObject>().Select(i =>
        {
            var id = JsonX.Str(i, "id") ?? "";
            return new ContentItem("tiktok", "video", id, JsonX.Str(i, "desc"), user, user, $"https://www.tiktok.com/@{user}/video/{id}",
                JsonX.Str(i, "video", "cover"), JsonX.Str(i, "desc"), JsonX.FromUnixSeconds(JsonX.Long(i, "createTime")), null, JsonX.Long(i, "stats", "playCount"),
                JsonX.Long(i, "stats", "diggCount"), JsonX.Long(i, "stats", "commentCount"));
        }).Where(i => i.ExternalId.Length > 0).ToList();
    }

    private async Task<ContentItem?> ProfileAsync(string user, CancellationToken ct)
    {
        JsonNode data;
        try
        {
            data = await http.GetJsonAsync("tiktok", $"{TikWm}/user/info?unique_id={JsonX.Quote(user)}", ct: ct);
        }
        catch (ProviderException)
        {
            return null;       // hồ sơ chỉ là phần phụ; danh sách video mới là kết quả chính
        }

        var u = JsonX.At(data, "data");
        if (JsonX.Long(data, "code") != 0 || u is null) return null;
        return new ContentItem("tiktok", "channel", user, JsonX.Str(u, "nickname") ?? user, user, JsonX.Str(u, "nickname") ?? user,
            $"https://www.tiktok.com/@{user}", JsonX.Str(u, "avatar"), JsonX.Str(u, "signature"), null, null,
            JsonX.Long(u, "follower_count"), JsonX.Long(u, "heart_count"));
    }

    // ---- Hashtag / tìm kiếm / khám phá ----

    private async Task<CrawlResult> HashtagAsync(CrawlRequest request, CancellationToken ct)
    {
        var tag = request.Target.Trim().TrimStart('#');
        var items = await ChallengeAsync(tag, request.MaxItems, ct);
        if (items.Count == 0) items = await TikWmListAsync($"{TikWm}/feed/search?keywords={JsonX.Quote(tag)}&count={request.MaxItems}&cursor=0", null, ct);
        if (items.Count == 0)
        {
            try
            {
                var info = await ytdlp.ExtractAsync("tiktok", new YtDlpQuery($"https://www.tiktok.com/tag/{tag}", request.MaxItems, CookiePlatform: "tiktok"), ct);
                items = YtDlpListing.Parse("tiktok", info, id => $"https://www.tiktok.com/video/{id}", max: request.MaxItems).Items;
            }
            catch (ProviderException ex)
            {
                throw new ProviderException("tiktok", ex.Kind, $"Không lấy được video cho #{tag}: nguồn tikwm bị chặn/không phản hồi và yt-dlp không đọc được trang hashtag ({ex.Message}).");
            }
        }

        if (items.Count == 0) throw new ProviderException("tiktok", ProviderErrorKind.BadResponse, $"Không có video nào cho #{tag}.");
        return new CrawlResult("tiktok", request.Kind, null, items.Take(request.MaxItems).ToList());
    }

    private async Task<CrawlResult> SearchAsync(CrawlRequest request, CancellationToken ct)
    {
        var items = await TikWmListAsync($"{TikWm}/feed/search?keywords={JsonX.Quote(request.Target)}&count={request.MaxItems}&cursor=0", null, ct);
        if (items.Count == 0) throw new ProviderException("tiktok", ProviderErrorKind.BadResponse, "Tìm kiếm TikTok không có kết quả (nguồn tikwm có thể đang bị chặn chống bot).");
        return new CrawlResult("tiktok", request.Kind, null, items.Take(request.MaxItems).ToList());
    }

    private async Task<CrawlResult> ExploreAsync(CrawlRequest request, CancellationToken ct)
    {
        var category = (request.Get("category") ?? (string.IsNullOrWhiteSpace(request.Target) ? "all" : request.Target)).ToLowerInvariant();
        var items = new List<ContentItem>();

        if (category != "all")
        {
            var tags = CategoryHashtags.TryGetValue(category, out var h) ? h : [category.Replace("_", "")];
            foreach (var tag in tags)
            {
                if (items.Count >= request.MaxItems) break;
                var found = await ChallengeAsync(tag, request.MaxItems, ct);
                items.AddRange(found.Where(f => items.All(x => x.ExternalId != f.ExternalId)));
            }

            string[] terms = items.Count > 0 ? [] : CategorySearchTerms.TryGetValue(category, out var t) ? t : [category.Replace('_', ' ')];
            foreach (var term in terms)
            {
                if (items.Count >= request.MaxItems) break;
                var found = await TikWmListAsync($"{TikWm}/feed/search?keywords={JsonX.Quote(term)}&count={Math.Min(request.MaxItems, 50)}&cursor=0", null, ct);
                items.AddRange(found.Where(f => items.All(x => x.ExternalId != f.ExternalId)));
            }
        }
        else
        {
            items = await FeedAsync(request.MaxItems, ct);
        }

        if (items.Count == 0 && category == "all") items = await ExploreHtmlAsync(request.MaxItems, ct);
        if (items.Count == 0)
            throw new ProviderException("tiktok", ProviderErrorKind.BadResponse,
                "Không lấy được nội dung khám phá TikTok: nguồn tikwm không phản hồi hoặc đang chặn chống bot, trang tiktok.com/explore yêu cầu trình duyệt. Thử lại sau vài phút hoặc đổi mạng/proxy.");

        return new CrawlResult("tiktok", request.Kind, null, items.Take(request.MaxItems).Select(i => i with { Category = category }).ToList());
    }

    private async Task<List<ContentItem>> ExploreHtmlAsync(int max, CancellationToken ct)
    {
        string html;
        try
        {
            html = await http.GetStringAsync("tiktok", "https://www.tiktok.com/explore",
                new Dictionary<string, string> { ["Accept"] = "text/html,application/xhtml+xml" }, ct: ct);
        }
        catch (ProviderException)
        {
            return [];
        }

        var seen = new HashSet<string>();
        var items = new List<ContentItem>();
        foreach (Match m in ExploreIdPattern().Matches(html))
        {
            var id = m.Groups[1].Value;
            if (!seen.Add(id)) continue;
            var block = html.Substring(Math.Max(0, m.Index - 2500), Math.Min(7000, html.Length - Math.Max(0, m.Index - 2500)));
            var uid = Regex.Match(block, "\"uniqueId\":\"([^\"]+)\"").Groups[1].Value;
            var desc = Regex.Match(block, "\"desc\":\"(.*?)\"").Groups[1].Value;
            var plays = Regex.Match(block, "\"playCount\":(\\d+)");
            items.Add(new ContentItem("tiktok", "video", id, desc.Length > 0 ? desc : $"TikTok video {id}", uid, uid,
                uid.Length > 0 ? $"https://www.tiktok.com/@{uid}/video/{id}" : $"https://www.tiktok.com/video/{id}",
                ViewCount: plays.Success ? long.Parse(plays.Groups[1].Value) : null));
            if (items.Count >= max) break;
        }

        return items;
    }

    // ---- tikwm ----

    /// <summary>
    /// Feed đề xuất của tikwm: bắt buộc `region` (thiếu thì trả "region empty"), mỗi lần chỉ ~10–16 video ngẫu nhiên nên gọi vài lần rồi gộp.
    /// Mặc định lấy vùng theo máy người dùng để giống tab Dành cho bạn của họ.
    /// </summary>
    internal async Task<List<ContentItem>> FeedAsync(int max, CancellationToken ct, string? region = null)
    {
        region ??= RegionInfo.CurrentRegion.TwoLetterISORegionName is { Length: 2 } r && r != "IV" ? r : "US";      // IV = vùng invariant
        var items = new List<ContentItem>();
        var misses = 0;
        for (var attempt = 0; attempt < Math.Min(8, max / 8 + 3) && items.Count < max && misses < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1.5), ct);     // tikwm miễn phí giới hạn ~1 lời gọi/giây
            var found = await TikWmListAsync($"{TikWm}/feed/list?region={region}&count={Math.Min(max, 30)}&cursor=0", null, ct);
            if (found.Count == 0) misses++;      // tikwm hay trả "Oops, an error occurred! Try again" tạm thời
            items.AddRange(found.Where(f => items.All(x => x.ExternalId != f.ExternalId)));
        }

        return items;
    }

    /// <summary>Video của hashtag: /challenge/posts nay bắt buộc `challenge_id`, nên tra id qua /challenge/info trước.</summary>
    private async Task<List<ContentItem>> ChallengeAsync(string tag, int max, CancellationToken ct)
    {
        string? id;
        try
        {
            var info = await http.GetJsonAsync("tiktok", $"{TikWm}/challenge/info?challenge_name={JsonX.Quote(tag)}", ct: ct);
            id = JsonX.Long(info, "code") == 0 ? JsonX.Str(info, "data", "id") : null;
        }
        catch (ProviderException)
        {
            return [];
        }

        if (id is null) return [];
        var items = new List<ContentItem>();
        var cursor = "0";
        while (items.Count < max)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.2), ct);
            JsonNode page;
            try
            {
                page = await http.GetJsonAsync("tiktok", $"{TikWm}/challenge/posts?challenge_id={id}&count={Math.Min(max - items.Count, 30)}&cursor={cursor}", ct: ct);
            }
            catch (ProviderException)
            {
                break;
            }

            var found = ParseTikWm(page, null);
            items.AddRange(found.Where(f => items.All(x => x.ExternalId != f.ExternalId)));
            var next = JsonX.Str(page, "data", "cursor");
            if (found.Count == 0 || JsonX.At(page, "data", "hasMore")?.ToString() is not ("true" or "1") || next is null || next == cursor) break;
            cursor = next;
        }

        return items;
    }

    private async Task<List<ContentItem>> TikWmListAsync(string url, string? defaultUser, CancellationToken ct)
    {
        JsonNode data;
        try
        {
            data = await http.GetJsonAsync("tiktok", url, ct: ct);
        }
        catch (ProviderException)
        {
            return [];     // tikwm hay lỗi tạm thời hoặc bị Cloudflare chặn (403); caller có đường dự phòng và sẽ báo lỗi nếu hết đường
        }

        return ParseTikWm(data, defaultUser);
    }

    internal static List<ContentItem> ParseTikWm(JsonNode data, string? defaultUser)
    {
        if (JsonX.Long(data, "code") != 0) return [];
        var block = JsonX.At(data, "data");
        // /feed/list trả thẳng mảng trong `data`, các endpoint khác bọc trong data.videos.
        var list = block as JsonArray ?? JsonX.At(block, "videos") as JsonArray ?? JsonX.At(block, "items") as JsonArray ?? JsonX.At(block, "list") as JsonArray ?? [];
        var items = new List<ContentItem>();
        foreach (var v in list.OfType<JsonObject>())
        {
            var id = JsonX.Str(v, "video_id") ?? JsonX.Str(v, "id") ?? JsonX.Str(v, "aweme_id");
            if (id is null) continue;
            var uid = JsonX.Str(v, "author", "unique_id") ?? JsonX.Str(v, "author", "uniqueId") ?? defaultUser;
            var duration = JsonX.Long(v, "duration");
            items.Add(new ContentItem("tiktok", "video", id, JsonX.Str(v, "title") ?? JsonX.Str(v, "desc") ?? $"TikTok video {id}", uid,
                JsonX.Str(v, "author", "nickname") ?? uid, uid is null ? $"https://www.tiktok.com/video/{id}" : $"https://www.tiktok.com/@{uid}/video/{id}",
                JsonX.Str(v, "cover") ?? JsonX.Str(v, "origin_cover"), JsonX.Str(v, "title"), JsonX.FromUnixSeconds(JsonX.Long(v, "create_time")),
                duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : null, JsonX.Long(v, "play_count"), JsonX.Long(v, "digg_count"),
                JsonX.Long(v, "comment_count")));
        }

        return items;
    }
}
