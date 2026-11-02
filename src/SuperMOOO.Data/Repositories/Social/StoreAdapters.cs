using SuperMOOO.Data.Repositories.Audit;
using SuperMOOO.Data.Repositories.Content;

namespace SuperMOOO.Data.Repositories.Social;

// Bọc repository dùng chung của P1 thành interface Core để Social (chỉ tham chiếu Core) dùng được mà không sửa file P1.

[Service(As = [typeof(ICrawlRunStore)])]
public sealed class CrawlRunStore(CrawlRunRepository runs) : ICrawlRunStore
{
    public Task<string> StartAsync(string platform, string runType, string? query, string? filtersJson = null, string? jobId = null,
        CancellationToken ct = default) => runs.StartAsync(platform, runType, query, filtersJson, jobId, ct);

    public Task AddItemAsync(string runId, long contentItemId, int rank, CancellationToken ct = default) =>
        runs.AddItemAsync(runId, contentItemId, rank, ct);

    public Task CompleteAsync(string runId, string status, int itemCount, string? error = null, CancellationToken ct = default) =>
        runs.CompleteAsync(runId, status, itemCount, error, ct);

    public async Task<IReadOnlyList<CrawlRunInfo>> ListAsync(string? platform = null, int limit = 100, CancellationToken ct = default) =>
        (await runs.ListAsync(platform, limit, ct)).Select(r => new CrawlRunInfo(r.Id, r.Platform, r.RunType, r.Query, r.Status,
            r.ItemCount, r.Error, DateTimeOffset.FromUnixTimeMilliseconds(r.StartedAt),
            r.FinishedAt is { } f ? DateTimeOffset.FromUnixTimeMilliseconds(f) : null)).ToList();
}

[Service(As = [typeof(IAuditLog)])]
public sealed class AuditLogStore(AuditLogRepository audit) : IAuditLog
{
    public Task AddAsync(string source, string action, string? target, string status, int? recordsCount = null,
        CancellationToken ct = default) => audit.AddAsync(source, action, target, status, recordsCount, ct);

    public async Task<IReadOnlyList<AuditEntry>> ListRecentAsync(int limit = 100, CancellationToken ct = default) =>
        (await audit.ListRecentAsync(limit, ct)).Select(e => new AuditEntry(e.Source, e.Action, e.Target, e.Status, e.RecordsCount,
            DateTimeOffset.FromUnixTimeMilliseconds(e.CreatedAt), e.Id)).ToList();
}

[Service(As = [typeof(IQuotaUsageStore)])]
public sealed class QuotaUsageStore(QuotaUsageRepository quota) : IQuotaUsageStore
{
    public Task AddAsync(string platform, string endpoint, int units, string caller = "system", CancellationToken ct = default) =>
        quota.AddAsync(platform, endpoint, units, caller, ct);

    public Task<long> SumUnitsSinceAsync(string platform, DateTimeOffset since, CancellationToken ct = default) =>
        quota.SumUnitsSinceAsync(platform, since, ct);
}
