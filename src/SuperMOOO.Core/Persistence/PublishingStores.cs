namespace SuperMOOO.Core.Persistence;

using SuperMOOO.Core.Publishing;

public sealed record FileHash(string Sha256, long SizeBytes, bool FromCache);

public interface IFileHasher
{
    /// <summary>SHA-256 dạng stream; dùng cache fingerprint (size + last-write) để khỏi hash lại file không đổi.</summary>
    Task<FileHash> HashAsync(string path, CancellationToken ct);
}

public interface ITokenVault
{
    Task SaveAsync(string key, string value, CancellationToken ct = default);
    Task<string?> TryGetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed record MediaSource(MediaKind Kind, string Path, string? ExpectedSha256 = null, long? ExpectedSize = null);

public sealed record StoredMedia(MediaKind Kind, string StoredPath, long SizeBytes, string Sha256, string OriginalPath);

public interface IMediaStore
{
    /// <summary>Đưa media vào kho (hard link nếu cùng volume, ngược lại copy + verify sha256). Lỗi giữa chừng tự dọn.</summary>
    Task<IReadOnlyList<StoredMedia>> StoreAsync(string contentItemId, IReadOnlyList<MediaSource> sources, CancellationToken ct);

    void Discard(string contentItemId);
}

public interface IFingerprintStore
{
    Task<(long SizeBytes, DateTimeOffset LastWriteUtc, string Sha256)?> GetAsync(string path, CancellationToken ct);
    Task UpsertAsync(string path, long sizeBytes, DateTimeOffset lastWriteUtc, string sha256, CancellationToken ct);
}

public interface ISourceRepository
{
    Task<IReadOnlyList<ContentSource>> ListAsync(CancellationToken ct);
    Task<ContentSource?> GetAsync(string id, CancellationToken ct);
    Task UpsertAsync(ContentSource source, CancellationToken ct);
    Task<bool> RemoveAsync(string id, CancellationToken ct);
}

public abstract record AddContentResult
{
    public sealed record Added : AddContentResult;
    public sealed record Duplicate(string ExistingId) : AddContentResult;
}

public sealed record ContentDetail(FinishedContent Item, IReadOnlyList<MediaAsset> Assets);

/// <summary>Sửa metadata nội dung; null = giữ nguyên.</summary>
public sealed record ContentMetadataUpdate(string? Title, string? Caption, string? Language, string? TagsJson, bool? IsAiGenerated, bool? IsReupload,
    ContentStatus? Status);

/// <summary>Một lần đăng của nội dung (kênh nào, trạng thái gì).</summary>
public sealed record ContentPostRow(string PublicationId, string DestinationTitle, SocialPlatform Platform, PublicationStatus Status,
    DateTimeOffset? ScheduledUtc, DateTimeOffset? PublishedUtc, string? RemoteUrl);

public interface IContentRepository
{
    Task<string?> FindBySourceAsync(string sourceId, string sourceContentId, CancellationToken ct);
    Task<string?> FindBySha256Async(string sha256, CancellationToken ct);
    /// <summary>Ghi item + assets trong 1 transaction; trùng (source,contentId) hoặc sha256 → Duplicate.</summary>
    Task<AddContentResult> AddAsync(FinishedContent item, IReadOnlyList<MediaAsset> assets, CancellationToken ct);
    Task<(IReadOnlyList<(FinishedContent Item, string? ThumbnailPath)> Items, int Total)> QueryAsync(
        string? search, string? sourceId, int page, int pageSize, CancellationToken ct, ContentStatus? status = null);
    Task<ContentDetail?> GetAsync(string id, CancellationToken ct);
    /// <summary>Nội dung Ready (planner chọn bài).</summary>
    Task<IReadOnlyList<FinishedContent>> ListReadyAsync(CancellationToken ct);
    /// <summary>Theo đường dẫn gốc lúc nhập (không phân biệt hoa/thường).</summary>
    Task<string?> FindByOriginalPathAsync(string path, CancellationToken ct);
    /// <summary>Sửa nhãn / cờ nội dung AI; tham số null giữ nguyên. false nếu không có nội dung.</summary>
    Task<bool> UpdateAsync(string id, string? tagsJson, bool? isAiGenerated, CancellationToken ct);
    Task<bool> UpdateMetadataAsync(string id, ContentMetadataUpdate update, CancellationToken ct);
    /// <summary>Lịch sử đăng của một nội dung, mới nhất trước.</summary>
    Task<IReadOnlyList<ContentPostRow>> ListPostsAsync(string contentId, CancellationToken ct);
    /// <summary>Đường dẫn media (khoá chuẩn hoá chữ thường) của nội dung còn bài chưa đăng xong (chờ duyệt/lịch/đang đăng); CacheJanitor không được xoá.</summary>
    Task<IReadOnlySet<string>> ListPendingMediaPathsAsync(CancellationToken ct);
}

public interface IActionRequiredRepository
{
    /// <summary>Không tạo thêm khi đã có mục chưa xử lý cùng (kind, refId); trả id mục hiện hành.</summary>
    Task<string> RaiseAsync(string kind, string refId, string title, string detail, CancellationToken ct);
    Task<bool> ResolveAsync(string id, CancellationToken ct);
    Task<int> ResolveByRefAsync(string kind, string refId, CancellationToken ct);
    Task<(IReadOnlyList<ActionRequired> Items, int Total)> ListAsync(bool includeResolved, int page, int pageSize, CancellationToken ct);
    Task<IReadOnlyList<ActionRequired>> ListOpenByKindAsync(string kind, CancellationToken ct);
    Task<int> CountOpenAsync(CancellationToken ct);
}

/// <summary>Cấu hình đăng bài dạng khoá/giá trị (khoá ở <see cref="SettingKeys"/>), lưu trong section "publishing" của ISettingsStore.</summary>
public interface IPublishSettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}

public interface IConnectionRepository
{
    Task<IReadOnlyList<SocialConnection>> ListAsync(CancellationToken ct);
    Task<SocialConnection?> GetAsync(string id, CancellationToken ct);
    /// <summary>Thêm/sửa thông tin người dùng nhập; không đụng các cột OAuth (api_status, token...).</summary>
    Task UpsertAsync(SocialConnection connection, CancellationToken ct);
    Task<bool> RemoveAsync(string id, CancellationToken ct);
    Task UpdateOAuthAsync(string id, ApiStatus status, string? provider, string? externalAccountId, string? scopes,
        DateTimeOffset? tokenExpiresUtc, string? statusDetail, DateTimeOffset now, CancellationToken ct);
    Task SetApiStatusAsync(string id, ApiStatus status, string? statusDetail, DateTimeOffset now, CancellationToken ct);
    Task SetTokenExpiryAsync(string id, DateTimeOffset? tokenExpiresUtc, DateTimeOffset now, CancellationToken ct);
    Task<SocialConnection?> FindByExternalAsync(string provider, string externalAccountId, CancellationToken ct);
}

/// <param name="Platform">Nền tảng của đích khi khác nền tảng của lần đồng bộ (Meta: Page Facebook + Instagram trong một kết nối).</param>
public sealed record DestinationSnapshot(
    string RemoteId, string Title, string? Url, string? UploadsPlaylistId, string AttributesJson, string CapabilitiesJson,
    SocialPlatform? Platform = null);

public interface IDestinationRepository
{
    Task<IReadOnlyList<SocialDestination>> ListAsync(string? connectionId, bool includeRemoved, CancellationToken ct);
    Task<SocialDestination?> GetAsync(string id, CancellationToken ct);
    /// <summary>Upsert theo (connection_id, remote_id) trong 1 transaction; kênh không còn trong <paramref name="current"/> → removed_utc (không xóa vì publication còn tham chiếu).</summary>
    Task SyncAsync(string connectionId, SocialPlatform platform, IReadOnlyList<DestinationSnapshot> current, DateTimeOffset now, CancellationToken ct);
}

public sealed record PublicationQuery(
    PublicationStatus? Status, string? DestinationId, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, int Page, int PageSize,
    string? CampaignId = null);

public sealed record PublicationRow(
    Publication Publication, string ContentTitle, string DestinationTitle, string ConnectionId, string ConnectionName,
    SocialPlatform Platform, int Attempts, string? CampaignName = null);

/// <summary>Bài đang chiếm chỗ trên kênh (đã/đang/sẽ đăng) dùng cho đánh giá rủi ro.</summary>
public sealed record RiskHistoryRow(
    string PublicationId, string ConnectionId, DateTimeOffset AtUtc, string ContentItemId, string VideoSha256, string Description,
    string DestinationId = "");

public sealed record PublishSummary(int Today, int TodayPublished, int Failed, int NeedsAction, int Unknown, int InProgress);

/// <summary>
/// Đưa publication về hàng đợi: đổi trạng thái có điều kiện, bỏ job cũ (job_id = NULL) và đặt mốc điều phối trong 1 transaction.
/// Bộ điều phối (Quartz) xếp job khi tới <see cref="DispatchUtc"/>; "Đăng ngay" thì service tự xếp rồi gắn bằng TryBindJobAsync.
/// </summary>
/// <param name="DispatchUtc">Mốc sớm nhất được xếp job upload.</param>
/// <param name="ResetUpload">Bỏ session/byte/mốc chunk cuối (đăng lại có chủ ý).</param>
/// <param name="Attach">Gắn video remote (người dùng nhập URL).</param>
/// <param name="Schedule">Cập nhật giờ đăng (lên lịch bài nháp).</param>
public sealed record PublicationRequeue(
    string PublicationId, IReadOnlyCollection<PublicationStatus> From, PublicationStatus To, DateTimeOffset DispatchUtc,
    bool ResetUpload = false, RemotePost? Attach = null, (DateTimeOffset? ScheduledUtc, bool Native)? Schedule = null,
    bool IntentionalRepost = false);

public interface IPublicationRepository
{
    /// <summary>
    /// Tạo nhiều publication trong 1 transaction: lỗi một bản ghi thì không bản nào được tạo. Bài Scheduled lấy mốc điều phối từ
    /// <paramref name="dispatchUtc"/> (theo id; thiếu = giờ hẹn, hoặc ngay nếu đăng theo lịch của nền tảng).
    /// </summary>
    Task CreateBatchAsync(IReadOnlyList<Publication> publications, IReadOnlyDictionary<string, DateTimeOffset> dispatchUtc, CancellationToken ct);

    /// <summary>Bài Scheduled/RetryScheduled chưa có job và đã tới mốc điều phối, cũ trước.</summary>
    Task<IReadOnlyList<Publication>> ListDispatchableAsync(DateTimeOffset nowUtc, int limit, CancellationToken ct);
    /// <summary>Gắn job upload cho bài nếu chưa có job (CAS job_id IS NULL hoặc đã là <paramref name="jobId"/>); false = job khác đã giữ bài.</summary>
    Task<bool> TryBindJobAsync(string id, string jobId, CancellationToken ct);
    /// <summary>Bỏ gắn job (job kết thúc mà bài vẫn chờ điều phối lại) nếu job hiện tại còn là <paramref name="jobId"/>.</summary>
    Task<bool> ReleaseJobAsync(string id, string jobId, DateTimeOffset dispatchUtc, CancellationToken ct);
    /// <summary>Bài Processing/AwaitingUser/Unknown có remote id tới hạn kiểm tra trạng thái (next_check_at), cũ trước.</summary>
    Task<IReadOnlyList<Publication>> ListDueStatusChecksAsync(DateTimeOffset nowUtc, int limit, CancellationToken ct);
    /// <summary>Đặt mốc kiểm tra trạng thái kế tiếp (null = thôi kiểm tra).</summary>
    Task SetNextCheckAsync(string id, DateTimeOffset? nextCheckUtc, CancellationToken ct);
    /// <summary>Ghi tuyến mạng/IP thật đã dùng cho lần thử.</summary>
    Task SetAttemptRouteAsync(string attemptId, string routeKind, string? routeRef, string? egressIp, string? egressCountry, DateTimeOffset? checkedUtc,
        CancellationToken ct);
    /// <summary>Bài theo nguồn tạo (S07/S08: origin + origin_ref), mới trước.</summary>
    Task<IReadOnlyList<Publication>> ListByOriginAsync(string origin, string? originRef, int limit, CancellationToken ct);
    Task<Publication?> GetAsync(string id, CancellationToken ct);
    Task<PublicationRow?> GetRowAsync(string id, CancellationToken ct);
    Task<(IReadOnlyList<PublicationRow> Items, int Total)> QueryAsync(PublicationQuery query, CancellationToken ct);

    /// <summary>Chuyển trạng thái nếu trạng thái hiện tại được phép đi tới <paramref name="to"/>; false nếu đã bị đổi song song.</summary>
    Task<bool> TransitionAsync(string id, PublicationStatus to, string? lastError, string? blockedReason, CancellationToken ct);
    Task<bool> RequeueAsync(PublicationRequeue requeue, CancellationToken ct);
    /// <summary>Hủy local: chỉ khi video chưa thể nằm trên nền tảng (chưa có remote id, chưa gửi chunk cuối) — trừ khi <paramref name="allowUnknown"/>.</summary>
    Task<bool> CancelAsync(string id, bool allowUnknown, CancellationToken ct);

    // Mốc upload: chỉ ghi khi publication còn Uploading (bị hủy giữa chừng → false để dừng upload).
    Task<bool> SaveUploadSessionAsync(string id, string sessionUri, long totalBytes, DateTimeOffset startedUtc, CancellationToken ct);
    Task<bool> SaveUploadProgressAsync(string id, long bytesConfirmed, CancellationToken ct);
    Task<bool> MarkFinalChunkSentAsync(string id, DateTimeOffset at, CancellationToken ct);
    Task ClearUploadSessionAsync(string id, CancellationToken ct);

    /// <summary>Lưu remote id ngay khi nhận + chuyển Processing + ghi remote_posts trong 1 transaction.</summary>
    Task<bool> AttachRemoteAsync(string id, RemotePost post, CancellationToken ct);
    /// <param name="remoteUrl">URL thật của bài khi nền tảng trả về lúc xác nhận (IG permalink, TikTok post id); null = giữ URL cũ.</param>
    Task<bool> MarkPublishedAsync(string id, DateTimeOffset at, string? remoteStatus, string? remoteUrl, CancellationToken ct);
    /// <summary>Đổi lựa chọn riêng của nền tảng khi bài còn ở một trong <paramref name="from"/> (chưa upload).</summary>
    Task<bool> SetPlatformSettingsAsync(string id, string? platformSettingsJson, IReadOnlyCollection<PublicationStatus> from, CancellationToken ct);
    Task SetProcessingPollsAsync(string id, int polls, CancellationToken ct);

    Task<IReadOnlyList<Publication>> FindOnDestinationAsync(string contentItemId, string destinationId, CancellationToken ct);
    Task<IReadOnlyList<RiskHistoryRow>> ListForRiskAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
    Task<IReadOnlyList<Publication>> ListByConnectionAsync(string connectionId, IReadOnlyCollection<PublicationStatus> statuses, CancellationToken ct);
    Task<IReadOnlySet<string>> RemoteIdsOnDestinationAsync(string destinationId, CancellationToken ct);
    Task<int> CountForConnectionAsync(string connectionId, CancellationToken ct);
    Task<PublishSummary> SummaryAsync(DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken ct);

    Task<string> StartAttemptAsync(string publicationId, string? jobId, DateTimeOffset at, CancellationToken ct);
    Task FinishAttemptAsync(string attemptId, string outcome, string? error, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<PublishAttempt>> ListAttemptsAsync(string publicationId, CancellationToken ct);
    /// <summary>Các lần thử đăng đã ghi tuyến của một tài khoản, mới nhất trước.</summary>
    Task<IReadOnlyList<PublishAttempt>> ListConnectionAttemptsAsync(string connectionId, int limit, CancellationToken ct);
    /// <summary>Lần thử đăng thành công gần nhất của tài khoản có ghi IP ra ngoài (null = chưa có).</summary>
    Task<PublishAttempt?> LastSucceededEgressAsync(string connectionId, CancellationToken ct);
    /// <summary>IP ra ngoài ở lần thử đăng gần nhất có ghi IP của từng tài khoản.</summary>
    Task<IReadOnlyList<ConnectionEgress>> ListLatestEgressAsync(CancellationToken ct);
}

/// <summary>
/// Sổ quota dùng chung bảng quota_usage (mỗi lần dùng một dòng, cột project_key = hash client id OAuth).
/// Ngày quota là cửa sổ [dayStartUtc, dayEndUtc) do caller tính (YouTube: ngày Pacific, có DST nên không cố định 24h).
/// </summary>
public interface IQuotaLedger
{
    /// <summary>Ghi units nguyên tử nếu tổng trong ngày không vượt <paramref name="limit"/>; false = hết quota ngày.</summary>
    Task<bool> TryConsumeAsync(string platform, string projectKey, string endpoint, int units, int limit, DateTimeOffset dayStartUtc,
        DateTimeOffset dayEndUtc, CancellationToken ct);
    /// <summary>Nền tảng báo hết quota sớm hơn sổ: ghi bù cho đủ <paramref name="limit"/> để không gọi tiếp trong ngày.</summary>
    Task MarkExhaustedAsync(string platform, string projectKey, int limit, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken ct);
    Task<int> GetUsedAsync(string projectKey, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken ct);
}

public sealed record CampaignCounts(int Total, int AwaitingApproval, int Upcoming);

/// <summary>Bài đang/đã chiếm chỗ trên kênh (mọi trạng thái trừ Draft/Cancelled/Failed); AtUtc = giờ đăng thật hoặc giờ hẹn.</summary>
public sealed record ChannelPost(
    string PublicationId, string DestinationId, string ContentItemId, string? CampaignId, string? PlanKey, DateTimeOffset AtUtc,
    PublicationStatus Status);

/// <summary>Bài chiến dịch đã hẹn mà chưa bắt đầu upload, giờ hẹn đã qua.</summary>
public sealed record LateCandidate(string PublicationId, string CampaignId, string DestinationId, string ConnectionId, DateTimeOffset ScheduledUtc);

public interface ICampaignRepository
{
    Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct);
    Task<Campaign?> GetAsync(string id, CancellationToken ct);
    /// <summary>campaignId null = mọi chiến dịch.</summary>
    Task<IReadOnlyList<CampaignRule>> ListRulesAsync(string? campaignId, CancellationToken ct);
    Task<IReadOnlyList<SchedulePolicy>> ListPoliciesAsync(string? campaignId, CancellationToken ct);

    /// <summary>Upsert chiến dịch, thay toàn bộ quy tắc và chính sách lịch trong 1 transaction (không đổi trạng thái của bản đã có).</summary>
    Task SaveAsync(Campaign campaign, IReadOnlyList<CampaignRule> rules, SchedulePolicy policy, CancellationToken ct);

    /// <summary>Đổi trạng thái nếu đang ở một trong <paramref name="from"/>; false nếu đã bị đổi song song.</summary>
    Task<bool> SetStatusAsync(string id, CampaignStatus status, string? reason, IReadOnlyCollection<CampaignStatus> from, DateTimeOffset now,
        CancellationToken ct);
    Task<bool> RemoveAsync(string id, CancellationToken ct);
    Task<IReadOnlyDictionary<string, CampaignCounts>> CountsAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Trạng thái kết thúc (Published/Failed/NeedsAction/Unknown) mới nhất của bài chiến dịch cập nhật sau <paramref name="sinceUtc"/>.</summary>
    Task<IReadOnlyList<PublicationStatus>> RecentOutcomesAsync(string campaignId, DateTimeOffset sinceUtc, int take, CancellationToken ct);

    Task<IReadOnlyList<ChannelPost>> ListChannelPostsAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>> LoadDeficitsAsync(CancellationToken ct);

    /// <summary>
    /// Ghi kết quả một lượt planner trong 1 transaction: publication (Scheduled thì kèm mốc điều phối) + mục cần xử lý + deficit chia lượt.
    /// false = trùng plan_key (lượt khác đã tạo cùng slot): không ghi gì.
    /// </summary>
    Task<bool> CommitPlanAsync(IReadOnlyList<Publication> publications, IReadOnlyDictionary<string, DateTimeOffset> dispatchUtc,
        IReadOnlyList<ActionRequired> actions,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> deficits, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<LateCandidate>> ListLateAsync(DateTimeOffset nowUtc, string? campaignId, CancellationToken ct);
    Task<bool> MarkLateResolvedAsync(string publicationId, CancellationToken ct);
    /// <summary>Hủy bài trễ chưa upload (kèm lý do); job đã xếp tự bỏ qua khi thấy bài không còn chờ đăng.</summary>
    Task<bool> CancelPendingAsync(string publicationId, string reason, CancellationToken ct);
    /// <summary>Bài AwaitingApproval/Scheduled chưa bắt đầu upload của chiến dịch.</summary>
    Task<IReadOnlyList<string>> ListPendingAsync(string campaignId, CancellationToken ct);
    /// <summary>Đổi giờ bài đang chờ duyệt (chưa có job).</summary>
    Task<bool> RescheduleAwaitingAsync(string publicationId, DateTimeOffset scheduledUtc, CancellationToken ct);
}

public interface IContentSignatureRepository
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<ulong>>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<ulong>?> GetAsync(string contentItemId, CancellationToken ct);
    Task SaveAsync(string contentItemId, IReadOnlyList<ulong> frameHashes, DateTimeOffset now, CancellationToken ct);
    Task<(int With, int Without)> CountAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListMissingAsync(int limit, CancellationToken ct);
}

/// <summary>Bài đã đăng có thể đồng bộ thống kê (publication Published có remote id).</summary>
/// <param name="LastSyncedUtc">Lần đọc gần nhất (mọi trạng thái, kể cả thất bại): bài thiếu quyền không bị gọi lại liên tục.</param>
public sealed record SyncCandidate(
    string PublicationId, string DestinationId, string ConnectionId, SocialPlatform Platform, string RemoteId, string? RemoteUrl,
    DateTimeOffset PublishedUtc, DateTimeOffset? LastSyncedUtc);

/// <summary>Điểm dữ liệu thống nhất từ snapshot chi tiết và daily_aggregates (aggregate luôn là Ok).</summary>
public sealed record MetricPoint(
    string DestinationId, string? PublicationId, SocialPlatform Platform, string MetricKey, double? Value, DateTimeOffset CapturedUtc,
    string Source, MetricStatus Status, string? Detail);

public sealed record PostInfo(
    string PublicationId, string DestinationId, string DestinationTitle, string ConnectionId, SocialPlatform Platform, string Title,
    DateTimeOffset PublishedUtc, string? CampaignId, string? CampaignName, string? SourceId, string? SourceName);

public sealed record DestinationInfoRow(string DestinationId, string Title, string ConnectionId, SocialPlatform Platform, bool Removed);

/// <param name="PublicationIds">null = không lọc theo bài; metric cấp kênh luôn có <see cref="MetricPoint.PublicationId"/> null.</param>
public sealed record PostFilter(
    SocialPlatform? Platform, string? DestinationId, string? CampaignId, string? SourceId, string? PublicationId, DateTimeOffset PublishedBeforeUtc);

public sealed record CompactionResult(int Aggregated, int Deleted);

/// <param name="Failed">Bài đăng lỗi/cần xử lý/không rõ trong ngày UTC.</param>
public sealed record DailyPublishOutcome(string Day, int Failed, int Published);

public sealed record PostViewSample(string PublicationId, DateTimeOffset PublishedUtc, double Views);

public interface IAnalyticsRepository
{
    Task<IReadOnlyList<MetricDefinition>> ListDefinitionsAsync(CancellationToken ct);

    Task<IReadOnlyList<SyncCandidate>> ListSyncCandidatesAsync(DateTimeOffset maxPublishedUtc, CancellationToken ct);
    /// <summary>Lần đọc cấp kênh gần nhất của mỗi kênh (mọi trạng thái).</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> LastChannelSyncAsync(CancellationToken ct);
    Task<IReadOnlyList<DestinationInfoRow>> ListDestinationsAsync(CancellationToken ct);
    /// <summary>Kênh đang có job đồng bộ Pending/Running: planner không xếp thêm để khỏi đọc trùng khi job bị dời (VPN, quota).</summary>
    Task<IReadOnlySet<string>> ListDestinationsWithPendingSyncAsync(CancellationToken ct);

    Task AddSnapshotsAsync(IReadOnlyList<MetricSnapshot> snapshots, CancellationToken ct);

    /// <summary>Gộp snapshot cũ hơn <paramref name="cutoffUtc"/> thành daily_aggregates (chỉ Ok) rồi xóa; chạy lại là no-op.</summary>
    Task<CompactionResult> CompactAsync(DateTimeOffset cutoffUtc, CancellationToken ct);

    Task<IReadOnlyList<PostInfo>> ListPostsAsync(PostFilter filter, CancellationToken ct);
    /// <param name="publicationIds">null = chỉ metric cấp kênh của <paramref name="destinationIds"/>; có giá trị = metric của các bài đó.</param>
    Task<IReadOnlyList<MetricPoint>> ListPointsAsync(IReadOnlyCollection<string>? destinationIds, IReadOnlyCollection<string>? publicationIds,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    Task<IReadOnlyList<DailyPublishOutcome>> ListPublishOutcomesAsync(string destinationId, DateTimeOffset fromUtc, CancellationToken ct);
    /// <summary>Lượt xem chính (mới nhất, Ok) của các bài đã đăng trước <paramref name="publishedBeforeUtc"/>, cũ → mới.</summary>
    Task<IReadOnlyList<PostViewSample>> ListPostViewSamplesAsync(string destinationId, string metricKey, DateTimeOffset publishedBeforeUtc,
        CancellationToken ct);

    /// <summary>false = đã có sự kiện cùng (kênh, loại, ngày).</summary>
    Task<bool> TryAddHealthEventAsync(AccountHealthEvent ev, string day, CancellationToken ct);
    Task SetHealthEventCampaignsPausedAsync(string id, int paused, CancellationToken ct);
    Task<IReadOnlyList<(AccountHealthEvent Event, string? DestinationTitle)>> ListHealthEventsAsync(int take, CancellationToken ct);
}

public interface IProductionRepository
{
    Task AddAsync(ProductionRequest request, CancellationToken ct);
    Task<ProductionRequest?> GetAsync(string id, CancellationToken ct);
    /// <param name="open">true = chỉ các trạng thái đang mở; false = mọi trạng thái (lọc theo <paramref name="state"/> nếu có).</param>
    Task<(IReadOnlyList<ProductionRequest> Items, int Total)> ListAsync(
        ProductionState? state, bool open, string? campaignId, int page, int pageSize, CancellationToken ct);
    /// <summary>updatedSinceUtc null = không giới hạn thời gian.</summary>
    Task<IReadOnlyList<ProductionRequest>> ListByStatesAsync(
        IReadOnlyCollection<ProductionState> states, string? producer, DateTimeOffset? updatedSinceUtc, CancellationToken ct);
    /// <summary>Ghi mọi trường thay đổi nếu state hiện tại còn thuộc <paramref name="from"/>; false = đã bị đổi song song.</summary>
    Task<bool> UpdateAsync(ProductionRequest request, IReadOnlyCollection<ProductionState> from, CancellationToken ct);
    /// <summary>false = đã có bản ghi cùng (request, state, updatedAt của producer).</summary>
    Task<bool> AddAckAsync(ProducerAcknowledgement ack, CancellationToken ct);
    Task<IReadOnlyList<ProducerAcknowledgement>> ListAcksAsync(string requestId, CancellationToken ct);
    Task<int> CountOpenAsync(string? producer, string? campaignId, CancellationToken ct);
    /// <summary>Số yêu cầu producer nhận từ <paramref name="sinceUtc"/> (trừ Cancelled): căn cứ ngân sách ngày.</summary>
    Task<int> CountCreatedSinceAsync(string producer, DateTimeOffset sinceUtc, CancellationToken ct);
    Task<IReadOnlyList<ProductionRequest>> ListRecentForCampaignAsync(string campaignId, int take, CancellationToken ct);
    Task<IReadOnlyDictionary<string, int>> LinkedContentCountsAsync(IReadOnlyCollection<string> requestIds, CancellationToken ct);
    Task<IReadOnlyList<(string Id, string Title)>> ListLinkedContentAsync(string requestId, CancellationToken ct);
    /// <summary>request id → campaign id của các yêu cầu thuộc chiến dịch (ưu tiên nội dung cho chiến dịch đã đặt).</summary>
    Task<IReadOnlyDictionary<string, string>> RequestCampaignsAsync(CancellationToken ct);
}

public interface IProductionConfigRepository
{
    Task<IReadOnlyList<ProducerConfig>> ListProducersAsync(CancellationToken ct);
    Task SaveProducerAsync(ProducerConfig config, CancellationToken ct);
    Task<CampaignProduction?> GetCampaignAsync(string campaignId, CancellationToken ct);
    Task<IReadOnlyList<CampaignProduction>> ListCampaignsAsync(CancellationToken ct);
    Task SaveCampaignAsync(CampaignProduction settings, DateTimeOffset now, CancellationToken ct);
    Task SetReplenishingAsync(string campaignId, bool value, CancellationToken ct);
}
