using System.Text.RegularExpressions;
using SuperMOOO.Social.Listening;
using SuperMOOO.Social.Platforms;

namespace SuperMOOO.Social.Crawlers;

public static class CrawlerFilters
{
    public static string? Get(this CrawlRequest request, string key) =>
        request.Filters is not null && request.Filters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>Cơ sở chung: nhận URL theo tên miền và kiểm tra loại crawl được hỗ trợ.</summary>
public abstract class CrawlerBase(string platform, params CrawlKind[] supports) : IPlatformCrawler
{
    public string Platform => platform;
    public IReadOnlySet<CrawlKind> Supports { get; } = supports.ToHashSet();

    public Task<CrawlResult> CrawlAsync(CrawlRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!Supports.Contains(request.Kind))
            throw new NotSupportedException($"{platform} không hỗ trợ kiểu quét {request.Kind}.");
        if (string.IsNullOrWhiteSpace(request.Target) && request.Kind is not (CrawlKind.Trending or CrawlKind.Explore))
            throw new ArgumentException("Cần nhập đích cần quét (liên kết, tên hoặc từ khoá).");
        progress?.Report(new ProgressInfo(-1, $"Đang quét {platform}…"));
        return RunAsync(request, ct);
    }

    protected abstract Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct);
}

/// <summary>YouTube: Data API khi có key/token và còn hạn mức, ngược lại yt-dlp (Auto tự chuyển và ghi cảnh báo).</summary>
[Service(As = [typeof(IPlatformCrawler)])]
public sealed partial class YouTubeCrawler(IYtDlpMetadata ytdlp, YouTubeDataApiClient api)
    : CrawlerBase("youtube", CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Search)
{
    private static readonly HashSet<string> SortModes = ["relevance", "date", "viewCount", "rating"];

    [GeneratedRegex(@"^(/(?:@[^/]+|(?:channel|c|user)/[^/]+))(?:/(?:videos|shorts|streams|featured))?/?$")]
    private static partial Regex ChannelPath();

    protected override async Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct)
    {
        var useApi = request.Source != CrawlSource.Scrape;
        if (useApi)
        {
            try
            {
                return request.Kind == CrawlKind.Search ? await ApiSearchAsync(request, ct) : await ApiChannelAsync(request, ct);
            }
            catch (ProviderException ex) when (request.Source == CrawlSource.Auto && ex.Kind is ProviderErrorKind.KeyMissing
                                                   or ProviderErrorKind.QuotaExceeded or ProviderErrorKind.RateLimited
                                                   or ProviderErrorKind.KeyInvalid or ProviderErrorKind.Forbidden)
            {
                var scraped = request.Kind == CrawlKind.Search ? await ScrapeSearchAsync(request, ct) : await ScrapeChannelAsync(request, ct);
                return scraped with { Warning = $"Data API không dùng được ({ex.Message}); đã chuyển sang yt-dlp." };
            }
        }

        return request.Kind == CrawlKind.Search ? await ScrapeSearchAsync(request, ct) : await ScrapeChannelAsync(request, ct);
    }

    internal static string ChannelUrl(string target, string? contentType)
    {
        var url = target.Trim();
        if (!url.Contains("://")) url = url.StartsWith('@') ? $"https://www.youtube.com/{url}" : $"https://www.youtube.com/channel/{url}";
        if (contentType is "videos" or "shorts" or "streams" && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host is "youtube.com" or "www.youtube.com" or "m.youtube.com" && ChannelPath().Match(uri.AbsolutePath) is { Success: true } m)
            url = new UriBuilder(uri) { Path = $"{m.Groups[1].Value}/{contentType}" }.Uri.ToString();
        return url;
    }

    private async Task<CrawlResult> ScrapeChannelAsync(CrawlRequest request, CancellationToken ct)
    {
        var url = ChannelUrl(request.Target, request.Get("content_type") ?? "videos");
        var info = await ytdlp.ExtractAsync("youtube", new YtDlpQuery(url, request.MaxItems,
            Flat: !NeedsDates(request), CookiePlatform: "youtube"), ct);
        return YtDlpListing.ToResult("youtube", request.Kind, info, id => $"https://www.youtube.com/watch?v={id}", request.MaxItems);
    }

    private async Task<CrawlResult> ScrapeSearchAsync(CrawlRequest request, CancellationToken ct)
    {
        var sort = request.Get("sort_by") switch
        {
            "latest" => "date", "popular" => "viewCount", "oldest" or "default" or null => "relevance",
            var value => value
        };
        if (!SortModes.Contains(sort)) throw new ArgumentException("Kiểu sắp xếp YouTube không được hỗ trợ.");
        var query = $"ytsearch{request.MaxItems}:{request.Target}";
        if (sort != "relevance")
        {
            // Protobuf của trang kết quả: trường 1 = kiểu sắp xếp, trường 2 = lọc video.
            var code = sort switch { "date" => 2, "viewCount" => 3, _ => 1 };
            query = "https://www.youtube.com/results?" + JsonX.Query([new("search_query", request.Target),
                new("sp", Convert.ToBase64String([8, (byte)code, 18, 2, 16, 1]))]);
        }

        var info = await ytdlp.ExtractAsync("youtube", new YtDlpQuery(query, request.MaxItems,
            Flat: !NeedsDates(request), CookiePlatform: "youtube"), ct);
        return YtDlpListing.ToResult("youtube", request.Kind, info, id => $"https://www.youtube.com/watch?v={id}", request.MaxItems);
    }

    private static bool NeedsDates(CrawlRequest request) =>
        request.Get("date_from") is not null || request.Get("date_to") is not null || request.Get("sort_by") is "latest" or "oldest";

    private async Task<CrawlResult> ApiChannelAsync(CrawlRequest request, CancellationToken ct)
    {
        var channel = await api.GetChannelAsync(request.Target, ct);
        var items = channel.UploadsPlaylistId is null ? [] : await api.GetVideosAsync(await api.GetPlaylistVideoIdsAsync(channel.UploadsPlaylistId, request.MaxItems, ct), ct);
        return new CrawlResult("youtube", request.Kind, YouTubeDataApiClient.ToItem(channel), items);
    }

    private async Task<CrawlResult> ApiSearchAsync(CrawlRequest request, CancellationToken ct)
    {
        var sort = request.Get("sort_by") switch
        {
            "latest" => "date", "popular" => "viewCount", "oldest" or "default" or null => "relevance",
            var value => value
        };
        if (!SortModes.Contains(sort)) throw new ArgumentException("Kiểu sắp xếp YouTube không được hỗ trợ.");
        var ids = await api.SearchVideoIdsAsync(request.Target, request.MaxItems, sort, ct);
        return new CrawlResult("youtube", request.Kind, null, await api.GetVideosAsync(ids, ct));
    }
}
