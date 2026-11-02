using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Files;

public sealed record MediaFileRecord(string Path, string Kind, string? Operation = null, string? SourceUrl = null,
    long? SizeBytes = null, string? Mime = null, long? DurationMs = null, string? Sha256 = null, string? JobId = null,
    long Id = 0);

[Service(As = [typeof(IOutputRegistry)])]
public sealed class MediaFileRepository(IDbConnectionFactory factory) : IOutputRegistry
{
    private sealed class Row
    {
        public string Path { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Operation { get; set; }
        public string? SourceUrl { get; set; }
        public long? SizeBytes { get; set; }
        public string? Mime { get; set; }
        public long? DurationMs { get; set; }
        public string? Sha256 { get; set; }
        public string? JobId { get; set; }
        public long Id { get; set; }
    }

    private const string Columns = "path, kind, operation, source_url, size_bytes, mime, duration_ms, sha256, job_id, id";

    private static MediaFileRecord ToRecord(Row r) =>
        new(r.Path, r.Kind, r.Operation, r.SourceUrl, r.SizeBytes, r.Mime, r.DurationMs, r.Sha256, r.JobId, r.Id);

    /// <summary>Thêm hoặc cập nhật theo path; trả về id.</summary>
    public async Task<long> UpsertAsync(MediaFileRecord file, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO media_file(path, kind, operation, source_url, size_bytes, mime, duration_ms, sha256, job_id, created_at)
            VALUES (@Path, @Kind, @Operation, @SourceUrl, @SizeBytes, @Mime, @DurationMs, @Sha256, @JobId, @now)
            ON CONFLICT(path) DO UPDATE SET
              kind = excluded.kind, operation = COALESCE(excluded.operation, operation),
              source_url = COALESCE(excluded.source_url, source_url), size_bytes = COALESCE(excluded.size_bytes, size_bytes),
              mime = COALESCE(excluded.mime, mime), duration_ms = COALESCE(excluded.duration_ms, duration_ms),
              sha256 = COALESCE(excluded.sha256, sha256), job_id = COALESCE(excluded.job_id, job_id)
            RETURNING id
            """, new { file.Path, file.Kind, file.Operation, file.SourceUrl, file.SizeBytes, file.Mime, file.DurationMs, file.Sha256, file.JobId, now = DapperSetup.NowMs },
            cancellationToken: ct));
    }

    public async Task RegisterOutputAsync(string path, string operation, CancellationToken ct = default) =>
        await UpsertAsync(new MediaFileRecord(path, "output", operation, SizeBytes: File.Exists(path) ? new FileInfo(path).Length : null), ct);

    /// <summary>Đăng ký file theo `kind` (vd `publish`) và trả id; gọi lại cùng path thì cập nhật.</summary>
    public Task<long> RegisterAsync(string path, string kind, string? mime = null, long? sizeBytes = null, CancellationToken ct = default) =>
        UpsertAsync(new MediaFileRecord(path, kind, Mime: mime, SizeBytes: sizeBytes), ct);

    public async Task<MediaFileRecord?> GetByPathAsync(string path, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM media_file WHERE path = @path", new { path }, cancellationToken: ct));
        return row is null ? null : ToRecord(row);
    }

    /// <summary>Lịch sử file theo nhóm kind (vd download + output) và từ khoá trong path/operation/source_url; mới nhất trước.</summary>
    public async Task<IReadOnlyList<MediaFileRecord>> QueryAsync(IReadOnlyList<string>? kinds = null, string? text = null,
        int limit = 200, int offset = 0, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var like = string.IsNullOrWhiteSpace(text) ? null : "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        // json_each để truyền danh sách kind dưới dạng một tham số, giữ SQL parameterized.
        return (await db.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM media_file
            WHERE (@kinds IS NULL OR kind IN (SELECT value FROM json_each(@kinds)))
              AND (@like IS NULL OR path LIKE @like ESCAPE '\' OR operation LIKE @like ESCAPE '\' OR source_url LIKE @like ESCAPE '\')
            ORDER BY created_at DESC, id DESC LIMIT @limit OFFSET @offset
            """, new { kinds = kinds is null ? null : System.Text.Json.JsonSerializer.Serialize(kinds), like, limit, offset },
            cancellationToken: ct))).Select(ToRecord).ToList();
    }

    public async Task<MediaFileRecord?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM media_file WHERE id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : ToRecord(row);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM media_file WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<MediaFileRecord>> ListAsync(string? kind = null, int limit = 200, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM media_file WHERE @kind IS NULL OR kind = @kind
            ORDER BY created_at DESC, id DESC LIMIT @limit
            """, new { kind, limit }, cancellationToken: ct))).Select(ToRecord).ToList();
    }
}
