using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Audit;

[Service]
public sealed class QuotaUsageRepository(IDbConnectionFactory factory)
{
    public async Task AddAsync(string platform, string endpoint, int units, string caller = "system", CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO quota_usage(platform, endpoint, units, caller, created_at)
            VALUES (@platform, @endpoint, @units, @caller, @now)
            """, new { platform, endpoint, units, caller, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<long> SumUnitsSinceAsync(string platform, DateTimeOffset since, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(SUM(units), 0) FROM quota_usage WHERE platform = @platform AND created_at >= @since",
            new { platform, since = since.ToUnixTimeMilliseconds() }, cancellationToken: ct));
    }
}
