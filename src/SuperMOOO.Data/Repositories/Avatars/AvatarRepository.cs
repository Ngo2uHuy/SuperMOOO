using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Avatars;

[Service(As = [typeof(IAvatarStore)])]
public sealed class AvatarRepository(IDbConnectionFactory factory) : IAvatarStore
{
    private sealed record Row(string Id, string Name, string SourcePath, string NormalizedPath, string FaceStatus, long FaceSeconds, double DurationSec, long CreatedAt);

    private const string Columns = "id AS Id, name AS Name, source_path AS SourcePath, normalized_path AS NormalizedPath, face_status AS FaceStatus, " +
                                   "face_seconds AS FaceSeconds, duration_sec AS DurationSec, created_at AS CreatedAt";

    private static Avatar Map(Row r) => new(r.Id, r.Name, r.SourcePath, r.NormalizedPath, r.FaceStatus, (int)r.FaceSeconds, r.DurationSec,
        DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAt));

    public async Task<IReadOnlyList<Avatar>> ListAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM avatar ORDER BY created_at DESC", cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<Avatar?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QueryFirstOrDefaultAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM avatar WHERE id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    public async Task UpsertAsync(Avatar a, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO avatar(id, name, source_path, normalized_path, face_status, face_seconds, duration_sec, created_at)
            VALUES (@Id, @Name, @SourcePath, @NormalizedPath, @FaceStatus, @FaceSeconds, @DurationSec, @now)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, source_path = excluded.source_path, normalized_path = excluded.normalized_path,
              face_status = excluded.face_status, face_seconds = excluded.face_seconds, duration_sec = excluded.duration_sec
            """, new { a.Id, a.Name, a.SourcePath, a.NormalizedPath, a.FaceStatus, a.FaceSeconds, a.DurationSec, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task SetFaceStatusAsync(string id, string status, int faceSeconds, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("UPDATE avatar SET face_status = @status, face_seconds = @faceSeconds WHERE id = @id",
            new { id, status, faceSeconds }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM avatar WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }
}
