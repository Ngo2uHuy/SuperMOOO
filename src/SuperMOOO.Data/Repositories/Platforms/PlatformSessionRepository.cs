using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Platforms;

public sealed record PlatformSessionRecord
{
    public string Platform { get; init; } = "";
    public string AuthType { get; init; } = "none";
    public string Status { get; init; } = "not_configured";
    public string? Message { get; init; }
    public long? CheckedAt { get; init; }
    public long? ExpiresAt { get; init; }
    public long UpdatedAt { get; init; }
}

/// <summary>Chỉ lưu trạng thái phiên; cookie/token nằm ở ISecretStore (scope platform:&lt;p&gt;).</summary>
[Service]
public sealed class PlatformSessionRepository(IDbConnectionFactory factory)
{
    private const string Columns = "platform, auth_type, status, message, checked_at, expires_at, updated_at";

    public async Task<PlatformSessionRecord> UpsertAsync(string platform, string authType, string status, string? message,
        long? expiresAt = null, CancellationToken ct = default)
    {
        var now = DapperSetup.NowMs;
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO platform_session(platform, auth_type, status, message, checked_at, expires_at, updated_at)
            VALUES (@platform, @authType, @status, @message, @now, @expiresAt, @now)
            ON CONFLICT(platform) DO UPDATE SET auth_type = excluded.auth_type, status = excluded.status,
              message = excluded.message, checked_at = excluded.checked_at, expires_at = excluded.expires_at, updated_at = excluded.updated_at
            """, new { platform, authType, status, message, expiresAt, now }, cancellationToken: ct));
        return new PlatformSessionRecord
        {
            Platform = platform, AuthType = authType, Status = status, Message = message,
            CheckedAt = now, ExpiresAt = expiresAt, UpdatedAt = now
        };
    }

    public async Task<IReadOnlyList<PlatformSessionRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<PlatformSessionRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM platform_session ORDER BY platform", cancellationToken: ct))).ToList();
    }
}
