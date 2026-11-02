namespace SuperMOOO.Core.Persistence;

// Social chỉ tham chiếu Core nên truy cập DB qua các interface này; cài đặt nằm ở Data\Repositories\Social.

public interface IContentStore
{
    /// <summary>Thêm hoặc cập nhật theo (platform, kind, external_id); trường null không ghi đè giá trị cũ. Trả về id.</summary>
    Task<long> UpsertAsync(ContentItem item, CancellationToken ct = default);
    Task<ContentItem?> GetAsync(long id, CancellationToken ct = default);
    Task<ContentItem?> FindAsync(string platform, string kind, string externalId, CancellationToken ct = default);
    /// <summary>Id của bản ghi mới nhất khớp (platform, external_id), bất kể loại.</summary>
    Task<long?> FindIdAsync(string platform, string externalId, CancellationToken ct = default);
    Task<IReadOnlyList<ContentItem>> ListByKindsAsync(string? platform, IReadOnlyCollection<string> kinds, int limit, CancellationToken ct = default);
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);
    Task SetInLibraryAsync(long id, bool inLibrary, CancellationToken ct = default);
}

public interface ICrawlRunStore
{
    Task<string> StartAsync(string platform, string runType, string? query, string? filtersJson = null, string? jobId = null, CancellationToken ct = default);
    Task AddItemAsync(string runId, long contentItemId, int rank, CancellationToken ct = default);
    Task CompleteAsync(string runId, string status, int itemCount, string? error = null, CancellationToken ct = default);
    Task<IReadOnlyList<CrawlRunInfo>> ListAsync(string? platform = null, int limit = 100, CancellationToken ct = default);
}

public interface IAuditLog
{
    Task AddAsync(string source, string action, string? target, string status, int? recordsCount = null, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> ListRecentAsync(int limit = 100, CancellationToken ct = default);
}

public interface IQuotaUsageStore
{
    Task AddAsync(string platform, string endpoint, int units, string caller = "system", CancellationToken ct = default);
    Task<long> SumUnitsSinceAsync(string platform, DateTimeOffset since, CancellationToken ct = default);
}

public interface ICommentStore
{
    Task<long> SaveCampaignAsync(CommentCampaign campaign, IReadOnlyList<CommentRecord> comments, CancellationToken ct = default);
    Task<IReadOnlyList<CommentCampaign>> ListCampaignsAsync(int limit = 200, CancellationToken ct = default);
    Task<CommentCampaign?> GetCampaignAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<CommentRecord>> ListCommentsAsync(long campaignId, bool leadsOnly = false, CancellationToken ct = default);
    Task<bool> DeleteCampaignAsync(long id, CancellationToken ct = default);
}

public interface IWatchTargetStore
{
    Task<long> AddAsync(WatchTarget target, CancellationToken ct = default);
    Task<IReadOnlyList<WatchTarget>> ListAsync(CancellationToken ct = default);
    Task<WatchTarget?> GetAsync(long id, CancellationToken ct = default);
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);
    Task MarkCheckedAsync(long id, DateTimeOffset at, long? contentItemId = null, CancellationToken ct = default);
}

public interface IMetricStore
{
    Task RecordAsync(MetricSample sample, CancellationToken ct = default);
    Task<IReadOnlyList<MetricSample>> HistoryAsync(string entityKey, string? metric = null, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<MetricEntity>> ListEntitiesAsync(int limit = 200, CancellationToken ct = default);
}
