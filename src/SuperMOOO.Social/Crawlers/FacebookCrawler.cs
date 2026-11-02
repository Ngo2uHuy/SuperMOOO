namespace SuperMOOO.Social.Crawlers;

/// <summary>Fanpage/video Facebook qua yt-dlp (cookie Facebook nếu đã lưu).</summary>
[Service(As = [typeof(IPlatformCrawler)])]
public sealed class FacebookCrawler(IYtDlpMetadata ytdlp) : CrawlerBase("facebook", CrawlKind.Channel, CrawlKind.Profile)
{
    protected override async Task<CrawlResult> RunAsync(CrawlRequest request, CancellationToken ct)
    {
        var url = request.Target.Contains("://") ? request.Target.Trim() : $"https://www.facebook.com/{request.Target.Trim().TrimStart('@', '/')}";
        var info = await ytdlp.ExtractAsync("facebook", new YtDlpQuery(url, request.MaxItems, CookiePlatform: "facebook"), ct);
        return YtDlpListing.ToResult("facebook", request.Kind, info, id => $"https://www.facebook.com/watch/?v={id}", request.MaxItems, "page");
    }
}
