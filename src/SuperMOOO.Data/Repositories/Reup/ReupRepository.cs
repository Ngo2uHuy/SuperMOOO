using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Reup;

[Service(As = [typeof(IReupStore)])]
public sealed class ReupRepository(IDbConnectionFactory factory) : IReupStore
{
    private const string RuleColumns = """
        id AS Id, name AS Name, enabled AS Enabled, config_json AS ConfigJson, initialized AS Initialized,
        last_check_at AS LastCheckAt, next_check_at AS NextCheckAt, message AS Message, created_at AS CreatedAt
        """;

    private const string ItemColumns = """
        i.id AS Id, i.rule_id AS RuleId, i.platform AS Platform, i.source_key AS SourceKey, i.video_json AS VideoJson, i.status AS Status,
        i.media_file_id AS MediaFileId, i.attempts AS Attempts, i.next_attempt_at AS NextAttemptAt, i.error AS Error, i.created_at AS CreatedAt
        """;

    private sealed class RuleRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long Enabled { get; set; }
        public string ConfigJson { get; set; } = "";
        public long Initialized { get; set; }
        public long? LastCheckAt { get; set; }
        public long? NextCheckAt { get; set; }
        public string? Message { get; set; }
        public long CreatedAt { get; set; }
    }

    private sealed class ItemRow
    {
        public string Id { get; set; } = "";
        public string RuleId { get; set; } = "";
        public string Platform { get; set; } = "";
        public string SourceKey { get; set; } = "";
        public string VideoJson { get; set; } = "";
        public string Status { get; set; } = "";
        public long? MediaFileId { get; set; }
        public int Attempts { get; set; }
        public long? NextAttemptAt { get; set; }
        public string? Error { get; set; }
        public long CreatedAt { get; set; }
    }

    private sealed class CountRow
    {
        public string RuleId { get; set; } = "";
        public string Status { get; set; } = "";
        public long N { get; set; }
    }

    private static DateTimeOffset FromMs(long v) => DateTimeOffset.FromUnixTimeMilliseconds(v);
    private static DateTimeOffset? FromMs(long? v) => v is { } n ? FromMs(n) : null;
    private static long ToMs(DateTimeOffset v) => v.ToUnixTimeMilliseconds();
    private static long? ToMs(DateTimeOffset? v) => v?.ToUnixTimeMilliseconds();

    private static ReupRuleRecord ToRule(RuleRow r) =>
        new(r.Id, r.Name, r.Enabled != 0, r.ConfigJson, r.Initialized != 0, FromMs(r.LastCheckAt), FromMs(r.NextCheckAt), r.Message, FromMs(r.CreatedAt));

    private static ReupItemRecord ToItem(ItemRow r) =>
        new(r.Id, r.RuleId, r.Platform, r.SourceKey, r.VideoJson, r.Status, r.MediaFileId, r.Attempts, FromMs(r.NextAttemptAt), r.Error, FromMs(r.CreatedAt));

    public async Task UpsertRuleAsync(ReupRuleRecord rule, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO reup_rule(id, name, enabled, config_json, initialized, last_check_at, next_check_at, message, created_at)
            VALUES (@Id, @Name, @enabled, @ConfigJson, @initialized, @last, @next, @Message, @created)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, enabled = excluded.enabled, config_json = excluded.config_json,
              next_check_at = excluded.next_check_at
            """, new
            {
                rule.Id, rule.Name, enabled = rule.Enabled ? 1 : 0, rule.ConfigJson, initialized = rule.Initialized ? 1 : 0,
                last = ToMs(rule.LastCheckAt), next = ToMs(rule.NextCheckAt), rule.Message, created = ToMs(rule.CreatedAt)
            }, cancellationToken: ct));
    }

    public async Task<ReupRuleRecord?> GetRuleAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<RuleRow>(new CommandDefinition(
            $"SELECT {RuleColumns} FROM reup_rule WHERE id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : ToRule(row);
    }

    public async Task<IReadOnlyList<ReupRuleRecord>> ListRulesAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<RuleRow>(new CommandDefinition(
            $"SELECT {RuleColumns} FROM reup_rule ORDER BY created_at DESC", cancellationToken: ct));
        return rows.Select(ToRule).ToList();
    }

    public async Task<bool> DeleteRuleAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM reup_rule WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    public async Task SetRuleEnabledAsync(string id, bool enabled, DateTimeOffset? nextCheckAt, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE reup_rule SET enabled = @enabled, next_check_at = COALESCE(@next, next_check_at) WHERE id = @id",
            new { id, enabled = enabled ? 1 : 0, next = ToMs(nextCheckAt) }, cancellationToken: ct));
    }

    public async Task<ReupRuleRecord?> NextDueRuleAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QueryFirstOrDefaultAsync<RuleRow>(new CommandDefinition($"""
            SELECT {RuleColumns} FROM reup_rule WHERE enabled = 1 AND COALESCE(next_check_at, 0) <= @now
            ORDER BY COALESCE(next_check_at, 0) LIMIT 1
            """, new { now = ToMs(now) }, cancellationToken: ct));
        return row is null ? null : ToRule(row);
    }

    public async Task UpdateRuleScanAsync(string id, bool initialized, DateTimeOffset lastCheckAt, DateTimeOffset nextCheckAt, string? message,
        CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE reup_rule SET initialized = @initialized, last_check_at = @last, next_check_at = @next, message = @message WHERE id = @id
            """, new { id, initialized = initialized ? 1 : 0, last = ToMs(lastCheckAt), next = ToMs(nextCheckAt), message }, cancellationToken: ct));
    }

    public async Task<bool> InsertItemAsync(ReupItemRecord item, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO reup_item(id, rule_id, platform, source_key, video_json, status, media_file_id, attempts, next_attempt_at, error, created_at)
            VALUES (@Id, @RuleId, @Platform, @SourceKey, @VideoJson, @Status, @MediaFileId, @Attempts, @next, @Error, @created)
            """, new
            {
                item.Id, item.RuleId, item.Platform, item.SourceKey, item.VideoJson, item.Status, item.MediaFileId, item.Attempts,
                next = ToMs(item.NextAttemptAt), item.Error, created = ToMs(item.CreatedAt)
            }, cancellationToken: ct)) > 0;
    }

    public async Task<ReupItemRecord?> GetItemAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            $"SELECT {ItemColumns} FROM reup_item i WHERE i.id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : ToItem(row);
    }

    public async Task<IReadOnlyList<ReupItemRecord>> ListItemsAsync(string? ruleId, string? status = null, int limit = 200, int offset = 0,
        CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<ItemRow>(new CommandDefinition($"""
            SELECT {ItemColumns} FROM reup_item i
            WHERE (@ruleId IS NULL OR i.rule_id = @ruleId) AND (@status IS NULL OR i.status = @status)
            ORDER BY i.created_at DESC, i.id LIMIT @limit OFFSET @offset
            """, new { ruleId, status, limit, offset }, cancellationToken: ct));
        return rows.Select(ToItem).ToList();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> CountByStatusAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<CountRow>(new CommandDefinition(
            "SELECT rule_id AS RuleId, status AS Status, COUNT(*) AS N FROM reup_item GROUP BY rule_id, status", cancellationToken: ct));
        return rows.GroupBy(r => r.RuleId).ToDictionary(g => g.Key,
            g => (IReadOnlyDictionary<string, int>)g.ToDictionary(r => r.Status, r => (int)r.N));
    }

    public async Task<ReupItemRecord?> ClaimPendingAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var id = await db.ExecuteScalarAsync<string?>(new CommandDefinition("""
            UPDATE reup_item SET status = 'processing'
            WHERE id = (SELECT i.id FROM reup_item i JOIN reup_rule r ON r.id = i.rule_id
                        WHERE r.enabled = 1 AND i.status = 'pending' AND COALESCE(i.next_attempt_at, 0) <= @now
                        ORDER BY CASE WHEN json_extract(r.config_json, '$.newestFirst') = 1
                                      THEN -COALESCE(json_extract(i.video_json, '$.publishedAtMs'), i.created_at)
                                      ELSE COALESCE(json_extract(i.video_json, '$.publishedAtMs'), i.created_at) END
                        LIMIT 1)
            RETURNING id
            """, new { now = ToMs(now) }, cancellationToken: ct));
        return id is null ? null : await GetItemAsync(id, ct);
    }

    public async Task<int> CountProcessingAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM reup_item WHERE status = 'processing'", cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ReupItemRecord>> ListProcessingAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<ItemRow>(new CommandDefinition(
            $"SELECT {ItemColumns} FROM reup_item i WHERE i.status = 'processing'", cancellationToken: ct));
        return rows.Select(ToItem).ToList();
    }

    public async Task UpdateItemAsync(string id, string status, int? attempts = null, DateTimeOffset? nextAttemptAt = null, string? error = null,
        string? videoJson = null, long? mediaFileId = null, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE reup_item SET status = @status, attempts = COALESCE(@attempts, attempts), next_attempt_at = @next, error = @error,
              video_json = COALESCE(@videoJson, video_json), media_file_id = COALESCE(@mediaFileId, media_file_id)
            WHERE id = @id
            """, new { id, status, attempts, next = ToMs(nextAttemptAt), error, videoJson, mediaFileId }, cancellationToken: ct));
    }

    public async Task<int> RecoverProcessingAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("UPDATE reup_item SET status = 'pending' WHERE status = 'processing'", cancellationToken: ct));
    }
}
