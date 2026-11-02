using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Infrastructure.Health;

/// <summary>Gom ICapabilityReporter của mọi project, thống kê máy và kiểm tra nhanh các điều kiện chạy.</summary>
[Service]
public sealed class SystemHealthService(IEnumerable<ICapabilityReporter> reporters, IDbConnectionFactory db, AppPaths paths,
    ILogger<SystemHealthService> log)
{
    private const long MinFreeMb = 1024;

    public async Task<IReadOnlyList<CapabilityReport>> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        var reports = new List<CapabilityReport>();
        foreach (var reporter in reporters.OrderBy(r => r.Area))
        {
            try
            {
                reports.Add(await reporter.ReportAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Reporter {Area} lỗi", reporter.Area);
                reports.Add(new CapabilityReport(reporter.Area, [new CapabilityItem("Báo cáo", false, ex.Message)]));
            }
        }

        return reports;
    }

    public Task<SystemStats> GetStatsAsync(CancellationToken ct = default)
    {
        using var process = Process.GetCurrentProcess();
        var root = Path.GetPathRoot(paths.DataRoot)!;
        return Task.FromResult(new SystemStats(Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
            process.WorkingSet64 / (1024 * 1024),
            new DriveInfo(root).AvailableFreeSpace / (1024 * 1024),
            RuntimeInformation.OSDescription));
    }

    public async Task<HealthReport> CheckAsync(CancellationToken ct = default)
    {
        var items = new List<CapabilityItem> { await CheckDatabaseAsync(), CheckDisk() };
        foreach (var report in await GetCapabilitiesAsync(ct))
            items.AddRange(report.Items.Select(i => i with { Name = $"{report.Area}: {i.Name}" }));
        return new HealthReport(items);
    }

    private async Task<CapabilityItem> CheckDatabaseAsync()
    {
        try
        {
            await using var conn = db.Open();
            var version = await conn.ExecuteScalarAsync<int>("PRAGMA user_version");
            return new CapabilityItem("Cơ sở dữ liệu", true, $"schema v{version}");
        }
        catch (Exception ex) when (ex is DbException or IOException)
        {
            return new CapabilityItem("Cơ sở dữ liệu", false, ex.Message);
        }
    }

    private CapabilityItem CheckDisk()
    {
        var free = new DriveInfo(Path.GetPathRoot(paths.DataRoot)!).AvailableFreeSpace / (1024 * 1024);
        return new CapabilityItem("Dung lượng đĩa dữ liệu", free >= MinFreeMb, $"{free:N0} MB trống");
    }
}
