using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Audit;

public sealed record AuditLogEntry
{
    public long Id { get; init; }
    public string Source { get; init; } = "";
    public string Action { get; init; } = "";
    public string? Target { get; init; }
    public string Status { get; init; } = "";
    public int? RecordsCount { get; init; }
    public long CreatedAt { get; init; }
}

[Service]
public sealed class AuditLogRepository(IDbConnectionFactory factory)
{
    public async Task AddAsync(string source, string action, string? target, string status, int? recordsCount = null,
        CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_log(source, action, target, status, records_count, created_at)
            VALUES (@source, @action, @target, @status, @recordsCount, @now)
            """, new { source, action, target, status, recordsCount, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AuditLogEntry>> ListRecentAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<AuditLogEntry>(new CommandDefinition(
            "SELECT id, source, action, target, status, records_count AS RecordsCount, created_at AS CreatedAt FROM audit_log ORDER BY created_at DESC, id DESC LIMIT @limit",
            new { limit }, cancellationToken: ct))).ToList();
    }
}
