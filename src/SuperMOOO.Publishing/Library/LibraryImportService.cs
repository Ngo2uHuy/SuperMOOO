using Microsoft.Extensions.Logging;
using Quartz;

namespace SuperMOOO.Publishing.Library;

public sealed record ImportTarget(ImportMode Mode, string Path);

public sealed record ImportSummary(int Added, int Duplicate, int NotReady, int Failed, IReadOnlyList<string> Errors)
{
    public static ImportSummary Empty { get; } = new(0, 0, 0, 0, []);
    public string Describe() => $"Thêm {Added}, trùng {Duplicate}" + (NotReady > 0 ? $", chờ ổn định {NotReady}" : "") + (Failed > 0 ? $", lỗi {Failed}" : "");
}

/// <summary>
/// Nhập từ nguồn ngoài vào kho qua <see cref="ContentIngestService"/>: thư mục theo dõi (nhiều thư mục, quét định kỳ, ưu tiên manifest.json,
/// rồi meta.json của VideoC, rồi file rời) và nhập tay (file/thư mục). Không dùng FileSystemWatcher: quét thưa + kiểm tra file đã nguội
/// rẻ hơn, không mất sự kiện khi tràn buffer hay ổ mạng, và không giữ handle thư mục.
/// </summary>
[Service]
public sealed class LibraryImportService(ContentIngestService ingest, ISourceRepository sources, IFingerprintStore fingerprints,
    IActionRequiredRepository actions, IPublishSettingsStore settings, ILogger<LibraryImportService> log)
{
    private const int MaxPerSweep = 200;
    private const int MaxFolderFiles = 5000;
    private static readonly EnumerationOptions Safe = new() { IgnoreInaccessible = true, RecurseSubdirectories = false };

    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private readonly HashSet<string> _done = [];
    private readonly Dictionary<string, string> _failed = [];

    /// <summary>Quét định kỳ: tạm dừng theo "automation_paused" (quét tay ở S21 vẫn chạy).</summary>
    public async Task<ImportSummary> SweepAllAsync(CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.AutomationPaused, ct) == "true") return ImportSummary.Empty;
        var total = ImportSummary.Empty;
        foreach (var s in await sources.ListAsync(ct))
            if (s.Enabled && s.WatchEnabled && !LibrarySources.IsSystem(s.Id)) total = Add(total, await SweepAsync(s, ct));
        return total;
    }

    public async Task<ImportSummary> SweepAsync(ContentSource source, CancellationToken ct)
    {
        await _sweepGate.WaitAsync(ct);
        try { return await SweepCoreAsync(source, ct); }
        finally { _sweepGate.Release(); }
    }

    private async Task<ImportSummary> SweepCoreAsync(ContentSource source, CancellationToken ct)
    {
        if (!Directory.Exists(source.RootPath))
        {
            await actions.RaiseAsync(ActionKinds.SourceUnavailable, source.Id, $"Nguồn \"{source.Name}\" không truy cập được", "Không tìm thấy thư mục: " + source.RootPath, ct);
            return ImportSummary.Empty;
        }
        await actions.ResolveByRefAsync(ActionKinds.SourceUnavailable, source.Id, ct);

        var summary = ImportSummary.Empty;
        var handled = 0;
        try
        {
            foreach (var target in Enumerate(source))
            {
                ct.ThrowIfCancellationRequested();
                var key = $"{source.Id}|{target.Mode}|{PathRules.Key(target.Path)}|{Stamp(target)}";
                if (_done.Contains(key) || _failed.ContainsKey(key)) continue;
                if (target.Mode == ImportMode.LooseFiles && await IsUnchangedAsync(target.Path, ct)) { _done.Add(key); continue; }
                summary = Add(summary, await ImportTargetAsync(target, source, key, ct));
                if (++handled >= MaxPerSweep) break;
            }
        }
        // Ổ rời bị tháo giữa lúc quét: coi như nguồn không truy cập được, lượt sau thử lại.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await actions.RaiseAsync(ActionKinds.SourceUnavailable, source.Id, $"Nguồn \"{source.Name}\" không truy cập được", e.Message, ct);
        }
        if (_done.Count > 20_000) _done.Clear();
        return summary;
    }

    /// <summary>Nhập file người dùng chọn (S14 "Chọn file…", S21): không cần chờ ổn định, vẫn ưu tiên manifest cạnh file nếu mô tả đúng file đó.</summary>
    public async Task<ImportSummary> ImportFilesAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var summary = ImportSummary.Empty;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            summary = Add(summary, await ImportOneAsync(() => ManifestReader.TryReadPreferred(Path.GetDirectoryName(PathRules.Normalize(path)), LibrarySources.Manual, path)
                ?? LooseFileReader.Build(path, LibrarySources.Manual, null, requireStable: false), path, ct));
        }
        return summary;
    }

    /// <summary>Nhập mọi video trong thư mục một lần (không đăng ký theo dõi).</summary>
    public async Task<ImportSummary> ImportFolderAsync(string root, bool recursive, CancellationToken ct)
    {
        if (!Directory.Exists(root)) throw new IngestException("Không tìm thấy thư mục: " + root);
        return await ImportFilesAsync(EnumerateVideos(root, recursive, skipWork: true).Take(MaxFolderFiles), ct);
    }

    private async Task<ImportSummary> ImportTargetAsync(ImportTarget target, ContentSource source, string key, CancellationToken ct)
    {
        var ai = source.Kind is SourceKind.AIVideoStudio or SourceKind.VideoC;
        var summary = await ImportOneAsync(() =>
        {
            var request = target.Mode switch
            {
                ImportMode.Manifest => ManifestReader.Read(target.Path, source.Id),
                ImportMode.VideoCMeta => ManifestReader.TryReadPreferred(target.Path, source.Id) ?? VideoCMetaReader.Read(target.Path, source.Id),
                _ => ManifestReader.TryReadPreferred(Path.GetDirectoryName(target.Path), source.Id, target.Path)
                     ?? LooseFileReader.Build(target.Path, source.Id, source.RootPath)
            };
            return request with { IsAiGenerated = ai || request.IsAiGenerated, RequireStable = true };
        }, target.Path, ct, onFailed: message => _failed[key] = message);
        if (summary.NotReady == 0 && summary.Failed == 0) _done.Add(key);
        return summary;
    }

    private async Task<ImportSummary> ImportOneAsync(Func<IngestRequest> build, string path, CancellationToken ct, Action<string>? onFailed = null)
    {
        try
        {
            var result = await ingest.IngestAsync(build(), ct);
            await actions.ResolveByRefAsync(ActionKinds.ImportFailed, PathRules.Key(path), ct);
            return result.Duplicate ? ImportSummary.Empty with { Duplicate = 1 } : ImportSummary.Empty with { Added = 1 };
        }
        catch (IngestNotReadyException) { return ImportSummary.Empty with { NotReady = 1 }; }
        catch (Exception e) when (e is IngestException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Không nhập được {Path}: {Reason}", path, e.Message);
            onFailed?.Invoke(e.Message);
            await actions.RaiseAsync(ActionKinds.ImportFailed, PathRules.Key(path), "Không nhập được video vào kho", $"{path}: {e.Message}", ct);
            return ImportSummary.Empty with { Failed = 1, Errors = [$"{Path.GetFileName(path)}: {e.Message}"] };
        }
    }

    private async Task<bool> IsUnchangedAsync(string path, CancellationToken ct)
    {
        var fp = await fingerprints.GetAsync(PathRules.Normalize(path), ct);
        if (fp is null) return false;
        var info = new FileInfo(path);
        return info.Exists && info.Length == fp.Value.SizeBytes && info.LastWriteTimeUtc == fp.Value.LastWriteUtc.UtcDateTime;
    }

    // Dấu thay đổi của mục: file đổi (hoặc manifest/meta ghi lại) thì được thử lại dù từng lỗi.
    private static string Stamp(ImportTarget t)
    {
        var file = t.Mode switch
        {
            ImportMode.Manifest => Path.Combine(t.Path, ManifestReader.FileName),
            ImportMode.VideoCMeta => Path.Combine(t.Path, VideoCMetaReader.MetaFileName),
            _ => t.Path
        };
        var info = new FileInfo(file);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
    }

    private static ImportSummary Add(ImportSummary a, ImportSummary b) =>
        new(a.Added + b.Added, a.Duplicate + b.Duplicate, a.NotReady + b.NotReady, a.Failed + b.Failed, [.. a.Errors, .. b.Errors]);

    private static IEnumerable<ImportTarget> Enumerate(ContentSource source) => source.ImportMode switch
    {
        ImportMode.Manifest => EnumerateMarked(source.RootPath, source.Recursive, ManifestReader.FileName, ImportMode.Manifest, _ => true),
        ImportMode.VideoCMeta => EnumerateMarked(source.RootPath, false, VideoCMetaReader.MetaFileName, ImportMode.VideoCMeta, VideoCMetaReader.IsComplete),
        _ => EnumerateVideos(source.RootPath, source.Recursive, source.Kind == SourceKind.AIVideoStudio).Select(f => new ImportTarget(ImportMode.LooseFiles, f))
    };

    private static IEnumerable<ImportTarget> EnumerateMarked(string root, bool recursive, string marker, ImportMode mode, Func<string, bool> ready)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var isRoot = dir == root;
            if (!isRoot && File.Exists(Path.Combine(dir, marker)) && ready(dir)) yield return new ImportTarget(mode, dir);
            if (isRoot || recursive)
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", Safe)) stack.Push(sub);
        }
    }

    private static IEnumerable<string> EnumerateVideos(string root, bool recursive, bool skipWork)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, "*", Safe))
                if (MediaFiles.IsVideo(file)) yield return file;
            if (!recursive) continue;
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", Safe))
                if (!(skipWork && Path.GetFileName(sub).Contains("work", StringComparison.OrdinalIgnoreCase))) stack.Push(sub);
        }
    }
}

/// <summary>Quét thư mục theo dõi mỗi phút; chỉ tốn I/O liệt kê, hash chỉ khi gặp file mới.</summary>
[ScheduledJob("20 * * * * ?")]
[DisallowConcurrentExecution]
public sealed class LibrarySweepJob(LibraryImportService library, ILogger<LibrarySweepJob> log) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var summary = await library.SweepAllAsync(cancellationToken);
        if (summary.Added > 0) log.LogInformation("Thư mục theo dõi: {Summary}", summary.Describe());
    }
}
