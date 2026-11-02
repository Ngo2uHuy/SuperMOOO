using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Social;

[Service(As = [typeof(IWatchTargetStore)])]
public sealed class WatchTargetRepository(IDbConnectionFactory factory) : IWatchTargetStore
{
    private sealed class Row
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string Url { get; set; } = "";
        public string Platform { get; set; } = "";
        public string TargetType { get; set; } = "";
        public int FrequencyHours { get; set; }
        public string Status { get; set; } = "";
        public long? ContentItemId { get; set; }
        public long? LastCheckedAt { get; set; }
        public long CreatedAt { get; set; }
    }

    private const string Columns =
        "id, name, url, platform, target_type, frequency_hours, status, content_item_id, last_checked_at, created_at";

    public async Task<long> AddAsync(WatchTarget target, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO watch_target(name, url, platform, target_type, frequency_hours, status, content_item_id, created_at)
            VALUES (@Name, @Url, @Platform, @TargetType, @FrequencyHours, @Status, @ContentItemId, @now)
            RETURNING id
            """, new { target.Name, target.Url, target.Platform, target.TargetType, target.FrequencyHours, target.Status, target.ContentItemId, now = DapperSetup.NowMs },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WatchTarget>> ListAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM watch_target ORDER BY id", cancellationToken: ct));
        return rows.Select(ToTarget).ToList();
    }

    public async Task<WatchTarget?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM watch_target WHERE id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : ToTarget(row);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM watch_target WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    public async Task MarkCheckedAsync(long id, DateTimeOffset at, long? contentItemId = null, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE watch_target SET last_checked_at = @at, content_item_id = COALESCE(@contentItemId, content_item_id) WHERE id = @id
            """, new { id, at = at.ToUnixTimeMilliseconds(), contentItemId }, cancellationToken: ct));
    }

    private static WatchTarget ToTarget(Row r) => new(r.Name ?? r.Url, r.Url, r.Platform, r.TargetType, r.FrequencyHours, r.Status,
        r.ContentItemId, r.LastCheckedAt is { } l ? DateTimeOffset.FromUnixTimeMilliseconds(l) : null,
        DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAt), r.Id);
}
