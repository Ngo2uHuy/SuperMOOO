using Microsoft.Data.Sqlite;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Data.Sqlite;
using static SuperMOOO.Data.Repositories.Publishing.PubSql;

namespace SuperMOOO.Data.Repositories.Publishing;

[Service(As = [typeof(ISourceRepository)])]
public sealed class SourceRepository(IDbConnectionFactory factory) : ISourceRepository
{
    private const string Columns = "id, name, kind, root_path, import_mode, recursive, watch_enabled, enabled, created_at, updated_at";

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public SourceKind Kind { get; set; }
        public string RootPath { get; set; } = "";
        public ImportMode ImportMode { get; set; }
        public bool Recursive { get; set; }
        public bool WatchEnabled { get; set; }
        public bool Enabled { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }

        public ContentSource ToSource() => new()
        {
            Id = Id, Name = Name, Kind = Kind, RootPath = RootPath, ImportMode = ImportMode, Recursive = Recursive, WatchEnabled = WatchEnabled,
            Enabled = Enabled, CreatedUtc = FromMs(CreatedAt), UpdatedUtc = FromMs(UpdatedAt)
        };
    }

    public async Task<IReadOnlyList<ContentSource>> ListAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_content_sources ORDER BY name COLLATE NOCASE, id", cancellationToken: ct))).Select(r => r.ToSource()).ToList();
    }

    public async Task<ContentSource?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_content_sources WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToSource();
    }

    public async Task UpsertAsync(ContentSource s, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pub_content_sources(id, name, kind, root_path, import_mode, recursive, watch_enabled, enabled, created_at, updated_at)
            VALUES (@Id, @Name, @Kind, @RootPath, @ImportMode, @Recursive, @WatchEnabled, @Enabled, @created, @updated)
            ON CONFLICT(id) DO UPDATE SET
              name = excluded.name, kind = excluded.kind, root_path = excluded.root_path, import_mode = excluded.import_mode,
              recursive = excluded.recursive, watch_enabled = excluded.watch_enabled, enabled = excluded.enabled,
              updated_at = excluded.updated_at
            """,
            new
            {
                s.Id, s.Name, Kind = s.Kind.ToString(), s.RootPath, ImportMode = s.ImportMode.ToString(),
                s.Recursive, s.WatchEnabled, s.Enabled, created = ToMs(s.CreatedUtc), updated = ToMs(s.UpdatedUtc)
            }, cancellationToken: ct));
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM pub_content_sources WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }
}

[Service(As = [typeof(IContentRepository)])]
public sealed class ContentRepository(IDbConnectionFactory factory) : IContentRepository
{
    private const string Columns = """
        id, source_id, source_content_id, source_job_id, title, caption, language, tags_json, duration_ms, width, height, video_sha256,
        status, imported_at, original_path, is_ai_generated, is_reupload, request_id
        """;

    private const string AssetColumns = "id, content_item_id, kind, stored_path, size_bytes, sha256, original_path";

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string? SourceId { get; set; }
        public string? SourceContentId { get; set; }
        public string? SourceJobId { get; set; }
        public string Title { get; set; } = "";
        public string? Caption { get; set; }
        public string? Language { get; set; }
        public string TagsJson { get; set; } = "[]";
        public long? DurationMs { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public string VideoSha256 { get; set; } = "";
        public ContentStatus Status { get; set; }
        public long ImportedAt { get; set; }
        public string OriginalPath { get; set; } = "";
        public bool IsAiGenerated { get; set; }
        public bool IsReupload { get; set; }
        public string? RequestId { get; set; }

        public FinishedContent ToContent() => new()
        {
            Id = Id, SourceId = SourceId, SourceContentId = SourceContentId, SourceJobId = SourceJobId, Title = Title, Caption = Caption,
            Language = Language, TagsJson = TagsJson, DurationMs = DurationMs, Width = Width, Height = Height, VideoSha256 = VideoSha256,
            Status = Status, ImportedUtc = FromMs(ImportedAt), OriginalPath = OriginalPath, IsAiGenerated = IsAiGenerated,
            IsReupload = IsReupload, RequestId = RequestId
        };
    }

    public async Task<string?> FindBySourceAsync(string sourceId, string sourceContentId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await FindBySourceAsync(db, null, sourceId, sourceContentId, ct);
    }

    public async Task<string?> FindBySha256Async(string sha256, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await FindBySha256Async(db, null, sha256, ct);
    }

    public async Task<AddContentResult> AddAsync(FinishedContent item, IReadOnlyList<MediaAsset> assets, CancellationToken ct)
    {
        await using var db = factory.Open();
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            if (item.SourceId is not null && item.SourceContentId is not null
                && await FindBySourceAsync(db, tx, item.SourceId, item.SourceContentId, ct) is { } bySource)
                return new AddContentResult.Duplicate(bySource);
            if (await FindBySha256Async(db, tx, item.VideoSha256, ct) is { } bySha)
                return new AddContentResult.Duplicate(bySha);

            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO pub_content_items(id, source_id, source_content_id, source_job_id, title, caption, language, tags_json,
                  duration_ms, width, height, video_sha256, status, imported_at, original_path, is_ai_generated, is_reupload, request_id)
                VALUES (@Id, @SourceId, @SourceContentId, @SourceJobId, @Title, @Caption, @Language, @TagsJson,
                  @DurationMs, @Width, @Height, @VideoSha256, @Status, @imported, @OriginalPath, @IsAiGenerated, @IsReupload, @RequestId)
                """,
                new
                {
                    item.Id, item.SourceId, item.SourceContentId, item.SourceJobId, item.Title, item.Caption, item.Language,
                    item.TagsJson, item.DurationMs, item.Width, item.Height, item.VideoSha256,
                    Status = item.Status.ToString(), imported = ToMs(item.ImportedUtc), item.OriginalPath, item.IsAiGenerated, item.IsReupload,
                    item.RequestId
                }, tx, cancellationToken: ct));

            foreach (var a in assets)
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO pub_media_assets(id, content_item_id, kind, stored_path, size_bytes, sha256, original_path)
                    VALUES (@Id, @ContentItemId, @Kind, @StoredPath, @SizeBytes, @Sha256, @OriginalPath)
                    """,
                    new { a.Id, a.ContentItemId, Kind = a.Kind.ToString(), a.StoredPath, a.SizeBytes, a.Sha256, a.OriginalPath },
                    tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return new AddContentResult.Added();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint)
        {
            // Hai import song song cùng nội dung: người thua cuộc nhận lại id của người thắng.
            var existing = await FindBySha256Async(db, null, item.VideoSha256, ct)
                           ?? (item.SourceId is not null && item.SourceContentId is not null
                               ? await FindBySourceAsync(db, null, item.SourceId, item.SourceContentId, ct) : null);
            if (existing is null) throw;
            return new AddContentResult.Duplicate(existing);
        }
    }

    public async Task<(IReadOnlyList<(FinishedContent Item, string? ThumbnailPath)> Items, int Total)> QueryAsync(
        string? search, string? sourceId, int page, int pageSize, CancellationToken ct, ContentStatus? status = null)
    {
        await using var db = factory.Open();
        var like = string.IsNullOrWhiteSpace(search) ? null : "%" + EscapeLike(search) + "%";
        var args = new { like, sourceId, status = status?.ToString(), size = pageSize, offset = (page - 1) * pageSize };
        const string where = """
            WHERE (@like IS NULL OR title LIKE @like ESCAPE '!' OR caption LIKE @like ESCAPE '!' OR tags_json LIKE @like ESCAPE '!')
              AND (@sourceId IS NULL OR source_id = @sourceId)
              AND (@status IS NULL OR status = @status)
            """;

        var total = await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM pub_content_items " + where, args, cancellationToken: ct));
        var items = (await db.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_content_items {where} ORDER BY imported_at DESC, id LIMIT @size OFFSET @offset",
            args, cancellationToken: ct))).Select(r => r.ToContent()).ToList();

        var thumbs = new Dictionary<string, string>();
        if (items.Count > 0)
        {
            var rows = await db.QueryAsync<(string ContentItemId, string StoredPath)>(new CommandDefinition(
                "SELECT content_item_id, stored_path FROM pub_media_assets WHERE kind = 'Thumbnail' AND content_item_id IN @ids",
                new { ids = items.Select(i => i.Id).ToList() }, cancellationToken: ct));
            foreach (var (cid, path) in rows) thumbs[cid] = path;
        }
        return (items.Select(i => (i, thumbs.GetValueOrDefault(i.Id))).ToList(), total);
    }

    public async Task<ContentDetail?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_content_items WHERE id = @id", new { id }, cancellationToken: ct));
        if (row is null) return null;
        var assets = (await db.QueryAsync<MediaAsset>(new CommandDefinition(
            $"SELECT {AssetColumns} FROM pub_media_assets WHERE content_item_id = @id ORDER BY kind, id", new { id },
            cancellationToken: ct))).ToList();
        return new ContentDetail(row.ToContent(), assets);
    }

    public async Task<IReadOnlyList<FinishedContent>> ListReadyAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_content_items WHERE status = 'Ready' ORDER BY imported_at, id", cancellationToken: ct)))
            .Select(r => r.ToContent()).ToList();
    }

    public async Task<string?> FindByOriginalPathAsync(string path, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT id FROM pub_content_items WHERE original_path = @path COLLATE NOCASE ORDER BY imported_at LIMIT 1",
            new { path }, cancellationToken: ct));
    }

    public async Task<bool> UpdateAsync(string id, string? tagsJson, bool? isAiGenerated, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_content_items SET tags_json = COALESCE(@tagsJson, tags_json), is_ai_generated = COALESCE(@isAiGenerated, is_ai_generated)
            WHERE id = @id
            """, new { id, tagsJson, isAiGenerated }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> UpdateMetadataAsync(string id, ContentMetadataUpdate u, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_content_items SET title = COALESCE(@Title, title), caption = COALESCE(@Caption, caption), language = COALESCE(@Language, language),
              tags_json = COALESCE(@TagsJson, tags_json), is_ai_generated = COALESCE(@IsAiGenerated, is_ai_generated),
              is_reupload = COALESCE(@IsReupload, is_reupload), status = COALESCE(@status, status)
            WHERE id = @id
            """, new { id, u.Title, u.Caption, u.Language, u.TagsJson, u.IsAiGenerated, u.IsReupload, status = u.Status?.ToString() },
            cancellationToken: ct)) > 0;
    }

    private sealed class PostRow
    {
        public string Id { get; set; } = "";
        public string DestinationTitle { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Status { get; set; } = "";
        public long? ScheduledAt { get; set; }
        public long? PublishedAt { get; set; }
        public string? RemoteUrl { get; set; }
    }

    public async Task<IReadOnlyList<ContentPostRow>> ListPostsAsync(string contentId, CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<PostRow>(new CommandDefinition("""
            SELECT p.id AS Id, d.title AS DestinationTitle, d.platform AS Platform, p.status AS Status, p.scheduled_at AS ScheduledAt,
                   p.published_at AS PublishedAt, p.remote_url AS RemoteUrl
            FROM pub_publications p JOIN pub_destinations d ON d.id = p.destination_id
            WHERE p.content_item_id = @contentId ORDER BY p.created_at DESC LIMIT 200
            """, new { contentId }, cancellationToken: ct));
        return rows.Select(r => new ContentPostRow(r.Id, r.DestinationTitle, Enum.TryParse<SocialPlatform>(r.Platform, out var pl) ? pl : SocialPlatform.Other,
            Enum.Parse<PublicationStatus>(r.Status), FromMs(r.ScheduledAt), FromMs(r.PublishedAt), r.RemoteUrl)).ToList();
    }

    public async Task<IReadOnlySet<string>> ListPendingMediaPathsAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        var paths = await db.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT m.stored_path FROM pub_media_assets m
            WHERE m.content_item_id IN (SELECT content_item_id FROM pub_publications
              WHERE status IN ('Draft','Validating','AwaitingApproval','Scheduled','Uploading','Processing','RetryScheduled','NeedsAction','Unknown','AwaitingUser'))
            """, cancellationToken: ct));
        return paths.Select(p => p.ToLowerInvariant()).ToHashSet();
    }

    private static string EscapeLike(string s) => s.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");

    private static Task<string?> FindBySourceAsync(DbConnection db, DbTransaction? tx, string sourceId, string contentId, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT id FROM pub_content_items WHERE source_id = @sourceId AND source_content_id = @contentId",
            new { sourceId, contentId }, tx, cancellationToken: ct));

    private static Task<string?> FindBySha256Async(DbConnection db, DbTransaction? tx, string sha256, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT id FROM pub_content_items WHERE video_sha256 = @sha256", new { sha256 }, tx, cancellationToken: ct));
}

[Service(As = [typeof(IActionRequiredRepository)])]
public sealed class ActionRequiredRepository(IDbConnectionFactory factory) : IActionRequiredRepository
{
    private const string Columns = "id, kind, ref_id, title, detail, created_at, resolved_at";

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string RefId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public long CreatedAt { get; set; }
        public long? ResolvedAt { get; set; }

        public ActionRequired ToAction() => new()
        {
            Id = Id, Kind = Kind, RefId = RefId, Title = Title, Detail = Detail, CreatedUtc = FromMs(CreatedAt), ResolvedUtc = FromMs(ResolvedAt)
        };
    }

    public async Task<string> RaiseAsync(string kind, string refId, string title, string detail, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO action_item(id, kind, ref_id, title, detail, created_at)
            VALUES (@id, @kind, @refId, @title, @detail, @now)
            ON CONFLICT(kind, ref_id) WHERE resolved_at IS NULL DO NOTHING
            """, new { id = Guid.NewGuid().ToString("N"), kind, refId, title, detail, now = DapperSetup.NowMs }, cancellationToken: ct));
        return await db.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT id FROM action_item WHERE kind = @kind AND ref_id = @refId AND resolved_at IS NULL",
            new { kind, refId }, cancellationToken: ct));
    }

    public async Task<bool> ResolveAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "UPDATE action_item SET resolved_at = @now WHERE id = @id AND resolved_at IS NULL",
            new { id, now = DapperSetup.NowMs }, cancellationToken: ct)) > 0;
    }

    public async Task<int> ResolveByRefAsync(string kind, string refId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "UPDATE action_item SET resolved_at = @now WHERE kind = @kind AND ref_id = @refId AND resolved_at IS NULL",
            new { kind, refId, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<(IReadOnlyList<ActionRequired> Items, int Total)> ListAsync(bool includeResolved, int page, int pageSize, CancellationToken ct)
    {
        await using var db = factory.Open();
        var args = new { includeResolved, size = pageSize, offset = (page - 1) * pageSize };
        var total = await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM action_item WHERE @includeResolved = 1 OR resolved_at IS NULL", args, cancellationToken: ct));
        var items = (await db.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM action_item WHERE @includeResolved = 1 OR resolved_at IS NULL
            ORDER BY created_at DESC, id LIMIT @size OFFSET @offset
            """, args, cancellationToken: ct))).Select(r => r.ToAction()).ToList();
        return (items, total);
    }

    public async Task<IReadOnlyList<ActionRequired>> ListOpenByKindAsync(string kind, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM action_item WHERE kind = @kind AND resolved_at IS NULL", new { kind }, cancellationToken: ct)))
            .Select(r => r.ToAction()).ToList();
    }

    public async Task<int> CountOpenAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM action_item WHERE resolved_at IS NULL", cancellationToken: ct));
    }
}

[Service(As = [typeof(IFingerprintStore)])]
public sealed class FingerprintStore(IDbConnectionFactory factory) : IFingerprintStore
{
    private sealed class Row
    {
        public long SizeBytes { get; set; }
        public long LastWriteAt { get; set; }
        public string Sha256 { get; set; } = "";
    }

    // last_write_at lưu tick UTC (100 ns) thay vì ms: mtime NTFS có đủ tick, so sánh bằng ms sẽ không bao giờ khớp nên cache vô dụng.
    public async Task<(long SizeBytes, DateTimeOffset LastWriteUtc, string Sha256)?> GetAsync(string path, CancellationToken ct)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT size_bytes, last_write_at, sha256 FROM pub_file_fingerprints WHERE path = @path", new { path }, cancellationToken: ct));
        return row is null ? null : (row.SizeBytes, new DateTimeOffset(row.LastWriteAt, TimeSpan.Zero), row.Sha256);
    }

    public async Task UpsertAsync(string path, long sizeBytes, DateTimeOffset lastWriteUtc, string sha256, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pub_file_fingerprints(path, size_bytes, last_write_at, sha256) VALUES (@path, @sizeBytes, @lastWrite, @sha256)
            ON CONFLICT(path) DO UPDATE SET size_bytes = excluded.size_bytes, last_write_at = excluded.last_write_at, sha256 = excluded.sha256
            """,
            new { path, sizeBytes, lastWrite = lastWriteUtc.UtcTicks, sha256 }, cancellationToken: ct));
    }
}
