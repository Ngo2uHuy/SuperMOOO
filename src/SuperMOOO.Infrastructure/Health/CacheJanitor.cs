using SuperMOOO.Infrastructure.Shell;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace SuperMOOO.Infrastructure.Health;

public sealed record CacheSweepResult(int FilesDeleted, long BytesFreed);

/// <summary>Dọn cache\: quá TTL hoặc vượt trần dung lượng (xoá file cũ nhất trước); chạy khi khởi động và theo lịch.</summary>
[Service]
public sealed class CacheJanitor(AppPaths paths, ISettingsStore settings, IContentRepository contents, ILogger<CacheJanitor> log) : IHostedService
{
    // File mới sửa chưa lâu có thể đang được tác vụ dùng; chỉ dọn file đã nguội.
    private static readonly TimeSpan MinAge = TimeSpan.FromHours(1);
    private const string JobsScope = "jobs";

    public IReadOnlyList<string> Scopes =>
        Directory.Exists(paths.Cache) ? Directory.EnumerateDirectories(paths.Cache).Select(d => Path.GetFileName(d)!).Order().ToList() : [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try { await SweepAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Dọn cache khi khởi động lỗi"); }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Đường dẫn media của nội dung còn bài chờ đăng: video đăng bài có thể nằm trong cache\, không được dọn. Không đọc được kho thì không dọn gì.</summary>
    private async Task<IReadOnlySet<string>?> PendingAsync(CancellationToken ct)
    {
        try { return await contents.ListPendingMediaPathsAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Không đọc được danh sách file còn bài chờ đăng, bỏ qua lượt dọn cache");
            return null;
        }
    }

    public async Task<CacheSweepResult> SweepAsync(CancellationToken ct = default)
    {
        if (await PendingAsync(ct) is not { } pending) return new CacheSweepResult(0, 0);
        return await Task.Run(() => Sweep(pending, ct), ct);
    }

    private CacheSweepResult Sweep(IReadOnlySet<string> pending, CancellationToken ct)
    {
        var cfg = settings.Get<CacheSettings>("cache");
        var now = DateTime.UtcNow;
        var files = ListFiles(paths.Cache).Where(f => now - f.LastWriteTimeUtc > MinAge && !pending.Contains(f.FullName.ToLowerInvariant())).OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = ListFiles(paths.Cache).Sum(f => f.Length);
        var cap = cfg.MaxMb * 1024L * 1024L;

        var deleted = 0;
        long freed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var expired = now - file.LastWriteTimeUtc > TimeSpan.FromDays(cfg.TtlDays);
            if (!expired && total - freed <= cap) break;
            if (TryDelete(file) is { } size) { deleted++; freed += size; }
        }

        RemoveEmptyDirectories(paths.Cache);
        return new CacheSweepResult(deleted, freed);
    }

    /// <summary>Xoá hết file trong một nhóm cache ("tts", "preview", …) hoặc "all" (trừ thư mục tác vụ đang chạy).</summary>
    public async Task<CacheSweepResult> ClearAsync(string scope, CancellationToken ct = default)
    {
        if (await PendingAsync(ct) is not { } pending) return new CacheSweepResult(0, 0);
        return await Task.Run(() => Clear(scope, pending, ct), ct);
    }

    private CacheSweepResult Clear(string scope, IReadOnlySet<string> pending, CancellationToken ct)
    {
        var folders = scope.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? Scopes.Where(s => s != JobsScope).Select(s => SafePath.Combine(paths.Cache, s)).ToList()
            : [SafePath.Combine(paths.Cache, scope)];

        var deleted = 0;
        long freed = 0;
        foreach (var file in folders.SelectMany(ListFiles))
        {
            ct.ThrowIfCancellationRequested();
            if (pending.Contains(file.FullName.ToLowerInvariant())) continue;
            if (TryDelete(file) is { } size) { deleted++; freed += size; }
        }

        return new CacheSweepResult(deleted, freed);
    }

    private static List<FileInfo> ListFiles(string folder) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList() : [];

    private static long? TryDelete(FileInfo file)
    {
        try
        {
            var size = file.Length;
            file.Delete();
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // đang bị giữ: lần dọn sau xử lý tiếp
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        // Giữ nguyên các thư mục nhóm cấp 1 (tts, preview, …) vì nơi ghi cache không tự tạo lại chúng.
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .Where(d => !string.Equals(Path.GetDirectoryName(d), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* bỏ qua */ }
        }
    }
}

[ScheduledJob("0 0 */6 ? * *")]
public sealed class CacheJanitorJob(CacheJanitor janitor) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => await janitor.SweepAsync(cancellationToken);
}
