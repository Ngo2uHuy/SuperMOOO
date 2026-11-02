namespace SuperMOOO.Core.Persistence;

public static class ReupItemStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Ignored = "ignored";
    public const string Duplicate = "duplicate";
}

public sealed record ReupRuleRecord(string Id, string Name, bool Enabled, string ConfigJson, bool Initialized, DateTimeOffset? LastCheckAt,
    DateTimeOffset? NextCheckAt, string? Message, DateTimeOffset CreatedAt);

public sealed record ReupItemRecord(string Id, string RuleId, string Platform, string SourceKey, string VideoJson, string Status,
    long? MediaFileId, int Attempts, DateTimeOffset? NextAttemptAt, string? Error, DateTimeOffset CreatedAt);

/// <summary>Lưu trữ cho `reup_rule`/`reup_item` (Auto Reup). Bài đăng nằm ở `pub_publications` (origin auto_reup).</summary>
public interface IReupStore
{
    Task UpsertRuleAsync(ReupRuleRecord rule, CancellationToken ct = default);
    Task<ReupRuleRecord?> GetRuleAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ReupRuleRecord>> ListRulesAsync(CancellationToken ct = default);
    Task<bool> DeleteRuleAsync(string id, CancellationToken ct = default);
    Task SetRuleEnabledAsync(string id, bool enabled, DateTimeOffset? nextCheckAt, CancellationToken ct = default);
    /// <summary>Rule bật và đã đến hạn quét (next_check_at ≤ now), cũ nhất trước.</summary>
    Task<ReupRuleRecord?> NextDueRuleAsync(DateTimeOffset now, CancellationToken ct = default);
    Task UpdateRuleScanAsync(string id, bool initialized, DateTimeOffset lastCheckAt, DateTimeOffset nextCheckAt, string? message, CancellationToken ct = default);

    /// <summary>false nếu (rule, source_key) đã có.</summary>
    Task<bool> InsertItemAsync(ReupItemRecord item, CancellationToken ct = default);
    Task<ReupItemRecord?> GetItemAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ReupItemRecord>> ListItemsAsync(string? ruleId, string? status = null, int limit = 200, int offset = 0, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> CountByStatusAsync(CancellationToken ct = default);
    /// <summary>Nhận 1 item `pending` đến hạn của rule đang bật (atomic) → processing; thứ tự theo `newestFirst` của rule.</summary>
    Task<ReupItemRecord?> ClaimPendingAsync(DateTimeOffset now, CancellationToken ct = default);
    Task<int> CountProcessingAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ReupItemRecord>> ListProcessingAsync(CancellationToken ct = default);
    Task UpdateItemAsync(string id, string status, int? attempts = null, DateTimeOffset? nextAttemptAt = null, string? error = null,
        string? videoJson = null, long? mediaFileId = null, CancellationToken ct = default);
    /// <summary>Item còn `processing` sau khi app tắt đột ngột → pending.</summary>
    Task<int> RecoverProcessingAsync(CancellationToken ct = default);
}
