using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Live;

[Service(As = [typeof(ILiveStore)])]
public sealed class LiveRepository(IDbConnectionFactory factory) : ILiveStore
{
    private sealed record ProductRow(string Id, string Name, string Facts, string Script, string? ClipPath, long UpdatedAt);
    private sealed record FaqRow(string Id, string ProductId, string Question, string Answer, string? ClipPath, long UpdatedAt);

    public async Task<IReadOnlyList<LiveProduct>> ListProductsAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<ProductRow>(new CommandDefinition(
            "SELECT id AS Id, name AS Name, facts AS Facts, script AS Script, clip_path AS ClipPath, updated_at AS UpdatedAt FROM live_product ORDER BY name COLLATE NOCASE",
            cancellationToken: ct));
        return rows.Select(r => new LiveProduct(r.Id, r.Name, r.Facts, r.Script, r.ClipPath, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt))).ToList();
    }

    public async Task UpsertProductAsync(LiveProduct p, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO live_product(id, name, facts, script, clip_path, updated_at) VALUES (@Id, @Name, @Facts, @Script, @ClipPath, @now)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, facts = excluded.facts, script = excluded.script,
              clip_path = excluded.clip_path, updated_at = excluded.updated_at
            """, new { p.Id, p.Name, p.Facts, p.Script, p.ClipPath, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<bool> DeleteProductAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        // Xoá FAQ tường minh: không phụ thuộc PRAGMA foreign_keys của từng kết nối.
        await db.ExecuteAsync(new CommandDefinition("DELETE FROM live_faq WHERE product_id = @id", new { id }, cancellationToken: ct));
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM live_product WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<LiveFaq>> ListFaqsAsync(string productId, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<FaqRow>(new CommandDefinition(
            "SELECT id AS Id, product_id AS ProductId, question AS Question, answer AS Answer, clip_path AS ClipPath, updated_at AS UpdatedAt " +
            "FROM live_faq WHERE product_id = @productId ORDER BY rowid", new { productId }, cancellationToken: ct));
        return rows.Select(r => new LiveFaq(r.Id, r.ProductId, r.Question, r.Answer, r.ClipPath, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt))).ToList();
    }

    public async Task UpsertFaqAsync(LiveFaq f, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO live_faq(id, product_id, question, answer, clip_path, updated_at) VALUES (@Id, @ProductId, @Question, @Answer, @ClipPath, @now)
            ON CONFLICT(id) DO UPDATE SET question = excluded.question, answer = excluded.answer, clip_path = excluded.clip_path,
              updated_at = excluded.updated_at
            """, new { f.Id, f.ProductId, f.Question, f.Answer, f.ClipPath, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<bool> DeleteFaqAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM live_faq WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }
}
