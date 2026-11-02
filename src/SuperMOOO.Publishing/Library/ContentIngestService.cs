using System.Security.Cryptography;
using System.Text.Json;
using SuperMOOO.Core.Models;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Publishing.Library;

/// <summary>Dữ liệu không hợp lệ của file/manifest nhập: không thử lại cho tới khi file đổi.</summary>
public sealed class IngestException(string message) : Exception(message);

/// <summary>Chưa sẵn sàng (đang ghi, thiếu meta.json…): bỏ qua lượt này, lượt quét sau thử lại, không báo lỗi.</summary>
public sealed class IngestNotReadyException(string message) : Exception(message);

public static class MediaFiles
{
    public static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".webm", ".m4v"];
    public static readonly string[] ThumbnailExtensions = [".jpg", ".png"];
    public static readonly string[] SubtitleExtensions = [".srt", ".vtt"];
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Nguồn hệ thống: pipeline của app và file người dùng thêm tay (nguồn ngoài là các <see cref="ContentSource"/> do người dùng cấu hình).</summary>
public static class LibrarySources
{
    public const string Pipeline = "sys-pipeline";
    public const string Manual = "sys-manual";

    public static bool IsSystem(string? id) => id is Pipeline or Manual;
}

/// <param name="SourceContentId">Định danh nội dung trong nguồn (contentId manifest, id meta.json, đường dẫn tương đối...): có thì kiểm trước khi hash.</param>
/// <param name="RequireStable">File từ thư mục theo dõi: chỉ nhập khi không còn ai ghi.</param>
public sealed record IngestRequest(
    string VideoPath, string Title, string? SourceId = null, string? SourceContentId = null, string? SourceJobId = null,
    string? Caption = null, string? Language = null, IReadOnlyList<string>? Tags = null,
    bool IsAiGenerated = false, bool IsReupload = false, string? RequestId = null,
    long? DurationMs = null, int? Width = null, int? Height = null,
    string? ThumbnailPath = null, string? SubtitlePath = null, string? ExpectedSha256 = null, bool RequireStable = false);

public sealed record IngestResult(string ContentId, bool Duplicate);

/// <summary>
/// Cổng duy nhất đưa video vào kho thành phẩm (pipeline, S14 "Chọn file…", DeliveryService, thư mục theo dõi, S21): sha256 tính một lần
/// (cache theo size + mtime), trùng thì dùng lại nội dung cũ và bổ sung metadata còn thiếu. Không chép file: kho trỏ thẳng đường dẫn gốc.
/// </summary>
[Service]
public sealed class ContentIngestService(IContentRepository contents, IFileHasher hasher, ISourceRepository sources, IMediaProbe probe, IClock clock,
    ILogger<ContentIngestService> log)
{
    private static readonly TimeSpan StableAge = TimeSpan.FromSeconds(10);

    // Hash file lớn tốn I/O: nhập tuần tự để watch folder + pipeline + S21 không giành đĩa với nhau (và không tạo hai bản cùng lúc).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _systemSources;

    public async Task<IngestResult> IngestAsync(IngestRequest r, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await IngestCoreAsync(r, ct); }
        finally { _gate.Release(); }
    }

    public async Task<IngestResult> IngestImagesAsync(IReadOnlyList<string> paths, string? title, CancellationToken ct)
    {
        if (paths.Count is < 1 or > 35) throw new IngestException("Chọn từ 1 đến 35 ảnh cho một bài đăng.");
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureSystemSourcesAsync(ct);
            var id = Guid.NewGuid().ToString("N");
            var assets = new List<MediaAsset>();
            foreach (var input in paths)
            {
                ct.ThrowIfCancellationRequested();
                var path = PathRules.Normalize(input);
                if (!MediaFiles.IsImage(path)) throw new IngestException("Chỉ nhận ảnh JPG, JPEG, PNG hoặc WebP.");
                if (!File.Exists(path)) throw new IngestException("Không tìm thấy ảnh: " + path);
                var hash = await hasher.HashAsync(path, ct);
                if (hash.SizeBytes <= 0) throw new IngestException("Ảnh rỗng: " + Path.GetFileName(path));
                // Repository sắp theo kind, id: suffix cố định giữ nguyên thứ tự carousel mà không đổi schema.
                assets.Add(new MediaAsset
                {
                    Id = $"{id}_{assets.Count:D3}", ContentItemId = id, Kind = MediaKind.Image,
                    StoredPath = path, OriginalPath = path, SizeBytes = hash.SizeBytes, Sha256 = hash.Sha256
                });
            }
            var signature = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                "images\n" + string.Join('\n', assets.Select(a => a.Sha256)))));
            if (await contents.FindBySha256Async(signature, ct) is { } existing) return new IngestResult(existing, true);
            var item = new FinishedContent
            {
                Id = id, SourceId = LibrarySources.Manual,
                Title = Cut(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(assets[0].StoredPath) : title.Trim(), 200),
                VideoSha256 = signature, OriginalPath = assets[0].StoredPath, ImportedUtc = clock.UtcNow
            };
            return await contents.AddAsync(item, assets, ct) is AddContentResult.Duplicate duplicate
                ? new IngestResult(duplicate.ExistingId, true) : new IngestResult(id, false);
        }
        finally { _gate.Release(); }
    }

    private async Task<IngestResult> IngestCoreAsync(IngestRequest r, CancellationToken ct)
    {
        var path = PathRules.Normalize(r.VideoPath);
        if (!File.Exists(path)) throw new IngestException("Không tìm thấy file video: " + path);
        if (!MediaFiles.IsVideo(path)) throw new IngestException("Định dạng video không được hỗ trợ: " + Path.GetExtension(path));
        await EnsureSystemSourcesAsync(ct);

        if (r.SourceId is not null && r.SourceContentId is not null
            && await contents.FindBySourceAsync(r.SourceId, r.SourceContentId, ct) is { } known)
            return await MergeAsync(known, r, ct);

        if (r.RequireStable) CheckStable(path);

        var hash = await hasher.HashAsync(path, ct);
        if (r.ExpectedSha256 is not null && !string.Equals(r.ExpectedSha256, hash.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IngestException("Checksum video không khớp manifest.");
        if (await contents.FindBySha256Async(hash.Sha256, ct) is { } same) return await MergeAsync(same, r, ct);

        var (durationMs, width, height) = (r.DurationMs, r.Width, r.Height);
        if (durationMs is null || width is null || height is null)
        {
            try
            {
                var info = await probe.ProbeAsync(path, ct);
                durationMs ??= info.Duration.TotalMilliseconds > 0 ? (long)info.Duration.TotalMilliseconds : null;
                if (info.Video is { } v)
                {
                    var swap = v.Rotation is 90 or 270 or -90 or -270;
                    width ??= swap ? v.Height : v.Width;
                    height ??= swap ? v.Width : v.Height;
                }
            }
            catch (MediaProbeException e) { log.LogWarning(e, "Không đọc được thông tin media: {Path}", path); }
        }

        var id = Guid.NewGuid().ToString("N");
        var item = new FinishedContent
        {
            Id = id, SourceId = r.SourceId, SourceContentId = r.SourceContentId, SourceJobId = r.SourceJobId,
            Title = Cut(string.IsNullOrWhiteSpace(r.Title) ? Path.GetFileNameWithoutExtension(path) : r.Title.Trim(), 200),
            Caption = string.IsNullOrWhiteSpace(r.Caption) ? null : r.Caption, Language = string.IsNullOrWhiteSpace(r.Language) ? null : r.Language,
            TagsJson = JsonSerializer.Serialize(r.Tags ?? []), DurationMs = durationMs, Width = width, Height = height, VideoSha256 = hash.Sha256,
            ImportedUtc = clock.UtcNow, OriginalPath = path, IsAiGenerated = r.IsAiGenerated, IsReupload = r.IsReupload, RequestId = r.RequestId
        };
        var assets = new List<MediaAsset> { Asset(id, MediaKind.Video, path, hash.SizeBytes, hash.Sha256) };
        if (r.ThumbnailPath is { } t && File.Exists(t)) assets.Add(Asset(id, MediaKind.Thumbnail, PathRules.Normalize(t), new FileInfo(t).Length, ""));
        if (r.SubtitlePath is { } s && File.Exists(s)) assets.Add(Asset(id, MediaKind.Subtitle, PathRules.Normalize(s), new FileInfo(s).Length, ""));
        return await contents.AddAsync(item, assets, ct) is AddContentResult.Duplicate dup ? await MergeAsync(dup.ExistingId, r, ct) : new IngestResult(id, false);
    }

    /// <summary>Nội dung đã có (vd Delivery ghi sơ sài trước khi pipeline xong): chỉ điền phần còn trống và bật cờ AI/reup, không ghi đè chữ người dùng đã sửa.</summary>
    private async Task<IngestResult> MergeAsync(string existingId, IngestRequest r, CancellationToken ct)
    {
        var detail = await contents.GetAsync(existingId, ct);
        if (detail is not null)
        {
            var c = detail.Item;
            var fillCaption = string.IsNullOrWhiteSpace(c.Caption) && !string.IsNullOrWhiteSpace(r.Caption);
            var fillLanguage = string.IsNullOrWhiteSpace(c.Language) && !string.IsNullOrWhiteSpace(r.Language);
            var fillTags = c.TagsJson is "[]" or "" && r.Tags is { Count: > 0 };
            var ai = r.IsAiGenerated && !c.IsAiGenerated;
            var reup = r.IsReupload && !c.IsReupload;
            if (fillCaption || fillLanguage || fillTags || ai || reup)
                await contents.UpdateMetadataAsync(existingId, new ContentMetadataUpdate(null, fillCaption ? r.Caption : null, fillLanguage ? r.Language : null,
                    fillTags ? JsonSerializer.Serialize(r.Tags) : null, ai ? true : null, reup ? true : null, null), ct);
        }
        return new IngestResult(existingId, true);
    }

    private static MediaAsset Asset(string contentId, MediaKind kind, string path, long size, string sha) => new()
    {
        Id = Guid.NewGuid().ToString("N"), ContentItemId = contentId, Kind = kind, StoredPath = path, SizeBytes = size, Sha256 = sha, OriginalPath = path
    };

    // Không còn ai giữ handle ghi và không sửa trong vài giây gần đây: không cần Task.Delay giữ luồng, lượt quét sau thử lại.
    private static void CheckStable(string path)
    {
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < StableAge) throw new IngestNotReadyException("File vừa được ghi, chờ ổn định.");
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IngestNotReadyException("File đang được ghi hoặc bị khoá.");
        }
    }

    private async Task EnsureSystemSourcesAsync(CancellationToken ct)
    {
        if (_systemSources) return;
        var now = clock.UtcNow;
        foreach (var (id, name, kind) in new[] { (LibrarySources.Pipeline, "Pipeline SuperMOOO", SourceKind.AIVideoStudio), (LibrarySources.Manual, "Thêm thủ công", SourceKind.Folder) })
            if (await sources.GetAsync(id, ct) is null)
                await sources.UpsertAsync(new ContentSource
                {
                    Id = id, Name = name, Kind = kind, RootPath = "", ImportMode = ImportMode.LooseFiles, Enabled = false, CreatedUtc = now, UpdatedUtc = now
                }, ct);
        _systemSources = true;
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;

    /// <summary>Khoá nguồn ổn định cho video pipeline: chạy lại cùng job không tạo bản trùng, khỏi hash lại.</summary>
    public static string PipelineContentId(string jobId, string videoFileName) =>
        "aivs_" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(jobId + "|" + videoFileName)))[..24];
}
