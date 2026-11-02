using System.Text.Json;
using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Library;

public sealed record LibraryItem
{
    public long Id { get; init; }
    public string Platform { get; init; } = "";
    public string Kind { get; init; } = "";
    public string ExternalId { get; init; } = "";
    public string? Title { get; init; }
    public string? AuthorName { get; init; }
    public string? Url { get; init; }
    public string? ThumbnailUrl { get; init; }
    public long? DurationMs { get; init; }
    public long? ViewCount { get; init; }
    public long? LikeCount { get; init; }
    public long? CommentCount { get; init; }
    public string? Language { get; init; }
    public string? Category { get; init; }
    public string? Project { get; init; }
    public string? Campaign { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Favorite { get; init; }
    public long? MediaFileId { get; init; }
    public string? LocalPath { get; init; }
    public long LastSeenAt { get; init; }
}

public sealed record LibraryFilter(string? Platform = null, string? Project = null, string? Campaign = null,
    string? Text = null, bool? Favorite = null, int Limit = 100, int Offset = 0);

/// <summary>Trường null = giữ nguyên; Tags null = giữ nguyên, danh sách rỗng = xoá hết nhãn.</summary>
public sealed record LibraryPatch(string? Project = null, string? Campaign = null, bool? Favorite = null,
    IReadOnlyList<string>? Tags = null, string? Category = null, string? Language = null, long? MediaFileId = null);

/// <summary>Thư viện = content_item.in_library = 1; file cục bộ lấy qua media_file_id.</summary>
[Service]
public sealed class LibraryRepository(IDbConnectionFactory factory)
{
    private sealed class Row
    {
        public long Id { get; set; }
        public string Platform { get; set; } = "";
        public string Kind { get; set; } = "";
        public string ExternalId { get; set; } = "";
        public string? Title { get; set; }
        public string? AuthorName { get; set; }
        public string? Url { get; set; }
        public string? ThumbnailUrl { get; set; }
        public long? DurationMs { get; set; }
        public long? ViewCount { get; set; }
        public long? LikeCount { get; set; }
        public long? CommentCount { get; set; }
        public string? Language { get; set; }
        public string? Category { get; set; }
        public string? Project { get; set; }
        public string? Campaign { get; set; }
        public string? TagsJson { get; set; }
        public long Favorite { get; set; }
        public long? MediaFileId { get; set; }
        public string? LocalPath { get; set; }
        public long LastSeenAt { get; set; }
    }

    private const string Select = """
        SELECT c.id, c.platform, c.kind, c.external_id, c.title, c.author_name, c.url, c.thumbnail_url, c.duration_ms,
               c.view_count, c.like_count, c.comment_count, c.language, c.category, c.project, c.campaign, c.tags_json,
               c.favorite, c.media_file_id, m.path AS local_path, c.last_seen_at
        FROM content_item c LEFT JOIN media_file m ON m.id = c.media_file_id
        """;

    public async Task<IReadOnlyList<LibraryItem>> QueryAsync(LibraryFilter filter, CancellationToken ct = default)
    {
        var like = string.IsNullOrWhiteSpace(filter.Text) ? null : "%" + Escape(filter.Text.Trim()) + "%";
        await using var db = factory.Open();
        var rows = await db.QueryAsync<Row>(new CommandDefinition($"""
            {Select}
            WHERE c.in_library = 1
              AND (@Platform IS NULL OR c.platform = @Platform)
              AND (@Project IS NULL OR c.project = @Project)
              AND (@Campaign IS NULL OR c.campaign = @Campaign)
              AND (@Favorite IS NULL OR c.favorite = @Favorite)
              AND (@like IS NULL OR c.title LIKE @like ESCAPE '\' OR c.author_name LIKE @like ESCAPE '\' OR c.url LIKE @like ESCAPE '\')
            ORDER BY c.last_seen_at DESC, c.id DESC LIMIT @Limit OFFSET @Offset
            """,
            new
            {
                Platform = NullIfAll(filter.Platform), filter.Project, filter.Campaign,
                Favorite = filter.Favorite is { } f ? (f ? 1 : 0) : (int?)null, like, filter.Limit, filter.Offset
            }, cancellationToken: ct));
        return rows.Select(ToItem).ToList();
    }

    public async Task<LibraryItem?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"{Select} WHERE c.id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : ToItem(row);
    }

    /// <summary>Gắn file đã tải cho các mục thư viện có cùng URL (không ghi đè mục đã có file khác).</summary>
    public async Task<int> LinkMediaByUrlAsync(string url, long mediaFileId, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "UPDATE content_item SET media_file_id = @mediaFileId WHERE in_library = 1 AND url = @url",
            new { url, mediaFileId }, cancellationToken: ct));
    }

    public async Task<LibraryItem?> UpdateAsync(long id, LibraryPatch patch, CancellationToken ct = default)
    {
        await using (var db = factory.Open())
        {
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE content_item SET
                  project = COALESCE(@Project, project), campaign = COALESCE(@Campaign, campaign),
                  favorite = COALESCE(@Favorite, favorite), tags_json = COALESCE(@tags, tags_json),
                  category = COALESCE(@Category, category), language = COALESCE(@Language, language),
                  media_file_id = COALESCE(@MediaFileId, media_file_id)
                WHERE id = @id AND in_library = 1
                """,
                new
                {
                    id, patch.Project, patch.Campaign, Favorite = patch.Favorite is { } f ? (f ? 1 : 0) : (int?)null,
                    tags = patch.Tags is null ? null : JsonSerializer.Serialize(patch.Tags), patch.Category, patch.Language, patch.MediaFileId
                }, cancellationToken: ct));
        }

        return await GetAsync(id, ct);
    }

    /// <summary>Chỉ xoá mục đang ở thư viện; thực thể/kênh chỉ được crawl (in_library = 0) giữ nguyên.</summary>
    public async Task<int> DeleteAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM content_item WHERE in_library = 1 AND id IN (SELECT value FROM json_each(@ids))",
            new { ids = JsonSerializer.Serialize(ids) }, cancellationToken: ct));
    }

    private static string? NullIfAll(string? platform) =>
        string.IsNullOrWhiteSpace(platform) || platform.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : platform;

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static LibraryItem ToItem(Row r) => new()
    {
        Id = r.Id, Platform = r.Platform, Kind = r.Kind, ExternalId = r.ExternalId, Title = r.Title, AuthorName = r.AuthorName,
        Url = r.Url, ThumbnailUrl = r.ThumbnailUrl, DurationMs = r.DurationMs, ViewCount = r.ViewCount, LikeCount = r.LikeCount,
        CommentCount = r.CommentCount, Language = r.Language, Category = r.Category, Project = r.Project, Campaign = r.Campaign,
        Tags = ParseTags(r.TagsJson), Favorite = r.Favorite != 0, MediaFileId = r.MediaFileId, LocalPath = r.LocalPath,
        LastSeenAt = r.LastSeenAt
    };

    private static IReadOnlyList<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
