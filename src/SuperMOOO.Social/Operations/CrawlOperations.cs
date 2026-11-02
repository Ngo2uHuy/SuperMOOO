using System.ComponentModel.DataAnnotations;
using SuperMOOO.Social.Comments;
using SuperMOOO.Social.Crawlers;
using SuperMOOO.Social.Reports;

namespace SuperMOOO.Social.Operations;

// ---- Request / kết quả dùng chung ----

public class ContentFilterRequest : IValidatableObject
{
    [Field("Từ ngày", Example = "2026-01-01", Help = "Ngày đăng, yyyy-MM-dd; để trống = không giới hạn.")]
    public string? DateFrom { get; init; }

    [Field("Đến ngày", Example = "2026-12-31", Help = "Tính cả ngày này; mục thiếu ngày đăng bị loại khi lọc ngày.")]
    public string? DateTo { get; init; }

    [Field("Sắp xếp", Kind = FieldKind.Choice, Choices = ["default", "latest", "oldest", "popular"], ChoiceLabels = ["Mặc định", "Mới nhất", "Cũ nhất", "Phổ biến (lượt xem)"], Help = "Lọc và sắp xếp trong số mục quét được, không phải toàn bộ kênh. Mục thiếu chỉ số xếp cuối.")]
    public string SortBy { get; init; } = "default";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        try { ContentFilters.Validate(CrawlOps.Filters(this)); }
        catch (ArgumentException ex) { return [new ValidationResult(ex.Message)]; }
        return [];
    }
}

public class ChannelRequest : ContentFilterRequest
{
    [Field("Liên kết hoặc tên kênh / trang", Example = "https://www.youtube.com/@tenkenh hoặc @tenkenh"), Required]
    public string Url { get; init; } = "";

    [Field("Số mục tối đa"), Range(1, 500)]
    public int MaxItems { get; init; } = 50;

    [Field("Loại nội dung YouTube", Kind = FieldKind.Choice, Choices = ["videos", "shorts", "streams"], ChoiceLabels = ["Video thông thường", "Shorts (video ngắn)", "Livestream (phát trực tiếp)"], Help = "Chỉ áp dụng khi quét YouTube."), RegularExpression("^(videos|shorts|streams)$")]
    public string ContentType { get; init; } = "videos";
}

public class KeywordRequest : ContentFilterRequest
{
    [Field("Từ khoá / hashtag"), Required]
    public string Keyword { get; init; } = "";

    [Field("Số mục tối đa"), Range(1, 200)]
    public int MaxItems { get; init; } = 30;

}

public class ExploreRequest : ContentFilterRequest
{
    [Field("Chuyên mục / chủ đề", Kind = FieldKind.Category, Help = "Chọn chuyên mục như thanh chuyên mục của nền tảng; \"Tất cả\" = không lọc.")]
    public string Category { get; init; } = "all";

    [Field("Số mục tối đa"), Range(1, 200)]
    public int MaxItems { get; init; } = 30;
}

public class UrlRequest
{
    [Field("Liên kết", Kind = FieldKind.Url), Required]
    public string Url { get; init; } = "";
}

public class CommentRequest
{
    [Field("Liên kết video / bài viết", Kind = FieldKind.Url), Required]
    public string Url { get; init; } = "";

    [Field("Số bình luận tối đa"), Range(1, 20000)]
    public int MaxComments { get; init; } = 5000;
}

public class NoRequest;

public sealed record ContentRow(string Platform, string Kind, string? Title, string? Author, string? Url, string? Duration, long? Views, long? Likes,
    long? Comments, string? Published, string? Category, long Id, string? Thumbnail)
{
    public static ContentRow From(ContentItem i) => new(i.Platform, i.Kind, i.Title, i.AuthorName, i.Url,
        i.Duration is { } d ? d.ToString(d.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss") : null, i.ViewCount, i.LikeCount, i.CommentCount,
        i.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), i.Category, i.Id, i.ThumbnailUrl);
}

public sealed record ExportResult(IReadOnlyList<string> Files, int Rows);

internal static class CrawlOps
{
    public static async Task<IReadOnlyList<ContentRow>> RunAsync(CrawlService crawl, CrawlKind kind, string target, int max, string? platform,
        IProgress<ProgressInfo>? progress, CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null, bool saveEntities = false,
        string runType = "scan_channel")
    {
        var result = await crawl.CrawlAsync(new CrawlRequest(kind, target, max, Filters: filters, SaveEntities: saveEntities),
            new CrawlOptions(platform, runType), progress, ct);
        return result.Items.Select(ContentRow.From).ToList();
    }

    public static Dictionary<string, string> Filters(ContentFilterRequest r) => new()
    {
        ["date_from"] = r.DateFrom ?? "", ["date_to"] = r.DateTo ?? "", ["sort_by"] = r.SortBy
    };

    public static Dictionary<string, string> Filters(ChannelRequest r)
    {
        var filters = Filters((ContentFilterRequest)r);
        filters["content_type"] = r.ContentType;
        return filters;
    }

    public static Dictionary<string, string> Filters(KeywordRequest r) => Filters((ContentFilterRequest)r);

    public static Dictionary<string, string> Filters(ExploreRequest r)
    {
        var filters = Filters((ContentFilterRequest)r);
        filters["category"] = r.Category;
        filters["topic"] = r.Category;
        return filters;
    }

    public static string OutputPath(ISettingsStore settings, string name)
    {
        var root = settings.Get<PathsSettings>("paths").OutputRoot;
        return Path.Combine(string.IsNullOrWhiteSpace(root) ? AppPaths.DefaultOutputRoot : root, "crawl", $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }
}

// ---- Quét kênh / tìm kiếm / khám phá ----

[Operation("crawl.crawl_channel", "Quét kênh đa nền tảng", OperationGroup.Crawl)]
public sealed class CrawlChannelOperation(CrawlService crawl) : IOperationHandler<ChannelRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ChannelRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Channel, r.Url, r.MaxItems, null, p, ct, CrawlOps.Filters(r));
}

[Operation("social_listening.crawl_youtube_channel_endpoint", "Quét YouTube kênh", OperationGroup.Crawl, LegacyKeys = ["crawl.crawl_youtube_channel_endpoint"])]
public sealed class YouTubeChannelOperation(CrawlService crawl) : IOperationHandler<ChannelRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ChannelRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Channel, r.Url, r.MaxItems, "youtube", p, ct, CrawlOps.Filters(r), saveEntities: true);
}

[Operation("social_listening.search_youtube_endpoint", "Tìm kiếm YouTube", OperationGroup.Crawl, LegacyKeys = ["crawl.search_youtube_endpoint"])]
public sealed class YouTubeSearchOperation(CrawlService crawl) : IOperationHandler<KeywordRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(KeywordRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Search, r.Keyword, r.MaxItems, "youtube", p, ct, CrawlOps.Filters(r), runType: "search");
}

[Operation("social_listening.crawl_tiktok_channel_endpoint", "Quét TikTok kênh", OperationGroup.Crawl, LegacyKeys = ["crawl.crawl_tiktok_channel_endpoint"])]
public sealed class TikTokChannelOperation(CrawlService crawl) : IOperationHandler<ChannelRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ChannelRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Channel, r.Url, r.MaxItems, "tiktok", p, ct, CrawlOps.Filters(r), saveEntities: true);
}

[Operation("social_listening.crawl_tiktok_hashtag_endpoint", "Quét TikTok hashtag", OperationGroup.Crawl, LegacyKeys = ["crawl.crawl_tiktok_hashtag_endpoint"])]
public sealed class TikTokHashtagOperation(CrawlService crawl) : IOperationHandler<KeywordRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(KeywordRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Hashtag, r.Keyword, r.MaxItems, "tiktok", p, ct, CrawlOps.Filters(r), runType: "hashtag");
}

[Operation("crawl.crawl_tiktok_explore_endpoint", "Quét TikTok khám phá", OperationGroup.Crawl)]
public sealed class TikTokExploreOperation(CrawlService crawl) : IOperationHandler<ExploreRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ExploreRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Explore, r.Category, r.MaxItems, "tiktok", p, ct, CrawlOps.Filters(r), runType: "discover");
}

[Operation("crawl.crawl_facebook_page_endpoint", "Quét Facebook trang", OperationGroup.Crawl)]
public sealed class FacebookPageOperation(CrawlService crawl) : IOperationHandler<ChannelRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ChannelRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Channel, r.Url, r.MaxItems, "facebook", p, ct, CrawlOps.Filters(r));
}

[Operation("crawl.crawl_instagram_profile_endpoint", "Quét Instagram hồ sơ", OperationGroup.Crawl)]
public sealed class InstagramProfileOperation(CrawlService crawl) : IOperationHandler<ChannelRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ChannelRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Profile, r.Url, r.MaxItems, "instagram", p, ct, CrawlOps.Filters(r));
}

[Operation("crawl.crawl_xiaohongshu_explore_endpoint", "Quét Xiaohongshu khám phá", OperationGroup.Crawl)]
public sealed class XiaohongshuExploreOperation(CrawlService crawl) : IOperationHandler<ExploreRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ExploreRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Explore, r.Category, r.MaxItems, "xiaohongshu", p, ct, CrawlOps.Filters(r), runType: "discover");
}

[Operation("crawl.crawl_xiaohongshu_profile_endpoint", "Quét Xiaohongshu hồ sơ", OperationGroup.Crawl)]
public sealed class XiaohongshuProfileOperation(CrawlService crawl) : IOperationHandler<ChannelRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ChannelRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Profile, r.Url, r.MaxItems, "xiaohongshu", p, ct, CrawlOps.Filters(r));
}

[Operation("crawl.crawl_douyin_channel_endpoint", "Quét Douyin kênh", OperationGroup.Crawl)]
public sealed class DouyinChannelOperation(CrawlService crawl) : IOperationHandler<ChannelRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ChannelRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Channel, r.Url, r.MaxItems, "douyin", p, ct, CrawlOps.Filters(r));
}

[Operation("crawl.search_douyin_endpoint", "Tìm kiếm Douyin", OperationGroup.Crawl)]
public sealed class DouyinSearchOperation(CrawlService crawl) : IOperationHandler<KeywordRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(KeywordRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Search, r.Keyword, r.MaxItems, "douyin", p, ct, CrawlOps.Filters(r), runType: "search");
}

[Operation("crawl.get_douyin_hot_board_endpoint", "Douyin bảng nổi bật", OperationGroup.Crawl)]
public sealed class DouyinHotBoardOperation(CrawlService crawl) : IOperationHandler<ExploreRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ExploreRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Trending, "", r.MaxItems, "douyin", p, ct, CrawlOps.Filters(r), runType: "trending");
}

[Operation("crawl.crawl_weibo_user_endpoint", "Quét Weibo người dùng", OperationGroup.Crawl)]
public sealed class WeiboUserOperation(CrawlService crawl) : IOperationHandler<ChannelRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ChannelRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Profile, r.Url, r.MaxItems, "weibo", p, ct, CrawlOps.Filters(r));
}

[Operation("crawl.get_weibo_hot_search_endpoint", "Weibo nổi bật tìm kiếm", OperationGroup.Crawl)]
public sealed class WeiboHotSearchOperation(CrawlService crawl) : IOperationHandler<ExploreRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ExploreRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Trending, "", r.MaxItems, "weibo", p, ct, CrawlOps.Filters(r), runType: "trending");
}

[Operation("crawl.crawl_bilibili_space_endpoint", "Quét Bilibili kênh", OperationGroup.Crawl)]
public sealed class BilibiliSpaceOperation(CrawlService crawl) : IOperationHandler<ChannelRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ChannelRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Channel, r.Url, r.MaxItems, "bilibili", p, ct, CrawlOps.Filters(r));
}

[Operation("crawl.search_bilibili_endpoint", "Tìm kiếm Bilibili", OperationGroup.Crawl)]
public sealed class BilibiliSearchOperation(CrawlService crawl) : IOperationHandler<KeywordRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(KeywordRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Search, r.Keyword, r.MaxItems, "bilibili", p, ct, CrawlOps.Filters(r), runType: "search");
}

[Operation("crawl.get_bilibili_ranking_endpoint", "Bilibili xếp hạng", OperationGroup.Crawl)]
public sealed class BilibiliRankingOperation(CrawlService crawl) : IOperationHandler<ExploreRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ExploreRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Trending, "", r.MaxItems, "bilibili", p, ct, CrawlOps.Filters(r), runType: "trending");
}

[Operation("crawl.crawl_kuaishou_trending_endpoint", "Quét Kuaishou trending", OperationGroup.Crawl)]
public sealed class KuaishouTrendingOperation(CrawlService crawl) : IOperationHandler<ExploreRequest, IReadOnlyList<ContentRow>>
{
    public Task<IReadOnlyList<ContentRow>> HandleAsync(ExploreRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        CrawlOps.RunAsync(crawl, CrawlKind.Trending, "", r.MaxItems, "kuaishou", p, ct, CrawlOps.Filters(r), runType: "trending");
}

[Operation("crawl.extract_kuaishou_video_endpoint", "Tách Kuaishou video", OperationGroup.Crawl)]
public sealed class KuaishouExtractOperation(KuaishouCrawler kuaishou) : IOperationHandler<UrlRequest, ContentItem>
{
    public Task<ContentItem> HandleAsync(UrlRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) => kuaishou.ResolveVideoAsync(r.Url, ct);
}

// ---- Khám phá / quét lưu thư viện ----

public class DiscoverRequest : ContentFilterRequest
{
    [Field("Nền tảng", Kind = FieldKind.Choice, Choices = ["tiktok", "youtube", "douyin", "xiaohongshu", "weibo", "bilibili", "kuaishou"],
        ChoiceLabels = ["TikTok", "YouTube", "Douyin", "Xiaohongshu", "Weibo", "Bilibili", "Kuaishou"]), Required]
    public string Platform { get; init; } = "tiktok";

    [Field("Chế độ", Kind = FieldKind.Choice, Choices = ["trending", "search", "hashtag"], ChoiceLabels = ["Xu hướng", "Tìm kiếm", "Hashtag"])]
    public string Mode { get; init; } = "trending";

    [Field("Từ khoá")] public string Query { get; init; } = "";
    [Field("Chuyên mục", Kind = FieldKind.Category, Help = "Chọn theo nền tảng ở trên; nền tảng không có chuyên mục thì bỏ qua.")] public string Category { get; init; } = "all";

    [Field("Số mục tối đa"), Range(1, 200)]
    public int MaxItems { get; init; } = 30;

}

public class ScanRequest : ContentFilterRequest
{
    [Field("Liên kết kênh", Example = "https://www.youtube.com/@tenkenh"), Required]
    public string Url { get; init; } = "";

    [Field("Nền tảng (để trống = tự nhận)")] public string? Platform { get; init; }

    [Field("Số mục tối đa"), Range(1, 500)]
    public int MaxItems { get; init; } = 50;

    [Field("Loại nội dung YouTube", Kind = FieldKind.Choice, Choices = ["videos", "shorts", "streams"], ChoiceLabels = ["Video thông thường", "Shorts (video ngắn)", "Livestream (phát trực tiếp)"], Help = "Chỉ áp dụng khi quét YouTube."), RegularExpression("^(videos|shorts|streams)$")] public string ContentType { get; init; } = "videos";
}

[Operation("operations.discover", "Khám phá nội dung", OperationGroup.Crawl)]
public sealed class DiscoverOperation(DiscoveryService discovery) : IOperationHandler<DiscoverRequest, IReadOnlyList<ContentRow>>
{
    public async Task<IReadOnlyList<ContentRow>> HandleAsync(DiscoverRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        (await discovery.DiscoverAsync(new DiscoveryQuery(r.Platform, r.Mode, r.Query, r.Category, r.MaxItems, r.SortBy, CrawlOps.Filters(r)), p, ct)).Items.Select(ContentRow.From).ToList();
}

[Operation("operations.scan", "Quét kênh và lưu thư viện", OperationGroup.Crawl)]
public sealed class ScanOperation(DiscoveryService discovery) : IOperationHandler<ScanRequest, IReadOnlyList<ContentRow>>
{
    public async Task<IReadOnlyList<ContentRow>> HandleAsync(ScanRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        (await discovery.ScanChannelToLibraryAsync(r.Url, r.Platform, r.MaxItems, r.ContentType, p, ct, CrawlOps.Filters(r))).Items.Select(ContentRow.From).ToList();
}

// ---- Bình luận ----

[Operation("crawl.crawl_comment", "Quét bình luận", OperationGroup.Crawl)]
public sealed class CrawlCommentOperation(CommentCampaignService campaigns) : IOperationHandler<CommentRequest, IReadOnlyList<CommentRecord>>
{
    public async Task<IReadOnlyList<CommentRecord>> HandleAsync(CommentRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        (await campaigns.CrawlAsync(r.Url, r.MaxComments, p, ct)).Comments;
}

public class ExportCommentsRequest : CommentRequest
{
    [Field("Lưu ra file (để trống = thư mục kết quả)", Kind = FieldKind.File)]
    public string? OutputPath { get; init; }
}

[Operation("crawl.export_comments_excel", "Xuất bình luận ra Excel", OperationGroup.Crawl)]
public sealed class ExportCommentsOperation(CommentCampaignService campaigns, ReportExporter exporter, ISettingsStore settings)
    : IOperationHandler<ExportCommentsRequest, ExportResult>
{
    public async Task<ExportResult> HandleAsync(ExportCommentsRequest r, IProgress<ProgressInfo>? p, CancellationToken ct)
    {
        var result = await campaigns.CrawlAsync(r.Url, r.MaxComments, p, ct);
        var path = string.IsNullOrWhiteSpace(r.OutputPath) ? CrawlOps.OutputPath(settings, $"comments_{result.Platform}") : r.OutputPath;
        return new ExportResult([await exporter.ExportCommentsAsync(result.Comments, result.Platform, path, ct)], result.Comments.Count);
    }
}
