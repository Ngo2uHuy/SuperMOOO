using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SuperMOOO.Social.Crawlers;

namespace SuperMOOO.Social.Listening;

public enum TrendMode { Videos, Hashtags, HashtagVideos, Creators }

public sealed record TrendQuery(string Platform, TrendMode Mode, string? Region = null, string? Topic = null, string? Keyword = null, int Limit = 50);

/// <summary>EngagementRate null = tính từ (thích + bình luận) / lượt xem; TikTok Creative Center trả sẵn tỉ lệ nhưng không trả số thích.</summary>
public sealed record TrendVideo(ContentItem Item, double? EngagementRate = null);

public sealed record TrendHashtag(string Name, int Rank, long? Posts, long? Views, string? Trend, string? Url);

public sealed record TrendCreator(string Platform, string Id, string Name, string? Url, long? Followers, int Videos, long TotalViews);

public sealed record TrendResult(IReadOnlyList<TrendVideo> Videos, IReadOnlyList<TrendHashtag> Hashtags, IReadOnlyList<TrendCreator> Creators,
    string Source, string? Note, DateTimeOffset FetchedAt, bool FromCache = false)
{
    public int Count => Videos.Count + Hashtags.Count + Creators.Count;
}

public sealed record TrendSupport(bool Supported, string Hint);

/// <summary>Bảng khả năng nền tảng × chế độ của tab Xu hướng (S11): giao diện khoá chế độ/bộ lọc theo bảng này.</summary>
public static class TrendCatalog
{
    public static readonly IReadOnlyList<string> Platforms = ["youtube", "tiktok", "douyin", "bilibili", "facebook", "instagram"];

    public static readonly IReadOnlyDictionary<string, string> RegionNames = new Dictionary<string, string>
    {
        ["VN"] = "Việt Nam", ["US"] = "Hoa Kỳ", ["JP"] = "Nhật Bản", ["KR"] = "Hàn Quốc", ["TW"] = "Đài Loan", ["TH"] = "Thái Lan",
        ["ID"] = "Indonesia", ["GB"] = "Anh", ["PH"] = "Philippines", ["MY"] = "Malaysia", ["SG"] = "Singapore", ["DE"] = "Đức",
        ["FR"] = "Pháp", ["BR"] = "Brazil", ["CA"] = "Canada", ["AU"] = "Úc"
    };

    // Mã chuyên mục YouTube (videoCategoryId) dùng được cho bảng thịnh hành ở hầu hết khu vực.
    private static readonly CategoryOption[] YouTubeCategories =
    [
        PlatformCategories.All, new("1", "Phim & hoạt hình (Film & Animation)"), new("2", "Ô tô & xe cộ (Autos & Vehicles)"), new("10", "Âm nhạc (Music)"),
        new("15", "Thú cưng & động vật (Pets & Animals)"), new("17", "Thể thao (Sports)"), new("19", "Du lịch & sự kiện (Travel & Events)"),
        new("20", "Trò chơi (Gaming)"), new("22", "Mọi người & blog (People & Blogs)"), new("23", "Hài (Comedy)"), new("24", "Giải trí (Entertainment)"),
        new("25", "Tin tức & chính trị (News & Politics)"), new("26", "Hướng dẫn & phong cách (Howto & Style)"), new("27", "Giáo dục (Education)"),
        new("28", "Khoa học & công nghệ (Science & Technology)")
    ];

    // Nhãn nội dung (contentLabelIDs) của bảng video TikTok Creative Center.
    private static readonly CategoryOption[] TikTokContentLabels =
    [
        PlatformCategories.All, new("11001", "Thiên nhiên & động vật (Nature & Animals)"), new("11002", "Làm đẹp (Beauty & Care)"), new("11003", "Thời trang (Fashion)"),
        new("11004", "Âm nhạc & giải trí (Music & Entertainment)"), new("11005", "Game (Games)"), new("11006", "Giáo dục & công việc (Education & Work)"),
        new("11007", "Thể thao (Sports & Outdoor)"), new("11008", "Sáng tạo & tài năng (Creativity & Talent)"), new("11009", "Gia đình & tình cảm (Family & Relationship)"),
        new("11010", "Ẩm thực (Food & Beverage)"), new("11011", "Vlog & selfie"), new("11012", "Xã hội (Society)"), new("11013", "Đời sống (Lifestyle & Leisure)"),
        new("11014", "Xe cộ (Vehicles & Transportation)"), new("11015", "Công nghệ & tài chính (Technology & Finance)"), new("11016", "Review sản phẩm (Product Info & Tests)")
    ];

    // Ngành (industryID) của bảng hashtag TikTok Creative Center.
    private static readonly CategoryOption[] TikTokIndustries =
    [
        PlatformCategories.All, new("10000000000", "Giáo dục (Education)"), new("11000000000", "Xe cộ (Vehicle & Transportation)"),
        new("12000000000", "Mẹ & bé (Baby, Kids & Maternity)"), new("13000000000", "Tài chính (Financial Services)"), new("14000000000", "Làm đẹp (Beauty & Personal Care)"),
        new("15000000000", "Công nghệ (Tech & Electronics)"), new("17000000000", "Du lịch (Travel)"), new("18000000000", "Đồ gia dụng (Household Products)"),
        new("19000000000", "Thú cưng (Pets)"), new("21000000000", "Nhà cửa (Home Improvement)"), new("22000000000", "Thời trang (Apparel & Accessories)"),
        new("23000000000", "Tin tức & giải trí (News & Entertainment)"), new("25000000000", "Game (Games)"), new("27000000000", "Ẩm thực (Food & Beverage)"),
        new("28000000000", "Thể thao (Sports & Outdoor)"), new("29000000000", "Sức khoẻ (Health)")
    ];

    public static TrendSupport Support(string platform, TrendMode mode) => (platform, mode) switch
    {
        ("youtube", TrendMode.Videos) => new(true, "YouTube Data API (bảng thịnh hành), 1 đơn vị hạn mức mỗi 50 video; cần API key ở Cài đặt > Nền tảng."),
        ("youtube", TrendMode.Hashtags) => new(true, "Tổng hợp hashtag/tags từ 200 video thịnh hành (4 đơn vị hạn mức); \"Số bài\" = số video thịnh hành có hashtag đó. Cần API key."),
        ("youtube", TrendMode.HashtagVideos) => new(true, "Trang youtube.com/hashtag/… qua yt-dlp (không tốn hạn mức); có API key thì bổ sung thích/bình luận/ngày đăng (1 đơn vị mỗi 50 video)."),
        ("youtube", TrendMode.Creators) => new(true, "Tổng hợp kênh từ 200 video thịnh hành + số người đăng ký (5–8 đơn vị hạn mức). Cần API key."),
        ("tiktok", TrendMode.Videos) => new(true, "TikTok Creative Center (video đăng 7 ngày qua, có ở US/JP/VN/TH/ID; khách chỉ xem được 4 video), phần còn lại bù bằng feed đề xuất tikwm theo khu vực."),
        ("tiktok", TrendMode.Hashtags) => new(true, "TikTok Creative Center (7 ngày; khách chỉ xem được 3 hashtag) + tổng hợp hashtag trong video nổi bật/feed tikwm."),
        ("tiktok", TrendMode.HashtagVideos) => new(true, "Bộ quét hashtag TikTok sẵn có (tikwm, dự phòng yt-dlp); không lọc theo khu vực."),
        ("tiktok", TrendMode.Creators) => new(true, "Tổng hợp creator từ video nổi bật (Creative Center + feed tikwm), kèm số follower khi nguồn có."),
        ("douyin", TrendMode.Hashtags) => new(true, "Bảng nổi bật (热榜) Douyin; cột Lượt xem là chỉ số độ hot. Bấm đúp một chủ đề để mở tìm kiếm trên douyin.com."),
        ("douyin", _) => new(false, "Douyin chỉ có bảng nổi bật công khai; danh sách video/kênh và tìm kiếm cần chữ ký chống bot (a_bogus) nên chưa hỗ trợ. Dùng \"Top hashtag\"."),
        ("bilibili", TrendMode.Videos) => new(true, "Bảng xếp hạng Bilibili theo phân vùng (cập nhật hằng ngày)."),
        ("bilibili", TrendMode.Hashtags) => new(true, "Từ khoá tìm kiếm hot (热搜) của Bilibili; cột Lượt xem là điểm độ hot."),
        ("bilibili", TrendMode.HashtagVideos) => new(true, "Tìm video Bilibili theo từ khoá, xếp theo lượt xem."),
        ("bilibili", TrendMode.Creators) => new(true, "Tổng hợp UP chủ từ bảng xếp hạng, kèm số người theo dõi."),
        ("instagram", TrendMode.HashtagVideos) => new(true, "Instagram Graph API (bài nổi bật của hashtag): cần token Meta của Trang đã liên kết tài khoản Instagram doanh nghiệp; tối đa 30 hashtag mỗi 7 ngày."),
        ("instagram", _) => new(false, "Instagram không có API xu hướng công khai; chỉ hỗ trợ \"Video theo hashtag\" qua Graph API chính thức (cần tài khoản Instagram doanh nghiệp)."),
        ("facebook", _) => new(false, "Facebook không có dữ liệu xu hướng công khai (app không quét nội dung cần đăng nhập). Dùng tab \"Theo dõi\" để theo dõi Trang cụ thể."),
        _ => new(false, "Nền tảng này chưa hỗ trợ xu hướng.")
    };

    /// <summary>Mã khu vực áp dụng được; rỗng = nguồn không lọc theo khu vực.</summary>
    public static IReadOnlyList<string> Regions(string platform, TrendMode mode) => (platform, mode) switch
    {
        ("youtube", not TrendMode.HashtagVideos) => [.. RegionNames.Keys],
        ("tiktok", TrendMode.Hashtags) => [.. RegionNames.Keys.Where(k => TikTokCreativeCenter.HashtagCountries.Contains(k))],
        ("tiktok", not TrendMode.HashtagVideos) => [.. RegionNames.Keys],
        _ => []
    };

    public static IReadOnlyList<CategoryOption> Topics(string platform, TrendMode mode) => (platform, mode) switch
    {
        ("youtube", not TrendMode.HashtagVideos) => YouTubeCategories,
        ("tiktok", TrendMode.Hashtags) => TikTokIndustries,
        ("tiktok", TrendMode.Videos or TrendMode.Creators) => TikTokContentLabels,
        ("bilibili", TrendMode.Videos or TrendMode.Creators) => PlatformCategories.For("bilibili"),
        _ => []
    };

    public static string? ChannelUrl(string platform, string? authorId) => authorId is not { Length: > 0 } id ? null : platform switch
    {
        "youtube" => $"https://www.youtube.com/channel/{id}",
        "tiktok" => $"https://www.tiktok.com/@{id}",
        "bilibili" => $"https://space.bilibili.com/{id}",
        _ => null
    };

    /// <summary>Mục theo dõi cho một hashtag: chỉ TikTok/Instagram quét được hashtag, nền tảng khác theo dõi như từ khoá tìm kiếm.</summary>
    public static (string Url, string Type) WatchHashtag(string platform, string hashtag) =>
        platform is "tiktok" or "instagram" ? (hashtag.TrimStart('#'), "hashtag") : (hashtag, "search");
}

/// <summary>
/// Dữ liệu tab Xu hướng (S11): video trending, top hashtag, video theo hashtag, kênh hot. Kết quả giữ trong bộ nhớ 30 phút theo
/// (nền tảng, chế độ, vùng, chủ đề, từ khoá, số lượng) để tiết kiệm hạn mức; mỗi lần lấy thật ghi vào nhật ký API.
/// </summary>
[Service]
public sealed partial class TrendService(YouTubeDataApiClient youtube, IYtDlpMetadata ytdlp, TikTokCreativeCenter creativeCenter, TikTokCrawler tiktok,
    DouyinCrawler douyin, BilibiliApi bilibili, CrawlHttp http, CrawlService crawl, MetaGraphClient meta, AuditLogService audit)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    // Mẫu đủ lớn để tổng hợp hashtag/kênh: YouTube 200 video = 4 đơn vị hạn mức.
    private const int AggregateSample = 200;

    private readonly ConcurrentDictionary<string, TrendResult> _cache = new();

    [GeneratedRegex(@"#([\p{L}\p{M}\p{N}_]+)")] private static partial Regex HashtagPattern();
    [GeneratedRegex("<[^>]+>")] private static partial Regex HtmlTag();

    public async Task<TrendResult> GetAsync(TrendQuery query, bool refresh = false, CancellationToken ct = default)
    {
        var support = TrendCatalog.Support(query.Platform, query.Mode);
        if (!support.Supported) throw new NotSupportedException(support.Hint);
        var regions = TrendCatalog.Regions(query.Platform, query.Mode);
        var q = query with
        {
            Region = regions.Count == 0 ? null : regions.Contains(query.Region?.ToUpperInvariant() ?? "") ? query.Region!.ToUpperInvariant() : regions[0],
            Topic = query.Topic is { Length: > 0 } t && t != PlatformCategories.All.Key ? t : null,
            Keyword = query.Keyword?.Trim() is { Length: > 0 } k ? k : null,
            Limit = Math.Clamp(query.Limit, 1, 200)
        };
        if (q.Mode == TrendMode.HashtagVideos && q.Keyword is null) throw new ArgumentException("Nhập hashtag hoặc từ khoá cần xem video.");

        var key = string.Join('|', q.Platform, q.Mode, q.Region, q.Topic, q.Keyword?.ToLowerInvariant(), q.Limit);
        if (!refresh && _cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.FetchedAt < CacheTtl) return hit with { FromCache = true };

        var action = $"trend_{q.Mode.ToString().ToLowerInvariant()}";
        var target = string.Join(' ', new[] { q.Region, q.Topic, q.Keyword }.Where(s => s is not null));
        TrendResult result;
        try
        {
            result = await FetchAsync(q, ct);
        }
        catch (Exception ex) when (ex is ProviderException or ArgumentException)
        {
            await audit.AddAsync(q.Platform, action, target, "failed", 0, CancellationToken.None);
            throw;
        }

        await audit.AddAsync(q.Platform, action, target, "success", result.Count, ct);
        _cache[key] = result;
        return result;
    }

    private async Task<TrendResult> FetchAsync(TrendQuery q, CancellationToken ct) => (q.Platform, q.Mode) switch
    {
        ("youtube", TrendMode.Videos) => await YouTubeVideosAsync(q, ct),
        ("youtube", TrendMode.Hashtags) => await YouTubeHashtagsAsync(q, ct),
        ("youtube", TrendMode.HashtagVideos) => await YouTubeHashtagVideosAsync(q, ct),
        ("youtube", TrendMode.Creators) => await YouTubeCreatorsAsync(q, ct),
        ("tiktok", TrendMode.Videos) => await TikTokVideosResultAsync(q, ct),
        ("tiktok", TrendMode.Hashtags) => await TikTokHashtagsAsync(q, ct),
        ("tiktok", TrendMode.HashtagVideos) => await CrawlHashtagAsync(q, CrawlKind.Hashtag, "Bộ quét hashtag TikTok", ct),
        ("tiktok", TrendMode.Creators) => await TikTokCreatorsAsync(q, ct),
        ("douyin", TrendMode.Hashtags) => await DouyinHotAsync(q, ct),
        ("bilibili", TrendMode.Videos) => Result(videos: await BilibiliRankingAsync(q, q.Limit, ct), source: "Bảng xếp hạng Bilibili"),
        ("bilibili", TrendMode.Hashtags) => await BilibiliHotWordsAsync(q, ct),
        ("bilibili", TrendMode.HashtagVideos) => await BilibiliSearchAsync(q, ct),
        ("bilibili", TrendMode.Creators) => await BilibiliCreatorsAsync(q, ct),
        ("instagram", TrendMode.HashtagVideos) => Result(videos: [.. (await meta.GetInstagramHashtagMediaAsync(q.Keyword!, q.Limit, ct)).Select(i => new TrendVideo(i))],
            source: "Instagram Graph API (top_media)", note: "Graph API không trả tên tài khoản đăng bài và lượt xem."),
        _ => throw new NotSupportedException(TrendCatalog.Support(q.Platform, q.Mode).Hint)
    };

    private static TrendResult Result(IReadOnlyList<TrendVideo>? videos = null, IReadOnlyList<TrendHashtag>? hashtags = null,
        IReadOnlyList<TrendCreator>? creators = null, string source = "", string? note = null) =>
        new(videos ?? [], hashtags ?? [], creators ?? [], source, note, DateTimeOffset.UtcNow);

    private static bool Matches(string? text, string? keyword) =>
        keyword is null || (text?.Contains(keyword.TrimStart('#'), StringComparison.OrdinalIgnoreCase) ?? false);

    private static string? Join(params string?[] notes) => notes.Where(n => n is { Length: > 0 }).ToList() is { Count: > 0 } list ? string.Join(" ", list) : null;

    /// <summary>Gộp video theo tác giả: số video trong mẫu và tổng lượt xem; Followers điền sau nếu nguồn có.</summary>
    private static List<TrendCreator> Aggregate(string platform, IEnumerable<ContentItem> videos, int limit) =>
        videos.Where(v => (v.AuthorId ?? v.AuthorName) is { Length: > 0 })
            .GroupBy(v => v.AuthorId ?? v.AuthorName!)
            .Select(g => new TrendCreator(platform, g.Key, g.Select(v => v.AuthorName).FirstOrDefault(n => n is not null) ?? g.Key,
                TrendCatalog.ChannelUrl(platform, g.First().AuthorId), null, g.Count(), g.Sum(v => v.ViewCount ?? 0)))
            .OrderByDescending(c => c.Videos).ThenByDescending(c => c.TotalViews).Take(limit).ToList();

    /// <summary>Đếm hashtag (#… trong tiêu đề/mô tả và tags) trên mẫu video: Posts = số video có hashtag, Views = tổng lượt xem các video đó.</summary>
    private static List<TrendHashtag> CountHashtags(IEnumerable<(ContentItem Video, IReadOnlyList<string> Tags)> sample, string? keyword, int startRank,
        Func<string, string> url)
    {
        var stats = new Dictionary<string, (string Name, int Posts, long Views)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (video, tags) in sample)
        {
            var found = HashtagPattern().Matches($"{video.Title} {video.Content}").Select(m => m.Groups[1].Value).Concat(tags)
                .Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in found)
            {
                var s = stats.GetValueOrDefault(tag, (tag, 0, 0));
                stats[tag] = (s.Name, s.Posts + 1, s.Views + (video.ViewCount ?? 0));
            }
        }

        return stats.Values.Where(s => s.Posts > 1 && Matches(s.Name, keyword)).OrderByDescending(s => s.Posts).ThenByDescending(s => s.Views)
            .Select((s, i) => new TrendHashtag(s.Name, startRank + i, s.Posts, s.Views, null, url(s.Name))).ToList();
    }

    // ---- YouTube ----

    private async Task<IReadOnlyList<(ContentItem Video, IReadOnlyList<string> Tags)>> YouTubeTrendingAsync(TrendQuery q, int max, CancellationToken ct)
    {
        try
        {
            return await youtube.GetMostPopularAsync(q.Region ?? "VN", q.Topic, max, ct);
        }
        // Phản hồi lỗi bị cắt còn 240 ký tự nên "quotaExceeded" có thể mất; câu "exceeded your … quota" luôn nằm ở đầu.
        catch (ProviderException ex) when (ex.Kind == ProviderErrorKind.Forbidden && ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderException("youtube", ProviderErrorKind.QuotaExceeded,
                "Đã hết hạn mức YouTube Data API hôm nay (10.000 đơn vị/ngày, đặt lại lúc 0 giờ giờ Thái Bình Dương). Kết quả đã lấy vẫn xem lại được trong 30 phút.");
        }
        catch (ProviderException ex) when (ex.Message.Contains("videoChartNotFound", StringComparison.Ordinal) || ex.Message.StartsWith("HTTP 404", StringComparison.Ordinal)
                                           || ex.Message.Contains("videoCategoryId", StringComparison.Ordinal))
        {
            throw new ProviderException("youtube", ProviderErrorKind.BadResponse, "YouTube không có bảng thịnh hành cho chủ đề này ở khu vực đã chọn; chọn chủ đề khác hoặc \"Tất cả\".");
        }
    }

    private async Task<TrendResult> YouTubeVideosAsync(TrendQuery q, CancellationToken ct)
    {
        var sample = await YouTubeTrendingAsync(q, q.Keyword is null ? q.Limit : AggregateSample, ct);
        return Result(videos: [.. sample.Select(s => s.Video).Where(v => Matches(v.Title, q.Keyword)).Take(q.Limit).Select(v => new TrendVideo(v))],
            source: "YouTube Data API (thịnh hành)");
    }

    private async Task<TrendResult> YouTubeHashtagsAsync(TrendQuery q, CancellationToken ct)
    {
        var sample = await YouTubeTrendingAsync(q, AggregateSample, ct);
        var tags = CountHashtags(sample, q.Keyword, 1, t => $"https://www.youtube.com/hashtag/{Uri.EscapeDataString(t.Replace(" ", ""))}");
        return Result(hashtags: [.. tags.Take(q.Limit)], source: $"Tổng hợp từ {sample.Count} video thịnh hành YouTube",
            note: "\"Số bài\" = số video thịnh hành có hashtag/tag này; \"Lượt xem\" = tổng lượt xem các video đó.");
    }

    private async Task<TrendResult> YouTubeHashtagVideosAsync(TrendQuery q, CancellationToken ct)
    {
        var tag = q.Keyword!.TrimStart('#').Replace(" ", "");
        var info = await ytdlp.ExtractAsync("youtube", new YtDlpQuery($"https://www.youtube.com/hashtag/{Uri.EscapeDataString(tag)}", q.Limit, CookiePlatform: "youtube"), ct);
        var items = YtDlpListing.Parse("youtube", info, id => $"https://www.youtube.com/watch?v={id}", max: q.Limit).Items;
        if (items.Count == 0) throw new ProviderException("youtube", ProviderErrorKind.BadResponse, $"YouTube không có video nào cho #{tag}.");

        string? note = null;
        try
        {
            var full = (await youtube.GetVideosAsync([.. items.Select(i => i.ExternalId)], ct)).ToDictionary(v => v.ExternalId);
            items = [.. items.Select(i => full.GetValueOrDefault(i.ExternalId) ?? i)];
        }
        catch (ProviderException ex) when (ex.Kind is ProviderErrorKind.KeyMissing or ProviderErrorKind.KeyInvalid or ProviderErrorKind.Forbidden
                                               or ProviderErrorKind.QuotaExceeded or ProviderErrorKind.RateLimited)
        {
            note = "Chưa dùng được YouTube Data API (thiếu key hoặc hết hạn mức) nên chỉ có lượt xem từ trang hashtag, thiếu thích/bình luận/ngày đăng.";
        }

        return Result(videos: [.. items.Select(i => new TrendVideo(i))], source: $"youtube.com/hashtag/{tag} (yt-dlp)", note: note);
    }

    private async Task<TrendResult> YouTubeCreatorsAsync(TrendQuery q, CancellationToken ct)
    {
        var sample = await YouTubeTrendingAsync(q, AggregateSample, ct);
        var creators = Aggregate("youtube", sample.Select(s => s.Video).Where(v => Matches(v.Title, q.Keyword)), q.Limit);
        var subs = (await youtube.GetChannelsAsync([.. creators.Select(c => c.Id)], ct)).ToDictionary(c => c.Id, c => c.Subscribers);
        return Result(creators: [.. creators.Select(c => c with { Followers = subs.TryGetValue(c.Id, out var s) ? s : null })],
            source: $"Tổng hợp từ {sample.Count} video thịnh hành YouTube", note: "\"Số video\" = số video của kênh trong bảng thịnh hành.");
    }

    // ---- TikTok ----

    /// <summary>Video nổi bật: Creative Center trước (khách chỉ nhận 4), thiếu thì bù feed đề xuất tikwm cùng khu vực (feed không lọc được chủ đề).</summary>
    private async Task<(List<TrendVideo> Videos, Dictionary<string, long> Followers, string? Note)> TikTokVideosAsync(TrendQuery q, int max, CancellationToken ct)
    {
        var country = q.Region ?? "VN";
        if (q.Topic is not null && !TikTokCreativeCenter.VideoCountries.Contains(country))
            throw new ArgumentException($"Lọc chủ đề TikTok chỉ có ở {string.Join(", ", TikTokCreativeCenter.VideoCountries)}; chọn khu vực khác hoặc chủ đề \"Tất cả\".");

        var videos = new List<TrendVideo>();
        var followers = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string? ccNote = null;
        if (TikTokCreativeCenter.VideoCountries.Contains(country))
        {
            try
            {
                foreach (var (item, rate, fans) in await creativeCenter.TopVideosAsync(country, q.Topic, max, ct))
                {
                    videos.Add(new TrendVideo(item, rate));
                    if (item.AuthorId is { } author && fans is { } f) followers[author] = f;
                }

                ccNote = $"Creative Center: {videos.Count} video" + (videos.Count < max ? " (khách chưa đăng nhập chỉ xem được vài video; lưu cookie TikTok ở Cài đặt > Nền tảng để lấy thêm)." : ".");
            }
            catch (ProviderException ex) when (q.Topic is null)
            {
                ccNote = $"Creative Center lỗi ({ex.Message}).";
            }
        }

        string? feedNote = null;
        if (videos.Count < max && q.Topic is null)
        {
            var feed = await tiktok.FeedAsync(max - videos.Count, ct, country);
            var added = feed.Where(f => videos.All(v => v.Item.ExternalId != f.ExternalId)).Select(f => new TrendVideo(f)).ToList();
            videos.AddRange(added);
            feedNote = $"Feed đề xuất tikwm ({country}): {added.Count} video, không phải bảng xếp hạng chính thức.";
        }

        if (videos.Count == 0)
            throw new ProviderException("tiktok", ProviderErrorKind.BadResponse, "Không lấy được video TikTok nổi bật: Creative Center và tikwm đều không trả dữ liệu. Thử lại sau vài phút hoặc đổi mạng/proxy.");
        return (videos, followers, Join(ccNote, feedNote));
    }

    private async Task<TrendResult> TikTokVideosResultAsync(TrendQuery q, CancellationToken ct)
    {
        var (videos, _, note) = await TikTokVideosAsync(q, q.Limit, ct);
        return Result(videos: [.. videos.Where(v => Matches(v.Item.Title, q.Keyword)).OrderByDescending(v => v.Item.ViewCount ?? 0).Take(q.Limit)],
            source: "TikTok Creative Center + tikwm", note: note);
    }

    private async Task<TrendResult> TikTokHashtagsAsync(TrendQuery q, CancellationToken ct)
    {
        var country = q.Region ?? "VN";
        var industry = long.TryParse(q.Topic, out var id) ? id : (long?)null;
        var cc = await creativeCenter.HashtagsAsync(country, industry, q.Limit, ct);
        var tags = cc.Where(h => Matches(h.Name, q.Keyword)).Select(h => new TrendHashtag(h.Name, h.Rank, h.Posts, h.Views, Direction(h.Curve),
            $"https://www.tiktok.com/tag/{Uri.EscapeDataString(h.Name)}")).ToList();
        var note = $"Creative Center: {tags.Count} hashtag (7 ngày; cột Xu hướng so điểm phổ biến hôm nay với hôm trước).";

        // Creative Center chỉ cho khách vài hashtag: bù bằng hashtag hay gặp trong video nổi bật cùng khu vực (không lọc được theo ngành).
        if (tags.Count < q.Limit && industry is null)
        {
            var (videos, _, _) = await TikTokVideosAsync(q with { Keyword = null }, 40, ct);
            var extra = CountHashtags(videos.Select(v => (v.Item, (IReadOnlyList<string>)Array.Empty<string>())), q.Keyword, tags.Count + 1,
                    t => $"https://www.tiktok.com/tag/{Uri.EscapeDataString(t)}")
                .Where(e => tags.All(t => !t.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            var start = tags.Count;
            tags.AddRange(extra.Select((e, i) => e with { Rank = start + i + 1 }));
            note += $" Thêm {extra.Count} hashtag tổng hợp từ {videos.Count} video nổi bật (\"Số bài\" = số video trong mẫu có hashtag).";
        }

        return Result(hashtags: [.. tags.Take(q.Limit)], source: "TikTok Creative Center", note: note);
    }

    private static string? Direction(IReadOnlyList<double> curve)
    {
        if (curve.Count < 2) return null;
        var delta = curve[^1] - curve[^2];
        return delta switch { > 5 => "Tăng", < -5 => "Giảm", _ => "Ổn định" };
    }

    private async Task<TrendResult> TikTokCreatorsAsync(TrendQuery q, CancellationToken ct)
    {
        var (videos, followers, note) = await TikTokVideosAsync(q with { Keyword = null }, Math.Clamp(q.Limit * 2, 40, 100), ct);
        var creators = Aggregate("tiktok", videos.Select(v => v.Item).Where(v => Matches(v.Title, q.Keyword)), q.Limit);
        return Result(creators: [.. creators.Select(c => c with { Followers = followers.TryGetValue(c.Id, out var f) ? f : null })],
            source: $"Tổng hợp từ {videos.Count} video TikTok nổi bật", note: note);
    }

    // ---- Nguồn qua bộ quét sẵn có (TikTok hashtag) ----

    private async Task<TrendResult> CrawlHashtagAsync(TrendQuery q, CrawlKind kind, string source, CancellationToken ct)
    {
        var result = await crawl.CrawlAsync(new CrawlRequest(kind, q.Keyword!.TrimStart('#'), q.Limit), new CrawlOptions(q.Platform, "trend"), ct: ct);
        return Result(videos: [.. result.Items.Select(i => new TrendVideo(i))], source: source, note: result.Warning);
    }

    // ---- Douyin ----

    private async Task<TrendResult> DouyinHotAsync(TrendQuery q, CancellationToken ct)
    {
        var board = await douyin.HotBoardAsync(50, ct);
        var tags = board.Select((h, i) => new TrendHashtag(h.ExternalId, i + 1, null, h.ViewCount, h.Category switch { "HOT" => "Hot", "NEW" => "Mới", _ => null }, h.Url))
            .Where(h => Matches(h.Name, q.Keyword)).Take(q.Limit).ToList();
        return Result(hashtags: tags, source: "Bảng nổi bật Douyin");
    }

    // ---- Bilibili ----

    private static JsonNode BilibiliData(JsonNode reply, string what)
    {
        var code = JsonX.Long(reply, "code") ?? 0;
        if (code == 0) return reply["data"] ?? reply;
        throw new ProviderException("bilibili", code is -412 or -352 ? ProviderErrorKind.RateLimited : ProviderErrorKind.BadResponse,
            $"Bilibili từ chối khi lấy {what} (mã {code}{(JsonX.Str(reply, "message") is { } m ? $", {m}" : "")}); thử lại sau.");
    }

    private static ContentItem BilibiliVideo(JsonObject v, string? title = null)
    {
        var bvid = JsonX.Str(v, "bvid") ?? "";
        var pic = JsonX.Str(v, "pic");
        if (pic is not null && pic.StartsWith("//", StringComparison.Ordinal)) pic = "https:" + pic;
        return new ContentItem("bilibili", "video", bvid, title ?? JsonX.Str(v, "title"), JsonX.Str(v, "owner", "mid") ?? JsonX.Str(v, "mid"),
            JsonX.Str(v, "owner", "name") ?? JsonX.Str(v, "author"), $"https://www.bilibili.com/video/{bvid}", pic, JsonX.Str(v, "desc") ?? JsonX.Str(v, "description"),
            JsonX.FromUnixSeconds(JsonX.Long(v, "pubdate")), null, JsonX.Long(v, "stat", "view") ?? JsonX.Long(v, "play"),
            JsonX.Long(v, "stat", "like") ?? JsonX.Long(v, "like"), JsonX.Long(v, "stat", "reply") ?? JsonX.Long(v, "review"));
    }

    private async Task<List<TrendVideo>> BilibiliRankingAsync(TrendQuery q, int max, CancellationToken ct)
    {
        var rid = BilibiliCrawler.Categories.TryGetValue(q.Topic ?? "all", out var r) ? r : 0;
        var data = BilibiliData(await http.GetJsonAsync("bilibili", $"https://api.bilibili.com/x/web-interface/ranking/v2?rid={rid}&type=all",
            await bilibili.HeadersAsync(ct), ct: ct), "bảng xếp hạng");
        var videos = JsonX.Objects(data, "list").Select(v => BilibiliVideo(v)).Where(v => v.ExternalId.Length > 0 && Matches(v.Title, q.Keyword)).Take(max)
            .Select(v => new TrendVideo(v)).ToList();
        if (videos.Count == 0 && q.Keyword is null) throw new ProviderException("bilibili", ProviderErrorKind.BadResponse, "Bảng xếp hạng Bilibili rỗng.");
        return videos;
    }

    private async Task<TrendResult> BilibiliHotWordsAsync(TrendQuery q, CancellationToken ct)
    {
        var data = await http.GetJsonAsync("bilibili", "https://s.search.bilibili.com/main/hotword?limit=50", await bilibili.HeadersAsync(ct), ct: ct);
        BilibiliData(data, "từ khoá hot");
        var tags = JsonX.Objects(data, "list").Select(w => (Word: JsonX.Str(w, "show_name") ?? JsonX.Str(w, "keyword"), Item: w))
            .Where(w => w.Word is not null && Matches(w.Word, q.Keyword))
            .Select((w, i) => new TrendHashtag(w.Word!, (int)(JsonX.Long(w.Item, "pos") ?? i + 1), null, JsonX.Long(w.Item, "heat_score"), null,
                $"https://search.bilibili.com/all?keyword={Uri.EscapeDataString(JsonX.Str(w.Item, "keyword") ?? w.Word!)}"))
            .Take(q.Limit).ToList();
        return Result(hashtags: tags, source: "Từ khoá hot Bilibili");
    }

    private async Task<TrendResult> BilibiliSearchAsync(TrendQuery q, CancellationToken ct)
    {
        var headers = await bilibili.HeadersAsync(ct);
        var keyword = q.Keyword!.TrimStart('#');
        var videos = new List<TrendVideo>();
        try
        {
            for (var page = 1; videos.Count < q.Limit && page <= 4; page++)
            {
                var data = BilibiliData(await http.GetJsonAsync("bilibili", "https://api.bilibili.com/x/web-interface/search/type?" + JsonX.Query(
                [
                    new("search_type", "video"), new("keyword", keyword), new("order", "click"), new("page", page.ToString()), new("page_size", "50")
                ]), headers, ct: ct), "kết quả tìm kiếm");
                var found = JsonX.Objects(data, "result").Select(v => BilibiliVideo(v, HtmlTag().Replace(JsonX.Str(v, "title") ?? "", "")))
                    .Where(v => v.ExternalId.Length > 0).ToList();
                videos.AddRange(found.Select(v => new TrendVideo(v)));
                if (found.Count == 0) break;
            }
        }
        catch (ProviderException) when (videos.Count == 0)
        {
            // API tìm kiếm hay dính chống bot: dùng bộ quét sẵn có (thử thêm yt-dlp) trước khi báo lỗi.
            return await CrawlHashtagAsync(q, CrawlKind.Search, "Tìm kiếm Bilibili (bộ quét)", ct);
        }

        return Result(videos: [.. videos.Take(q.Limit)], source: "Tìm kiếm Bilibili (theo lượt xem)");
    }

    private async Task<TrendResult> BilibiliCreatorsAsync(TrendQuery q, CancellationToken ct)
    {
        var videos = await BilibiliRankingAsync(q with { Keyword = null }, 100, ct);
        var creators = Aggregate("bilibili", videos.Select(v => v.Item).Where(v => Matches(v.Title, q.Keyword)), q.Limit);
        var headers = await bilibili.HeadersAsync(ct);
        var withFans = new List<TrendCreator>(creators.Count);
        foreach (var (creator, index) in creators.Select((c, i) => (c, i)))
        {
            long? fans = null;
            // Mỗi kênh một lời gọi: chỉ tra 30 kênh đầu để không bị chặn chống bot.
            if (index < 30)
            {
                try
                {
                    fans = JsonX.Long(await http.GetJsonAsync("bilibili", $"https://api.bilibili.com/x/relation/stat?vmid={JsonX.Quote(creator.Id)}", headers,
                        TimeSpan.FromSeconds(8), ct), "data", "follower");
                }
                catch (ProviderException)
                {
                    // số người theo dõi chỉ để tham khảo
                }
            }

            withFans.Add(creator with { Followers = fans });
        }

        return Result(creators: withFans, source: $"Tổng hợp từ {videos.Count} video bảng xếp hạng Bilibili", note: "\"Số video\" = số video của UP chủ trong bảng xếp hạng.");
    }
}
