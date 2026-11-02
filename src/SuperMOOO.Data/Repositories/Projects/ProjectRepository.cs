using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Projects;

[Service(As = [typeof(IProjectStore)])]
public sealed class ProjectRepository(IDbConnectionFactory factory) : IProjectStore
{
    private sealed record Row(string Id, string Kind, string Name, string Folder, string? StateJson, long UpdatedAt);

    private const string Columns = "id AS Id, kind AS Kind, name AS Name, folder AS Folder, state_json AS StateJson, updated_at AS UpdatedAt";

    public async Task<IReadOnlyList<ProjectRecord>> ListAsync(string kind, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM project WHERE kind = @kind ORDER BY updated_at DESC", new { kind }, cancellationToken: ct));
        return rows.Select(ToModel).ToList();
    }

    public async Task<ProjectRecord?> GetByFolderAsync(string folder, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM project WHERE folder = @folder", new { folder }, cancellationToken: ct));
        return row is null ? null : ToModel(row);
    }

    public async Task UpsertAsync(ProjectRecord p, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO project(id, kind, name, folder, state_json, updated_at) VALUES (@Id, @Kind, @Name, @Folder, @StateJson, @now)
            ON CONFLICT(folder) DO UPDATE SET kind = excluded.kind, name = excluded.name, state_json = excluded.state_json, updated_at = excluded.updated_at
            """, new { p.Id, p.Kind, p.Name, p.Folder, p.StateJson, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM project WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    private static ProjectRecord ToModel(Row r) =>
        new(r.Id, r.Kind, r.Name, r.Folder, r.StateJson, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt));
}
