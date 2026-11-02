using System.ComponentModel.DataAnnotations;
using SuperMOOO.Social.Comments;
using SuperMOOO.Social.Crawlers;
using SuperMOOO.Social.Listening;

namespace SuperMOOO.Social.Operations;

public class LimitRequest
{
    [Field("Số dòng"), Range(1, 500)]
    public int Limit { get; init; } = 50;
}

public class IdRequest
{
    [Field("Id"), Required]
    public long Id { get; init; }
}

public class YouTubeChannelDetailsRequest
{
    [Field("Kênh YouTube", Example = "@tenkenh, UC… hoặc URL /channel/"), Required]
    public string Channel { get; init; } = "";
}

public class YouTubeCommentsRequest
{
    [Field("ID video YouTube"), Required]
    public string VideoId { get; init; } = "";

    [Field("Số bình luận tối đa"), Range(1, 500)]
    public int MaxComments { get; init; } = 50;
}

public class MetaAdsRequest
{
    [Field("Từ khoá trong nội dung quảng cáo")] public string? SearchTerms { get; init; }
    [Field("ID Trang Facebook")] public string? SearchPageIds { get; init; }
    [Field("Quốc gia (2 ký tự)")] public string Country { get; init; } = "VN";
    [Field("Trạng thái", Kind = FieldKind.Choice, Choices = ["ACTIVE", "ALL", "INACTIVE"], ChoiceLabels = ["Đang hoạt động", "Tất cả", "Ngừng hoạt động"])] public string ActiveStatus { get; init; } = "ACTIVE";

    [Field("Số kết quả"), Range(1, 100)]
    public int Limit { get; init; } = 25;
}

public class MetaPageRequest
{
    [Field("ID hoặc username Trang"), Required]
    public string PageId { get; init; } = "";

    [Field("Số bài tối đa (chỉ dùng cho bài đăng)"), Range(1, 50)]
    public int Limit { get; init; } = 25;
}

public class TikTokProfileRequest
{
    [Field("Tên người dùng hoặc URL hồ sơ", Example = "@tenkenh"), Required]
    public string Target { get; init; } = "";
}

public class EntityListRequest
{
    [Field("Nền tảng")] public string? Platform { get; init; }
    [Field("Loại thực thể", Kind = FieldKind.Choice, Choices = ["", "channel", "page", "profile"], ChoiceLabels = ["Tất cả", "Kênh", "Trang", "Hồ sơ"])] public string? EntityType { get; init; }

    [Field("Số dòng"), Range(1, 500)]
    public int Limit { get; init; } = 50;
}

public class EntityKeyRequest
{
    [Field("Nền tảng"), Required] public string Platform { get; init; } = "";
    [Field("ID ngoài của thực thể"), Required] public string ExternalId { get; init; } = "";
}

public class MetricHistoryRequest
{
    [Field("ID thực thể (video/kênh)"), Required]
    public string EntityId { get; init; } = "";

    [Field("Chỉ số", Help = "views, likes, comments, followers… để trống = tất cả.")]
    public string? Metric { get; init; }

    [Field("Số điểm"), Range(1, 500)]
    public int Limit { get; init; } = 50;
}

public class RecordMetricRequest
{
    [Field("ID thực thể"), Required] public string EntityId { get; init; } = "";
    [Field("Tên thực thể")] public string? EntityTitle { get; init; }
    [Field("Nền tảng"), Required] public string Platform { get; init; } = "";
    [Field("Liên kết")] public string Url { get; init; } = "";
    [Field("Tên chỉ số"), Required] public string MetricName { get; init; } = "";
    [Field("Giá trị")] public double MetricValue { get; init; }
}

public class WatchAddRequest
{
    [Field("Tên gợi nhớ"), Required] public string Name { get; init; } = "";
    [Field("Liên kết hoặc từ khoá"), Required] public string Url { get; init; } = "";
    [Field("Nền tảng"), Required] public string Platform { get; init; } = "youtube";
    [Field("Loại", Kind = FieldKind.Choice, Choices = ["channel", "profile", "hashtag", "search"], ChoiceLabels = ["Kênh", "Hồ sơ", "Hashtag", "Tìm kiếm"])] public string TargetType { get; init; } = "channel";

    [Field("Kiểm tra mỗi (giờ)"), Range(1, 720)]
    public int FrequencyHours { get; init; } = 24;
}

public sealed record JobStarted(string JobId);
public sealed record Deleted(bool Success);
public sealed record Added(long Id);
public sealed record GuideText(string Title, string Text);

[Operation("social_listening.get_youtube_channel_details_endpoint", "YouTube kênh chi tiết", OperationGroup.Monitor)]
public sealed class YouTubeChannelDetailsOperation(YouTubeDataApiClient api, IContentStore content, MetricService metrics)
    : IOperationHandler<YouTubeChannelDetailsRequest, YouTubeChannel>
{
    public async Task<YouTubeChannel> HandleAsync(YouTubeChannelDetailsRequest r, IProgress<ProgressInfo>? p, CancellationToken ct)
    {
        var channel = await api.GetChannelAsync(r.Channel, ct);
        var item = YouTubeDataApiClient.ToItem(channel);
        await metrics.RecordItemAsync(item with { Id = await content.UpsertAsync(item, ct) }, ct);
        await metrics.RecordAsync("youtube", channel.Id, channel.Title, channel.Url, "views", channel.Views, ct);
        return channel;
    }
}

[Operation("social_listening.get_youtube_video_comments_endpoint", "YouTube video bình luận", OperationGroup.Monitor)]
public sealed class YouTubeCommentsOperation(YouTubeDataApiClient api, CommentCampaignService campaigns) : IOperationHandler<YouTubeCommentsRequest, IReadOnlyList<CommentRecord>>
{
    public async Task<IReadOnlyList<CommentRecord>> HandleAsync(YouTubeCommentsRequest r, IProgress<ProgressInfo>? p, CancellationToken ct)
    {
        var comments = await api.GetCommentsAsync(r.VideoId, r.MaxComments, ct);
        return (await campaigns.AnalyzeAndSaveAsync($"YouTube {r.VideoId}", $"https://www.youtube.com/watch?v={r.VideoId}", "youtube", "youtube_video", comments, ct)).Comments;
    }
}

[Operation("social_listening.search_meta_ads_endpoint", "Tìm kiếm Meta quảng cáo", OperationGroup.Monitor)]
public sealed class MetaAdsOperation(MetaGraphClient meta) : IOperationHandler<MetaAdsRequest, IReadOnlyList<ContentRow>>
{
    public async Task<IReadOnlyList<ContentRow>> HandleAsync(MetaAdsRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        (await meta.SearchAdsAsync(new MetaAdsQuery(r.SearchTerms, r.SearchPageIds, r.Country, r.ActiveStatus, r.Limit), ct)).Ads.Select(ContentRow.From).ToList();
}

[Operation("social_listening.get_meta_page_info_endpoint", "Meta trang thông tin", OperationGroup.Monitor)]
public sealed class MetaPageInfoOperation(MetaGraphClient meta) : IOperationHandler<MetaPageRequest, IReadOnlyList<ContentRow>>
{
    public async Task<IReadOnlyList<ContentRow>> HandleAsync(MetaPageRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        [ContentRow.From(await meta.GetPageAsync(r.PageId, ct))];
}

[Operation("social_listening.get_meta_page_posts_endpoint", "Meta trang bài đăng", OperationGroup.Monitor)]
public sealed class MetaPagePostsOperation(MetaGraphClient meta) : IOperationHandler<MetaPageRequest, IReadOnlyList<ContentRow>>
{
    public async Task<IReadOnlyList<ContentRow>> HandleAsync(MetaPageRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        (await meta.GetPagePostsAsync(r.PageId, r.Limit, ct)).Select(ContentRow.From).ToList();
}

[Operation("social_listening.get_meta_app_review_guide_endpoint", "Hướng dẫn Meta App Review", OperationGroup.Monitor)]
public sealed class MetaAppReviewGuideOperation : IOperationHandler<NoRequest, GuideText>
{
    public Task<GuideText> HandleAsync(NoRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) => Task.FromResult(new GuideText(
        "Hướng dẫn vượt Meta App Review",
        """
        Quyền cần xin: "Page Public Content Access" (đọc nội dung công khai của Trang bất kỳ; khó, cần chứng minh use case social listening) và "pages_read_engagement" (Trang do bạn hoặc khách hàng uỷ quyền; mức trung bình).
        1. Xác minh doanh nghiệp (Business Verification): giấy phép kinh doanh, sao kê ngân hàng, hoá đơn tiện ích.
        2. Hoàn thiện thông tin ứng dụng: Privacy Policy URL, Terms of Service URL, User Data Deletion Callback URL.
        3. Quay video screencast: đăng nhập, nhập URL Fanpage, cách hiển thị bài viết và chỉ số.
        4. Bản giải trình use case: chỉ dùng để lắng nghe thị trường, không lưu dữ liệu cá nhân của người dùng.
        Nếu chưa được duyệt: dùng Meta Ads Library API (không cần App Review) để theo dõi quảng cáo của đối thủ.
        """));
}

[Operation("social_listening.get_tiktok_profile_endpoint", "TikTok hồ sơ", OperationGroup.Monitor)]
public sealed class TikTokProfileOperation(TikTokCrawler tiktok) : IOperationHandler<TikTokProfileRequest, ContentRow>
{
    public async Task<ContentRow> HandleAsync(TikTokProfileRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        ContentRow.From(await tiktok.GetProfileAsync(r.Target, ct));
}

[Operation("social_listening.list_entities_endpoint", "Danh sách thực thể", OperationGroup.Monitor)]
public sealed class ListEntitiesOperation(EntityService entities) : IOperationHandler<EntityListRequest, IReadOnlyList<ContentRow>>
{
    public async Task<IReadOnlyList<ContentRow>> HandleAsync(EntityListRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        (await entities.ListAsync(r.Platform, r.EntityType, r.Limit, ct)).Select(ContentRow.From).ToList();
}

[Operation("social_listening.get_entity_endpoint", "Chi tiết thực thể", OperationGroup.Monitor)]
public sealed class GetEntityOperation(EntityService entities) : IOperationHandler<EntityKeyRequest, ContentRow>
{
    public async Task<ContentRow> HandleAsync(EntityKeyRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        ContentRow.From(await entities.GetAsync(r.Platform, r.ExternalId, ct) ?? throw new KeyNotFoundException("Không tìm thấy thực thể."));
}

[Operation("social_listening.delete_entity_endpoint", "Xoá thực thể", OperationGroup.Monitor)]
public sealed class DeleteEntityOperation(EntityService entities) : IOperationHandler<IdRequest, Deleted>
{
    public async Task<Deleted> HandleAsync(IdRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) => new(await entities.DeleteAsync(r.Id, ct));
}

[Operation("social_listening.get_metric_history_endpoint", "Lịch sử chỉ số", OperationGroup.Monitor, LegacyKeys = ["crawl.get_metric_history_endpoint"])]
public sealed class MetricHistoryOperation(MetricService metrics) : IOperationHandler<MetricHistoryRequest, IReadOnlyList<MetricSample>>
{
    public Task<IReadOnlyList<MetricSample>> HandleAsync(MetricHistoryRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        metrics.GetHistoryAsync(r.EntityId, r.Metric, r.Limit, ct);
}

[Operation("social_listening.record_metric_endpoint", "Ghi chỉ số", OperationGroup.Monitor)]
public sealed class RecordMetricOperation(MetricService metrics) : IOperationHandler<RecordMetricRequest, Deleted>
{
    public async Task<Deleted> HandleAsync(RecordMetricRequest r, IProgress<ProgressInfo>? p, CancellationToken ct)
    {
        await metrics.RecordAsync(r.Platform, r.EntityId, r.EntityTitle ?? r.EntityId, r.Url, r.MetricName, r.MetricValue, ct);
        return new Deleted(true);
    }
}

[Operation("social_listening.get_compliance_audit_logs_endpoint", "Nhật ký nền tảng", OperationGroup.Monitor, LegacyKeys = ["crawl.get_audit_logs_endpoint"])]
public sealed class AuditLogsOperation(AuditLogService audit) : IOperationHandler<LimitRequest, IReadOnlyList<AuditEntry>>
{
    public Task<IReadOnlyList<AuditEntry>> HandleAsync(LimitRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) => audit.QueryAsync(r.Limit, ct);
}

[Operation("crawl.list_watchlist", "Danh sách kênh theo dõi", OperationGroup.Monitor)]
public sealed class ListWatchlistOperation(WatchlistService watchlist) : IOperationHandler<NoRequest, IReadOnlyList<WatchTarget>>
{
    public Task<IReadOnlyList<WatchTarget>> HandleAsync(NoRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) => watchlist.ListAsync(ct);
}

[Operation("crawl.add_watchlist", "Thêm kênh theo dõi", OperationGroup.Monitor)]
public sealed class AddWatchlistOperation(WatchlistService watchlist) : IOperationHandler<WatchAddRequest, Added>
{
    public async Task<Added> HandleAsync(WatchAddRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) =>
        new(await watchlist.AddAsync(r.Name, r.Url, r.Platform, r.TargetType, r.FrequencyHours, ct));
}

[Operation("crawl.delete_watchlist", "Xoá kênh theo dõi", OperationGroup.Monitor)]
public sealed class DeleteWatchlistOperation(WatchlistService watchlist) : IOperationHandler<IdRequest, Deleted>
{
    public async Task<Deleted> HandleAsync(IdRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) => new(await watchlist.DeleteAsync(r.Id, ct));
}

[Operation("crawl.check_watchlist_item_now", "Kiểm tra kênh ngay", OperationGroup.Monitor)]
public sealed class CheckWatchlistNowOperation(WatchlistService watchlist) : IOperationHandler<IdRequest, JobStarted>
{
    public async Task<JobStarted> HandleAsync(IdRequest r, IProgress<ProgressInfo>? p, CancellationToken ct) => new(await watchlist.CheckNowAsync(r.Id, ct));
}
