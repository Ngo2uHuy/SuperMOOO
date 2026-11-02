using System.Globalization;
using SuperMOOO.Core.Publishing;
using Microsoft.Extensions.Logging;
using Quartz;

namespace SuperMOOO.Infrastructure.Operations;

public sealed record DiskStatus(string Root, string Label, long FreeBytes, long TotalBytes, bool Low);

/// <summary>
/// Theo dõi dung lượng trống của ổ chứa DataRoot và thư mục xuất. Dưới ngưỡng cảnh báo (StorageSettings.WarnFreeGb) →
/// mục Cần xử lý + sự kiện <see cref="LowSpace"/> (toast ở tầng App); đủ chỗ lại thì tự đóng mục. Chặn job: xem <see cref="MediaDiskPreRunCheck"/>.
/// </summary>
[Service]
public sealed class DiskMonitor(AppPaths paths, ISettingsStore settings, IPublishSettingsStore publishSettings, BackupService backups,
    IActionRequiredRepository actions, ILogger<DiskMonitor> log)
{
    public const string ActionKind = "DiskLow";
    public const long DefaultMinFreeMb = 1024;

    private readonly HashSet<string> _low = [];
    private readonly object _gate = new();

    /// <summary>Phát khi một ổ vừa xuống dưới ngưỡng cảnh báo (không lặp lại cho tới khi ổ đủ chỗ trở lại).</summary>
    public event Action<DiskStatus>? LowSpace;

    public long WarnBytes => Math.Max(0, settings.Get<StorageSettings>("storage").WarnFreeGb) * 1024L * 1024 * 1024;

    public async Task<long> MinFreeBytesAsync(CancellationToken ct) =>
        (long.TryParse(await publishSettings.GetAsync(SettingKeys.DiskMinFreeMb, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var mb)
            ? mb : DefaultMinFreeMb) * 1024 * 1024;

    /// <summary>Các ổ cần theo dõi (DataRoot, thư mục xuất), bỏ trùng ổ.</summary>
    public IReadOnlyList<DiskStatus> Read()
    {
        var warn = WarnBytes;
        var result = new List<DiskStatus>();
        foreach (var (path, label) in new[] { (paths.DataRoot, "Dữ liệu app"), (backups.OutputRoot(), "Thư mục xuất") })
        {
            if (Path.GetPathRoot(Path.GetFullPath(path)) is not { Length: > 0 } root) continue;
            var existing = result.FirstOrDefault(r => string.Equals(r.Root, root, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                result[result.IndexOf(existing)] = existing with { Label = existing.Label + " + " + label.ToLowerInvariant() };
                continue;
            }
            try
            {
                var drive = new DriveInfo(root);
                result.Add(new DiskStatus(root, label, drive.AvailableFreeSpace, drive.TotalSize, drive.AvailableFreeSpace < warn));
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // Không đọc được ổ (vd UNC, ổ rời đã rút): không cảnh báo, không chặn việc gì.
            }
        }
        return result;
    }

    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        foreach (var disk in Read())
        {
            bool raise, resolve;
            lock (_gate)
            {
                raise = disk.Low && _low.Add(disk.Root);
                resolve = !disk.Low && _low.Remove(disk.Root);
            }
            if (raise)
            {
                log.LogWarning("Ổ {Root} còn {FreeMb} MB (ngưỡng cảnh báo {WarnMb} MB)", disk.Root, disk.FreeBytes >> 20, WarnBytes >> 20);
                await actions.RaiseAsync(ActionKind, disk.Root, $"Ổ {disk.Root} sắp đầy",
                    $"{disk.Label}: còn {disk.FreeBytes >> 20} MB trống (ngưỡng cảnh báo {WarnBytes >> 20} MB). Dọn dung lượng ở Cài đặt > Sao lưu & lưu trữ.", ct);
                LowSpace?.Invoke(disk);
            }
            else if (resolve)
            {
                await actions.ResolveByRefAsync(ActionKind, disk.Root, ct);
            }
        }
    }

    /// <summary>Ổ nào (DataRoot/thư mục xuất) dưới ngưỡng tối thiểu → mô tả để dời job; null nếu đủ chỗ.</summary>
    public async Task<string?> BelowMinimumAsync(CancellationToken ct)
    {
        var min = await MinFreeBytesAsync(ct);
        if (min <= 0) return null;
        foreach (var disk in Read())
            if (disk.FreeBytes < min) return $"Ổ {disk.Root} còn {disk.FreeBytes >> 20} MB (tối thiểu {min >> 20} MB); tạm dừng tác vụ ghi file.";
        return null;
    }
}

/// <summary>Trước khi chạy job lane Media (ffmpeg ghi file lớn): ổ dưới ngưỡng tối thiểu → dời job, không tính lượt thử. Lane Publish do PublishPreRunCheck lo.</summary>
[Service(As = [typeof(IJobPreRunCheck)])]
public sealed class MediaDiskPreRunCheck(DiskMonitor disk, IClock clock) : IJobPreRunCheck
{
    public bool AppliesTo(string jobType) => true;

    public async Task<JobPreRunDecision?> CheckAsync(JobPreRunContext job, CancellationToken ct)
    {
        if (job.Lane != JobLane.Media) return null;
        return await disk.BelowMinimumAsync(ct) is { } reason ? new JobPreRunDecision(clock.UtcNow + TimeSpan.FromMinutes(10), reason) : null;
    }
}

[ScheduledJob("0 0/5 * * * ?")]
[DisallowConcurrentExecution]
public sealed class DiskMonitorJob(DiskMonitor monitor) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => await monitor.EvaluateAsync(cancellationToken);
}
