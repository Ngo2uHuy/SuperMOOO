namespace SuperMOOO.Social.Crawlers;

public sealed record DiscoveryQuery(string Platform, string Mode = "trending", string Query = "", string Category = "all",
    int MaxItems = 30, string SortBy = "relevance", IReadOnlyDictionary<string, string>? Filters = null);

/// <summary>Khám phá (trending/tìm kiếm/hashtag) và quét kênh lưu thư viện, đều qua CrawlService.</summary>
[Service]
public sealed class DiscoveryService(CrawlService crawl)
{
    public Task<CrawlResult> DiscoverAsync(DiscoveryQuery q, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var platform = q.Platform.Trim().ToLowerInvariant();
        var mode = q.Mode.Trim().ToLowerInvariant();
        var filters = new Dictionary<string, string>(q.Filters ?? new Dictionary<string, string>())
        {
            ["category"] = q.Category, ["sort_by"] = q.SortBy
        };

        var (kind, target) = (platform, mode) switch
        {
            ("tiktok", "hashtag") => (CrawlKind.Hashtag, q.Query.TrimStart('#')),
            ("tiktok", _) => (CrawlKind.Explore, q.Category),
            ("youtube", "search") => (CrawlKind.Search, q.Query),
            // YouTube không có nguồn xu hướng chung (Data API mostPopular theo vùng chưa làm): có từ khoá thì tìm kiếm, không thì báo rõ.
            ("youtube", _) when !string.IsNullOrWhiteSpace(q.Query) => (CrawlKind.Search, q.Query),
            ("youtube", _) => throw new ArgumentException("YouTube chỉ hỗ trợ chế độ \"search\": nhập Từ khoá (hoặc dùng \"Tìm kiếm YouTube\")."),
            ("douyin", "search") => (CrawlKind.Search, q.Query),
            ("douyin", _) => (CrawlKind.Trending, ""),
            ("xiaohongshu", _) => (CrawlKind.Explore, string.IsNullOrWhiteSpace(q.Category) || q.Category == "all" ? (q.Query.Length > 0 ? q.Query : "all") : q.Category),
            ("weibo", _) => (CrawlKind.Trending, ""),
            ("bilibili", "search") => (CrawlKind.Search, q.Query),
            ("bilibili", _) => (CrawlKind.Trending, q.Category),
            ("kuaishou", _) => (CrawlKind.Trending, ""),
            _ => throw new NotSupportedException("Nền tảng hoặc chế độ khám phá chưa được hỗ trợ.")
        };

        var runType = mode is "search" or "hashtag" ? mode : "trending";
        return crawl.CrawlAsync(new CrawlRequest(kind, target, q.MaxItems, Filters: filters),
            new CrawlOptions(platform, runType), progress, ct);
    }

    public Task<CrawlResult> ScanChannelToLibraryAsync(string url, string? platform = null, int maxItems = 50, string contentType = "videos",
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? filters = null) =>
        crawl.CrawlAsync(new CrawlRequest(CrawlKind.Channel, url, maxItems, Filters: new Dictionary<string, string>(filters ?? new Dictionary<string, string>()) { ["content_type"] = contentType }),
            new CrawlOptions(platform, "scan_channel", SaveToLibrary: true), progress, ct);
}
