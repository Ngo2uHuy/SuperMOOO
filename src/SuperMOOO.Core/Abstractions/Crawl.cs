namespace SuperMOOO.Core.Abstractions;

public enum CrawlKind { Channel, Profile, Search, Hashtag, Explore, Trending, Comments }
public enum CrawlSource { Auto, Scrape, OfficialApi, ThirdParty }
public sealed record CrawlRequest(CrawlKind Kind, string Target, int MaxItems = 50, CrawlSource Source = CrawlSource.Auto,
    IReadOnlyDictionary<string, string>? Filters = null, bool SaveEntities = false);
public sealed record CrawlResult(string Platform, CrawlKind Kind, ContentItem? Owner, IReadOnlyList<ContentItem> Items, string? Warning = null);

public interface IPlatformCrawler
{
    string Platform { get; }    // youtube tiktok facebook instagram douyin xiaohongshu weibo bilibili kuaishou
    IReadOnlySet<CrawlKind> Supports { get; }
    Task<CrawlResult> CrawlAsync(CrawlRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct);
}
