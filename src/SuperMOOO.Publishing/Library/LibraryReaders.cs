using System.Text.Json;

namespace SuperMOOO.Publishing.Library;

/// <summary>manifest.json v1 (docs/producer-contract.md của CenterPost): thư mục thành phẩm có manifest, video/thumbnail/phụ đề là đường dẫn tương đối.</summary>
public static class ManifestReader
{
    public const string FileName = "manifest.json";
    public const int SupportedSchemaVersion = 1;
    public const long MaxBytes = 1_048_576;

    public static IngestRequest Read(string contentDir, string? sourceId)
    {
        var manifestPath = Path.Combine(contentDir, FileName);
        var info = new FileInfo(manifestPath);
        if (!info.Exists) throw new IngestException("Không có manifest.json.");
        if (info.Length > MaxBytes) throw new IngestException("manifest.json vượt quá 1 MB.");

        JsonObject m;
        try
        {
            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            m = JsonNode.Parse(stream) as JsonObject ?? throw new IngestException("manifest.json phải là một đối tượng JSON.");
        }
        catch (JsonException e) { throw new IngestException($"manifest.json không hợp lệ: {e.Message}"); }
        catch (IOException) { throw new IngestNotReadyException("manifest.json đang được ghi."); }

        if (Num(m, "schemaVersion") != SupportedSchemaVersion) throw new IngestException($"schemaVersion {Num(m, "schemaVersion")} chưa được hỗ trợ.");
        var contentId = Str(m, "contentId") ?? throw new IngestException("Thiếu contentId.");
        var title = Str(m, "title") ?? throw new IngestException("Thiếu title.");
        var width = (int?)Num(m, "width");
        var height = (int?)Num(m, "height");
        var duration = Num(m, "duration");
        if (width is <= 0) throw new IngestException("width phải > 0.");
        if (height is <= 0) throw new IngestException("height phải > 0.");
        if (duration is <= 0) throw new IngestException("duration phải > 0.");

        var video = Resolve(contentDir, Str(m, "video"), "video", required: true)!;
        var thumb = Resolve(contentDir, Str(m, "thumbnail"), "thumbnail", required: false);
        var subs = Resolve(contentDir, Str(m, "subtitles"), "subtitles", required: false);

        const string prefix = "sha256:";
        var checksum = Str(m, "checksum") ?? "";
        if (!checksum.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || checksum.Length != prefix.Length + 64
            || checksum.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdefABCDEF"))
            throw new IngestException("checksum phải có dạng sha256:<64 hex>.");

        var tags = m["tags"] is JsonArray a ? a.Select(t => t?.GetValue<string>()).OfType<string>().ToList() : [];
        return new IngestRequest(video, title, sourceId, contentId, Str(m, "sourceJobId"), Str(m, "caption"), Str(m, "language"), tags,
            IsReupload: string.Equals(Str(m, "origin"), "reup", StringComparison.OrdinalIgnoreCase), RequestId: Str(m, "requestId"),
            DurationMs: duration is { } d ? (long)Math.Round(d * 1000) : null, Width: width, Height: height,
            ThumbnailPath: thumb, SubtitlePath: subs, ExpectedSha256: checksum[prefix.Length..].ToLowerInvariant());
    }

    /// <summary>Ưu tiên manifest khi thư mục đã có manifest.json hợp lệ; null = dùng meta.json hoặc file rời.
    /// <paramref name="onlyVideoPath"/>: chỉ nhận khi manifest mô tả đúng file video này (một thư mục có thể chứa nhiều video).</summary>
    public static IngestRequest? TryReadPreferred(string? dir, string? sourceId, string? onlyVideoPath = null)
    {
        if (dir is null || !File.Exists(Path.Combine(dir, FileName))) return null;
        try
        {
            var request = Read(dir, sourceId);
            return onlyVideoPath is not null && PathRules.Key(request.VideoPath) != PathRules.Key(onlyVideoPath) ? null : request;
        }
        // Manifest sai/chưa hoàn tất không được chặn nhập: dùng cách đọc cũ.
        catch (Exception e) when (e is IngestException or IngestNotReadyException) { return null; }
    }

    internal static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    internal static double? Num(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    internal static string? Resolve(string root, string? relative, string label, bool required)
    {
        if (string.IsNullOrWhiteSpace(relative)) return required ? throw new IngestException($"Thiếu đường dẫn {label}.") : null;
        if (!PathRules.TryResolveInside(root, relative, out var full))
            throw new IngestException($"Đường dẫn {label} không hợp lệ hoặc nằm ngoài thư mục nội dung.");
        if (!File.Exists(full)) throw new IngestException($"Không tìm thấy file {label}.");
        return full;
    }
}

/// <summary>meta.json của VideoC: thư mục hoàn tất khi meta.json (ghi cuối cùng) có và không còn _work; SEO lấy từ script.json.</summary>
public static class VideoCMetaReader
{
    public const string MetaFileName = "meta.json";
    public const string WorkDirName = "_work";

    public static bool IsComplete(string dir) => File.Exists(Path.Combine(dir, MetaFileName)) && !Directory.Exists(Path.Combine(dir, WorkDirName));

    public static IngestRequest Read(string dir, string? sourceId)
    {
        if (!IsComplete(dir)) throw new IngestNotReadyException("Thư mục VideoC chưa hoàn tất (thiếu meta.json hoặc còn _work).");

        JsonObject meta;
        try
        {
            using var stream = new FileStream(Path.Combine(dir, MetaFileName), FileMode.Open, FileAccess.Read, FileShare.Read);
            meta = JsonNode.Parse(stream) as JsonObject ?? throw new IngestException("meta.json phải là object.");
        }
        catch (JsonException e) { throw new IngestException($"meta.json không hợp lệ: {e.Message}"); }
        catch (IOException) { throw new IngestNotReadyException("meta.json đang được ghi."); }

        var id = ManifestReader.Str(meta, "id") ?? throw new IngestException("meta.json thiếu id.");
        var title = ManifestReader.Str(meta, "title") ?? ManifestReader.Str(meta, "topic") ?? Path.GetFileName(dir);
        var files = meta["files"] as JsonObject;
        var video = ManifestReader.Resolve(dir, files is null ? null : ManifestReader.Str(files, "video"), "video", required: true)!;
        var thumb = ManifestReader.Resolve(dir, files is null ? null : ManifestReader.Str(files, "thumbnail"), "thumbnail", required: false);
        var srt = ManifestReader.Resolve(dir, files is null ? null : ManifestReader.Str(files, "srt"), "srt", required: false);
        var seconds = ManifestReader.Num(meta, "durationSec");
        var (language, tags, caption) = ReadSeo(dir);
        return new IngestRequest(video, title, sourceId, id, id, caption, language, tags, DurationMs: seconds is > 0 ? (long)Math.Round(seconds.Value * 1000) : null,
            ThumbnailPath: thumb, SubtitlePath: srt, RequireStable: true);
    }

    private static (string? Language, IReadOnlyList<string> Tags, string? Caption) ReadSeo(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "script.json");
            if (!File.Exists(path)) return (null, [], null);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (JsonNode.Parse(stream) is not JsonObject { } root || root["seo"] is not JsonObject seo) return (null, [], null);
            var tags = seo["tags"] is JsonArray a ? a.Select(t => t?.GetValue<string>()).OfType<string>().ToList() : [];
            return (ManifestReader.Str(seo, "language"), tags, ManifestReader.Str(seo, "description"));
        }
        // script.json chỉ là thông tin phụ: lỗi đọc không được chặn nhập.
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { return (null, [], null); }
    }
}

public static class LooseFileReader
{
    /// <param name="sourceRoot">Có root thì source_content_id = đường dẫn tương đối của video so với root.</param>
    public static IngestRequest Build(string videoPath, string? sourceId, string? sourceRoot, bool requireStable = true)
    {
        if (!File.Exists(videoPath)) throw new IngestException("Không tìm thấy file video.");
        if (!MediaFiles.IsVideo(videoPath)) throw new IngestException("Định dạng video không được hỗ trợ.");
        var full = PathRules.Normalize(videoPath);
        string? contentId = null;
        if (sourceId is not null && sourceRoot is not null)
            contentId = Path.GetRelativePath(PathRules.Normalize(sourceRoot), full).Replace('\\', '/');
        var dir = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);
        return new IngestRequest(full, stem, sourceId, contentId, ThumbnailPath: Sidecar(dir, stem, MediaFiles.ThumbnailExtensions),
            SubtitlePath: Sidecar(dir, stem, MediaFiles.SubtitleExtensions), RequireStable: requireStable);
    }

    private static string? Sidecar(string dir, string stem, string[] extensions) => extensions.Select(e => Path.Combine(dir, stem + e)).FirstOrDefault(File.Exists);
}
