using SuperMOOO.Data.Repositories.Jobs;
using SuperMOOO.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace SuperMOOO.Infrastructure.Health;

/// <summary>Xoá lịch sử job đã kết thúc quá CacheSettings.JobHistoryDays (không đụng file output, thư viện, dự án); chạy khi khởi động và theo lịch.</summary>
[Service]
public sealed class JobHistoryJanitor(JobRepository jobs, DatabaseMaintenance maintenance, ISettingsStore settings, ILogger<JobHistoryJanitor> log)
    : IHostedService
{
    private const double CompactFreeRatio = 0.2;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => SweepAsync(), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var days = settings.Get<CacheSettings>("cache").JobHistoryDays;
        if (days <= 0 || !await _gate.WaitAsync(0, ct)) return 0;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            var deleted = 0;
            if (await jobs.AnyFinishedBeforeAsync(cutoff, ct))
            {
                // Lần đầu xoá trên DB chưa có bản sao lưu nào (migration đã sao lưu thì thôi).
                if (!maintenance.HasBackup) log.LogInformation("Đã sao lưu DB trước khi dọn lịch sử tác vụ: {Path}", maintenance.Backup());
                deleted = await jobs.DeleteFinishedBeforeAsync(cutoff, ct: ct);
                if (deleted > 0) log.LogInformation("Đã xoá {Count} tác vụ kết thúc trước {Days} ngày", deleted, days);
            }

            // VACUUM giữ khoá ghi; đang có job chạy thì để lần dọn sau, trang trống vẫn còn nên lần sau vẫn thu gọn.
            if ((await jobs.ListAsync(new SuperMOOO.Core.Jobs.JobQuery(Statuses: [JobStatus.Running], Limit: 1), ct)).Count == 0
                && maintenance.CompactIfFragmented(CompactFreeRatio))
                log.LogInformation("Đã thu gọn DB sau khi dọn lịch sử tác vụ");
            return deleted;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Dọn lịch sử tác vụ lỗi");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }
}

[ScheduledJob("0 30 */6 ? * *")]
public sealed class JobHistoryJanitorJob(JobHistoryJanitor janitor) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => await janitor.SweepAsync(cancellationToken);
}
