using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Content;

public sealed record CrawlRunRecord
{
    public string Id { get; init; } = "";
    public string Platform { get; init; } = "";
    public string RunType { get; init; } = "";
    public string? Query { get; init; }
    public string Status { get; init; } = "";
    public int ItemCount { get; init; }
    public string? Error { get; init; }
    public long StartedAt { get; init; }
    public long? FinishedAt { get; init; }
}

[Service]
public sealed class CrawlRunRepository(IDbConnectionFactory factory)
{
    private const string Columns =
        "id, platform, run_type AS RunType, query, status, item_count AS ItemCount, error, started_at AS StartedAt, finished_at AS FinishedAt";

    public async Task<string> StartAsync(string platform, string runType, string? query, string? filtersJson = null,
        string? jobId = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO crawl_run(id, platform, run_type, query, filters_json, status, job_id, started_at)
            VALUES (@id, @platform, @runType, @query, COALESCE(@filtersJson, '{}'), 'running', @jobId, @now)
            """, new { id, platform, runType, query, filtersJson, jobId, now = DapperSetup.NowMs }, cancellationToken: ct));
        return id;
    }

    public async Task CompleteAsync(string id, string status, int itemCount, string? error = null, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE crawl_run SET status = @status, item_count = @itemCount, error = @error, finished_at = @now WHERE id = @id",
            new { id, status, itemCount, error, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task AddItemAsync(string runId, long contentItemId, int rank, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO crawl_run_item(run_id, content_item_id, rank) VALUES (@runId, @contentItemId, @rank)",
            new { runId, contentItemId, rank }, cancellationToken: ct));
    }

    public async Task<CrawlRunRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.QuerySingleOrDefaultAsync<CrawlRunRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM crawl_run WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CrawlRunRecord>> ListAsync(string? platform = null, int limit = 100, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<CrawlRunRecord>(new CommandDefinition($"""
            SELECT {Columns} FROM crawl_run WHERE @platform IS NULL OR platform = @platform
            ORDER BY started_at DESC LIMIT @limit
            """, new { platform, limit }, cancellationToken: ct))).ToList();
    }
}
