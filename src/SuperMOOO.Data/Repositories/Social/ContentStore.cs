using SuperMOOO.Data.Repositories.Content;

namespace SuperMOOO.Data.Repositories.Social;

[Service(As = [typeof(IContentStore)])]
public sealed class ContentStore(IDbConnectionFactory factory, ContentItemRepository items) : IContentStore
{
    private sealed class Row
    {
        public string Platform { get; set; } = "";
        public string Kind { get; set; } = "";
        public string ExternalId { get; set; } = "";
        public string? Title { get; set; }
        public string? AuthorId { get; set; }
        public string? AuthorName { get; set; }
        public string? Url { get; set; }
        public string? ThumbnailUrl { get; set; }
        public string? Content { get; set; }
        public long? PublishedAt { get; set; }
        public long? DurationMs { get; set; }
        public long? ViewCount { get; set; }
        public long? LikeCount { get; set; }
        public long? CommentCount { get; set; }
        public string? Language { get; set; }
        public string? Category { get; set; }
        public long Id { get; set; }
    }

    private const string Columns = """
        platform, kind, external_id, title, author_id, author_name, url, thumbnail_url, content, published_at,
        duration_ms, view_count, like_count, comment_count, language, category, id
        """;

    public Task<long> UpsertAsync(ContentItem item, CancellationToken ct = default) => items.UpsertAsync(item, ct);

    public Task<ContentItem?> GetAsync(long id, CancellationToken ct = default) => items.GetAsync(id, ct);

    public Task SetInLibraryAsync(long id, bool inLibrary, CancellationToken ct = default) => items.SetInLibraryAsync(id, inLibrary, ct);

    public async Task<ContentItem?> FindAsync(string platform, string kind, string externalId, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM content_item WHERE platform = @platform AND kind = @kind AND external_id = @externalId",
            new { platform, kind, externalId }, cancellationToken: ct));
        return row is null ? null : ToItem(row);
    }

    public async Task<long?> FindIdAsync(string platform, string externalId, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM content_item WHERE platform = @platform AND external_id = @externalId ORDER BY last_seen_at DESC LIMIT 1",
            new { platform, externalId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ContentItem>> ListByKindsAsync(string? platform, IReadOnlyCollection<string> kinds, int limit,
        CancellationToken ct = default)
    {
        await using var db = factory.Open();
        // "IN @kinds": Dapper mở rộng thành tham số riêng cho từng phần tử.
        var rows = await db.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM content_item
            WHERE (@platform IS NULL OR platform = @platform) AND kind IN @kinds
            ORDER BY last_seen_at DESC, id DESC LIMIT @limit
            """, new { platform, kinds, limit }, cancellationToken: ct));
        return rows.Select(ToItem).ToList();
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM content_item WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    private static ContentItem ToItem(Row r) => new(r.Platform, r.Kind, r.ExternalId, r.Title, r.AuthorId, r.AuthorName,
        r.Url, r.ThumbnailUrl, r.Content, r.PublishedAt is { } p ? DateTimeOffset.FromUnixTimeMilliseconds(p) : null,
        r.DurationMs is { } d ? TimeSpan.FromMilliseconds(d) : null, r.ViewCount, r.LikeCount, r.CommentCount,
        r.Language, r.Category, r.Id);
}
