using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Content;

[Service]
public sealed class ContentItemRepository(IDbConnectionFactory factory)
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

    /// <summary>Thêm hoặc cập nhật theo (platform, kind, external_id); trường null không ghi đè giá trị cũ. Trả về id.</summary>
    public async Task<long> UpsertAsync(ContentItem item, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO content_item(platform, kind, external_id, title, author_id, author_name, url, thumbnail_url, content,
                published_at, duration_ms, view_count, like_count, comment_count, language, category, first_seen_at, last_seen_at)
            VALUES (@Platform, @Kind, @ExternalId, @Title, @AuthorId, @AuthorName, @Url, @ThumbnailUrl, @Content,
                @publishedAt, @durationMs, @ViewCount, @LikeCount, @CommentCount, @Language, @Category, @now, @now)
            ON CONFLICT(platform, kind, external_id) DO UPDATE SET
              title = COALESCE(excluded.title, title), author_id = COALESCE(excluded.author_id, author_id),
              author_name = COALESCE(excluded.author_name, author_name), url = COALESCE(excluded.url, url),
              thumbnail_url = COALESCE(excluded.thumbnail_url, thumbnail_url), content = COALESCE(excluded.content, content),
              published_at = COALESCE(excluded.published_at, published_at), duration_ms = COALESCE(excluded.duration_ms, duration_ms),
              view_count = COALESCE(excluded.view_count, view_count), like_count = COALESCE(excluded.like_count, like_count),
              comment_count = COALESCE(excluded.comment_count, comment_count), language = COALESCE(excluded.language, language),
              category = COALESCE(excluded.category, category), last_seen_at = excluded.last_seen_at
            RETURNING id
            """,
            new
            {
                item.Platform, item.Kind, item.ExternalId, item.Title, item.AuthorId, item.AuthorName, item.Url, item.ThumbnailUrl,
                item.Content, publishedAt = item.PublishedAt?.ToUnixTimeMilliseconds(), durationMs = (long?)item.Duration?.TotalMilliseconds,
                item.ViewCount, item.LikeCount, item.CommentCount, item.Language, item.Category, now = DapperSetup.NowMs
            }, cancellationToken: ct));
    }

    public async Task<ContentItem?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM content_item WHERE id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : ToItem(row);
    }

    public async Task SetInLibraryAsync(long id, bool inLibrary, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("UPDATE content_item SET in_library = @flag WHERE id = @id",
            new { id, flag = inLibrary ? 1 : 0 }, cancellationToken: ct));
    }

    private static ContentItem ToItem(Row r) => new(r.Platform, r.Kind, r.ExternalId, r.Title, r.AuthorId, r.AuthorName,
        r.Url, r.ThumbnailUrl, r.Content, r.PublishedAt is { } p ? DateTimeOffset.FromUnixTimeMilliseconds(p) : null,
        r.DurationMs is { } d ? TimeSpan.FromMilliseconds(d) : null, r.ViewCount, r.LikeCount, r.CommentCount,
        r.Language, r.Category, r.Id);
}
