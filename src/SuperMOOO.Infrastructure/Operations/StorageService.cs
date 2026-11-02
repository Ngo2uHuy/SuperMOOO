using Microsoft.Extensions.Logging;
using Quartz;

namespace SuperMOOO.Infrastructure.Operations;

public sealed record FolderUsage(string Name, string Path, long Bytes);

public sealed record LogCleanResult(int FilesDeleted, long BytesFreed);

/// <summary>Dung lượng từng thư mục trong DataRoot và dọn log theo số ngày giữ. Dọn cache dùng CacheJanitor (không đụng file còn bài chờ đăng).</summary>
[Service]
public sealed class StorageService(AppPaths paths, ISettingsStore settings, ILogger<StorageService> log)
{
    public Task<IReadOnlyList<FolderUsage>> MeasureAsync(CancellationToken ct = default) => Task.Run<IReadOnlyList<FolderUsage>>(() =>
    {
        var result = new List<FolderUsage>();
        var db = new[] { paths.Database, paths.Database + "-wal", paths.Database + "-shm" }.Select(f => new FileInfo(f)).Where(f => f.Exists).Sum(f => f.Length);
        result.Add(new FolderUsage("aivs.db", paths.Database, db));
        foreach (var dir in Directory.EnumerateDirectories(paths.DataRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new FolderUsage(Path.GetFileName(dir), dir, DirectorySize(dir)));
        }
        return result;
    }, ct);

    private static long DirectorySize(string directory)
    {
        long sum = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
        {
            try { sum += file.Length; }
            catch (IOException) { }
        }
        return sum;
    }

    /// <summary>Xoá file *.log cũ hơn số ngày giữ (StorageSettings.LogKeepDays; 0 = không dọn). File đang ghi mới sửa gần đây nên không bị chạm.</summary>
    public LogCleanResult CleanLogs()
    {
        var days = settings.Get<StorageSettings>("storage").LogKeepDays;
        if (days <= 0 || !Directory.Exists(paths.Logs)) return new LogCleanResult(0, 0);

        var cutoff = DateTime.UtcNow.AddDays(-days);
        var deleted = 0;
        long freed = 0;
        foreach (var file in new DirectoryInfo(paths.Logs).EnumerateFiles("*.log").Where(f => f.LastWriteTimeUtc < cutoff))
        {
            try
            {
                var size = file.Length;
                file.Delete();
                freed += size;
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Không xoá được log {File}: {Reason}", file.Name, e.Message);
            }
        }
        if (deleted > 0) log.LogInformation("Dọn log: xoá {Count} file cũ hơn {Days} ngày, giải phóng {Bytes} byte", deleted, days, freed);
        return new LogCleanResult(deleted, freed);
    }
}

[ScheduledJob("0 15 3 * * ?")]
public sealed class LogCleanupJob(StorageService storage) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        storage.CleanLogs();
        return ValueTask.CompletedTask;
    }
}
