using SuperMOOO.Core.Publishing;
using static SuperMOOO.Data.Repositories.Publishing.PubSql;

namespace SuperMOOO.Data.Repositories.Publishing;

[Service(As = [typeof(IConnectionRepository)])]
public sealed class ConnectionRepository(IDbConnectionFactory factory) : IConnectionRepository
{
    private const string Columns = """
        id, platform, display_name, home_url, route_profile_id, ip_policy_json, api_status, provider, external_account_id, scopes,
        token_expires_at, status_detail, max_posts_per_day, min_gap_minutes, created_at, updated_at
        """;

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public SocialPlatform Platform { get; set; }
        public string DisplayName { get; set; } = "";
        public string HomeUrl { get; set; } = "";
        public string? RouteProfileId { get; set; }
        public string? IpPolicyJson { get; set; }
        public ApiStatus ApiStatus { get; set; }
        public string? Provider { get; set; }
        public string? ExternalAccountId { get; set; }
        public string? Scopes { get; set; }
        public long? TokenExpiresAt { get; set; }
        public string? StatusDetail { get; set; }
        public int? MaxPostsPerDay { get; set; }
        public int? MinGapMinutes { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }

        public SocialConnection ToConnection() => new()
        {
            Id = Id, Platform = Platform, DisplayName = DisplayName, HomeUrl = HomeUrl, RouteProfileId = RouteProfileId, IpPolicyJson = IpPolicyJson,
            ApiStatus = ApiStatus, Provider = Provider, ExternalAccountId = ExternalAccountId, Scopes = Scopes,
            TokenExpiresUtc = FromMs(TokenExpiresAt), StatusDetail = StatusDetail, MaxPostsPerDay = MaxPostsPerDay, MinGapMinutes = MinGapMinutes,
            CreatedUtc = FromMs(CreatedAt), UpdatedUtc = FromMs(UpdatedAt)
        };
    }

    public async Task<IReadOnlyList<SocialConnection>> ListAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_connections ORDER BY display_name COLLATE NOCASE, id", cancellationToken: ct)))
            .Select(r => r.ToConnection()).ToList();
    }

    public async Task<SocialConnection?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_connections WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToConnection();
    }

    public async Task UpsertAsync(SocialConnection c, CancellationToken ct)
    {
        await using var db = factory.Open();
        // Sửa thông tin không đụng các cột OAuth: đổi tên/tuyến mạng không được làm mất trạng thái kết nối API.
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pub_connections(id, platform, display_name, home_url, route_profile_id, ip_policy_json, api_status,
              max_posts_per_day, min_gap_minutes, created_at, updated_at)
            VALUES (@Id, @Platform, @DisplayName, @HomeUrl, @RouteProfileId, @IpPolicyJson, @ApiStatus, @MaxPostsPerDay, @MinGapMinutes,
              @created, @updated)
            ON CONFLICT(id) DO UPDATE SET
              platform = excluded.platform, display_name = excluded.display_name, home_url = excluded.home_url,
              route_profile_id = excluded.route_profile_id, ip_policy_json = excluded.ip_policy_json,
              max_posts_per_day = excluded.max_posts_per_day, min_gap_minutes = excluded.min_gap_minutes, updated_at = excluded.updated_at
            """,
            new
            {
                c.Id, Platform = c.Platform.ToString(), c.DisplayName, c.HomeUrl, c.RouteProfileId, c.IpPolicyJson,
                ApiStatus = c.ApiStatus.ToString(), c.MaxPostsPerDay, c.MinGapMinutes, created = ToMs(c.CreatedUtc), updated = ToMs(c.UpdatedUtc)
            }, cancellationToken: ct));
    }

    public async Task UpdateOAuthAsync(string id, ApiStatus status, string? provider, string? externalAccountId, string? scopes,
        DateTimeOffset? tokenExpiresUtc, string? statusDetail, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_connections SET api_status = @status, provider = @provider, external_account_id = @externalAccountId,
              scopes = @scopes, token_expires_at = @expires, status_detail = @statusDetail, updated_at = @now
            WHERE id = @id
            """, new { id, status = status.ToString(), provider, externalAccountId, scopes, expires = ToMs(tokenExpiresUtc), statusDetail, now = ToMs(now) },
            cancellationToken: ct));
    }

    public async Task SetApiStatusAsync(string id, ApiStatus status, string? statusDetail, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE pub_connections SET api_status = @status, status_detail = @statusDetail, updated_at = @now WHERE id = @id",
            new { id, status = status.ToString(), statusDetail, now = ToMs(now) }, cancellationToken: ct));
    }

    public async Task SetTokenExpiryAsync(string id, DateTimeOffset? tokenExpiresUtc, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE pub_connections SET token_expires_at = @expires, updated_at = @now WHERE id = @id",
            new { id, expires = ToMs(tokenExpiresUtc), now = ToMs(now) }, cancellationToken: ct));
    }

    public async Task<SocialConnection?> FindByExternalAsync(string provider, string externalAccountId, CancellationToken ct)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_connections WHERE provider = @provider AND external_account_id = @externalAccountId",
            new { provider, externalAccountId }, cancellationToken: ct));
        return row?.ToConnection();
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM pub_connections WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }
}

[Service(As = [typeof(IDestinationRepository)])]
public sealed class DestinationRepository(IDbConnectionFactory factory) : IDestinationRepository
{
    private const string Columns = """
        id, connection_id, platform, remote_id, title, url, uploads_playlist_id, attributes_json, capabilities_json, enabled,
        synced_at, removed_at, created_at, updated_at
        """;

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public SocialPlatform Platform { get; set; }
        public string RemoteId { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Url { get; set; }
        public string? UploadsPlaylistId { get; set; }
        public string AttributesJson { get; set; } = "{}";
        public string CapabilitiesJson { get; set; } = "[]";
        public bool Enabled { get; set; }
        public long SyncedAt { get; set; }
        public long? RemovedAt { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }

        public SocialDestination ToDestination() => new()
        {
            Id = Id, ConnectionId = ConnectionId, Platform = Platform, RemoteId = RemoteId, Title = Title, Url = Url,
            UploadsPlaylistId = UploadsPlaylistId, AttributesJson = AttributesJson, CapabilitiesJson = CapabilitiesJson, Enabled = Enabled,
            SyncedUtc = FromMs(SyncedAt), RemovedUtc = FromMs(RemovedAt), CreatedUtc = FromMs(CreatedAt), UpdatedUtc = FromMs(UpdatedAt)
        };
    }

    public async Task<IReadOnlyList<SocialDestination>> ListAsync(string? connectionId, bool includeRemoved, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM pub_destinations
            WHERE (@connectionId IS NULL OR connection_id = @connectionId) AND (@includeRemoved = 1 OR removed_at IS NULL)
            ORDER BY title COLLATE NOCASE, id
            """, new { connectionId, includeRemoved }, cancellationToken: ct))).Select(r => r.ToDestination()).ToList();
    }

    public async Task<SocialDestination?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_destinations WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToDestination();
    }

    public async Task SyncAsync(string connectionId, SocialPlatform platform, IReadOnlyList<DestinationSnapshot> current, DateTimeOffset now,
        CancellationToken ct)
    {
        var at = ToMs(now);
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var d in current)
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO pub_destinations(id, connection_id, platform, remote_id, title, url, uploads_playlist_id, attributes_json,
                  capabilities_json, enabled, synced_at, removed_at, created_at, updated_at)
                VALUES (@id, @connectionId, @platform, @RemoteId, @Title, @Url, @UploadsPlaylistId, @AttributesJson,
                  @CapabilitiesJson, 1, @at, NULL, @at, @at)
                ON CONFLICT(connection_id, remote_id) DO UPDATE SET
                  title = excluded.title, url = excluded.url, uploads_playlist_id = excluded.uploads_playlist_id,
                  attributes_json = excluded.attributes_json, capabilities_json = excluded.capabilities_json,
                  synced_at = excluded.synced_at, removed_at = NULL, updated_at = excluded.updated_at
                """,
                new
                {
                    id = Guid.NewGuid().ToString("N"), connectionId, platform = (d.Platform ?? platform).ToString(), d.RemoteId, d.Title, d.Url,
                    d.UploadsPlaylistId, d.AttributesJson, d.CapabilitiesJson, at
                }, tx, cancellationToken: ct));

        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_destinations SET removed_at = @at, updated_at = @at
            WHERE connection_id = @connectionId AND removed_at IS NULL AND remote_id NOT IN @ids
            """, new { connectionId, at, ids = current.Select(d => d.RemoteId).ToList() }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
