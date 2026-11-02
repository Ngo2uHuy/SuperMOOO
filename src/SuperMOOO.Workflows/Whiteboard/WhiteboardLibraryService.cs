using SuperMOOO.Workflows.Projects;

namespace SuperMOOO.Workflows.Whiteboard;

public sealed record WhiteboardLibraryItem(string Dir, string Title, string Topic, string VideoPath, string? ThumbnailPath, string? SeoPath, string? SrtPath,
    DateTimeOffset CreatedAt, long SizeBytes, double DurationSec, string Aspect)
{
    public double SizeMb => SizeBytes / 1048576.0;
}

/// <summary>Thư viện video vẽ tay đã xuất: quét meta.json trong thư mục xuất (như library.ts của VideoC); xoá chuyển cả thư mục vào Thùng rác.</summary>
[Service]
public sealed class WhiteboardLibraryService(ProjectIndex projects)
{
    public string DefaultFolder() => Path.Combine(projects.OutputRoot(), "Whiteboard");

    public async Task<IReadOnlyList<WhiteboardLibraryItem>> ListAsync(string? folder, CancellationToken ct = default)
    {
        var root = string.IsNullOrWhiteSpace(folder) ? DefaultFolder() : folder;
        var items = new List<WhiteboardLibraryItem>();
        if (!Directory.Exists(root)) return items;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var metaFile = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaFile)) continue;      // thư mục của job đang chạy hoặc lỗi chưa có meta
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metaFile, ct));
                var meta = doc.RootElement;
                var files = meta.GetProperty("files");
                string? File_(string key) => files.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } name
                    ? Path.Combine(dir, Path.GetFileName(name)) : null;
                var video = File_("video");
                if (video is null || !File.Exists(video)) continue;
                // createdAt: mili giây Unix (như VideoC) hoặc chuỗi ISO của bản cũ.
                var created = meta.TryGetProperty("createdAt", out var c)
                    && (c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                        : c.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(c.GetString(), CultureInfo.InvariantCulture, out var t) ? t : (DateTimeOffset?)null) is { } parsed
                    ? parsed : File.GetLastWriteTimeUtc(metaFile);
                items.Add(new WhiteboardLibraryItem(dir, meta.TryGetProperty("title", out var title) ? title.GetString() ?? "" : Path.GetFileName(dir),
                    meta.TryGetProperty("topic", out var topic) ? topic.GetString() ?? "" : "", video,
                    File_("thumbnail") is { } th && File.Exists(th) ? th : null, File_("seo") is { } seo && File.Exists(seo) ? seo : null,
                    File_("srt") is { } srt && File.Exists(srt) ? srt : null, created, new FileInfo(video).Length,
                    meta.TryGetProperty("durationSec", out var d) && d.TryGetDouble(out var sec) ? sec : 0,
                    meta.TryGetProperty("aspect", out var a) ? a.GetString() ?? "16:9" : "16:9"));
            }
            catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException or InvalidOperationException)
            {
                // meta hỏng thì bỏ qua thư mục đó
            }
        }

        return [.. items.OrderByDescending(i => i.CreatedAt)];
    }

    public void Delete(WhiteboardLibraryItem item)
    {
        if (!Directory.Exists(item.Dir)) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Dir, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }
}
