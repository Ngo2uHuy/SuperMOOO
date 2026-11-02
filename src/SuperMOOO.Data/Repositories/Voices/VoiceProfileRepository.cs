using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Voices;

[Service(As = [typeof(IVoiceProfileStore)])]
public sealed class VoiceProfileRepository(IDbConnectionFactory factory) : IVoiceProfileStore
{
    private sealed record Row(string Id, string Provider, string Name, string? Language, string? Gender, string? RefPath,
        string? RefText, string Status, string? ExtraJson, long CreatedAt);

    private const string Columns = "id AS Id, provider AS Provider, name AS Name, language AS Language, gender AS Gender, " +
                                   "ref_path AS RefPath, ref_text AS RefText, status AS Status, extra_json AS ExtraJson, created_at AS CreatedAt";

    public async Task<IReadOnlyList<VoiceProfile>> ListAsync(string provider, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM voice_profile WHERE provider = @provider ORDER BY created_at", new { provider }, cancellationToken: ct));
        return rows.Select(ToModel).ToList();
    }

    public async Task<VoiceProfile?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM voice_profile WHERE id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : ToModel(row);
    }

    public async Task UpsertAsync(VoiceProfile p, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO voice_profile(id, provider, name, language, gender, ref_path, ref_text, status, extra_json, created_at)
            VALUES (@Id, @Provider, @Name, @Language, @Gender, @RefPath, @RefText, @Status, @ExtraJson, @now)
            ON CONFLICT(id) DO UPDATE SET provider = excluded.provider, name = excluded.name, language = excluded.language,
              gender = excluded.gender, ref_path = excluded.ref_path, ref_text = excluded.ref_text, status = excluded.status,
              extra_json = excluded.extra_json
            """, new { p.Id, p.Provider, p.Name, p.Language, p.Gender, p.RefPath, p.RefText, p.Status, p.ExtraJson,
                now = p.CreatedAt?.ToUnixTimeMilliseconds() ?? DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM voice_profile WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    private static VoiceProfile ToModel(Row r) => new(r.Id, r.Provider, r.Name, r.Language, r.Gender, r.RefPath, r.RefText,
        r.Status, r.ExtraJson, DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAt));
}
