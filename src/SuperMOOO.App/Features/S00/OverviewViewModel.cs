using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Infrastructure.Health;
using SuperMOOO.Infrastructure.Operations;

namespace SuperMOOO.App.Features.S00;

public sealed record UsageBar(string Name, double Percent, string Detail, bool Warn);
public sealed record HealthLine(string Name, bool Ok, string Detail);

[Service(ServiceLifetime.Transient)]
public sealed partial class OverviewViewModel(IJobQueue jobs, IRuntimeResources resources, DiskMonitor disks, SystemHealthService health,
    IActionRequiredRepository actions) : ObservableObject
{
    private const double Gb = 1024d * 1024 * 1024;
    private static readonly ToolKind[] RequiredTools = [ToolKind.Ffmpeg, ToolKind.Ffprobe, ToolKind.YtDlp];

    public ObservableCollection<JobRow> RecentJobs { get; } = [];

    [ObservableProperty]
    public partial int Running { get; set; }

    [ObservableProperty]
    public partial int Queued { get; set; }

    [ObservableProperty]
    public partial int SucceededToday { get; set; }

    [ObservableProperty]
    public partial int FailedToday { get; set; }

    [ObservableProperty]
    public partial int OpenActions { get; set; }

    [ObservableProperty]
    public partial string Machine { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<UsageBar> Usage { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<HealthLine> Components { get; set; } = [];

    [ObservableProperty]
    public partial string HealthSummary { get; set; } = "Đang kiểm tra…";

    [ObservableProperty]
    public partial bool HealthOk { get; set; } = true;

    public async Task LoadAsync()
    {
        RefreshUsage();
        await RefreshJobsAsync();
        await RefreshHealthAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    public async Task RefreshJobsAsync()
    {
        var active = await jobs.ListAsync(new JobQuery(Statuses: [JobStatus.Running, JobStatus.Queued], Limit: 500));
        Running = active.Count(j => j.Status == JobStatus.Running);
        Queued = active.Count - Running;

        var today = new DateTimeOffset(DateTime.Today);
        var finished = await jobs.ListAsync(new JobQuery(Statuses: [JobStatus.Succeeded, JobStatus.Failed, JobStatus.WaitingManualRetry], Limit: 500));
        var finishedToday = finished.Where(j => (j.FinishedAt ?? j.CreatedAt) >= today).ToList();
        SucceededToday = finishedToday.Count(j => j.Status == JobStatus.Succeeded);
        FailedToday = finishedToday.Count - SucceededToday;

        var recent = await jobs.ListAsync(new JobQuery(Limit: 8));
        RecentJobs.Clear();
        foreach (var job in recent) RecentJobs.Add(new JobRow(job));

        OpenActions = await actions.CountOpenAsync(CancellationToken.None);
    }

    public void RefreshUsage()
    {
        var machine = resources.Machine;
        var load = resources.Load;
        var cores = machine.PhysicalCores > 0 ? machine.PhysicalCores : machine.LogicalProcessors;
        Machine = $"{machine.CpuName.Trim()} · {cores} nhân · RAM {machine.TotalMemoryBytes / Gb:0} GB";

        var busy = load.Pressure != ResourcePressure.Normal;
        var usedRam = Math.Max(0, machine.TotalMemoryBytes - load.AvailableMemoryBytes);
        List<UsageBar> bars =
        [
            new("CPU", load.CpuPercent, $"{load.CpuPercent:0}%", busy),
            new("RAM", Percent(usedRam, machine.TotalMemoryBytes), $"{usedRam / Gb:0.0} / {machine.TotalMemoryBytes / Gb:0.0} GB", busy)
        ];
        foreach (var disk in disks.Read())
            bars.Add(new($"Ổ {disk.Root.TrimEnd('\\')} ({disk.Label})", Percent(disk.TotalBytes - disk.FreeBytes, disk.TotalBytes),
                $"còn {disk.FreeBytes / Gb:0.0} GB", disk.Low));
        Usage = bars;
    }

    private async Task RefreshHealthAsync()
    {
        var items = (await health.CheckAsync()).Items;
        var tools = items.Where(i => i.Name.StartsWith("Công cụ: ", StringComparison.Ordinal)).ToList();
        var missingTools = tools.Where(i => !i.Available).Select(i => i.Name["Công cụ: ".Length..]).ToList();
        var tts = items.Where(i => i.Name.StartsWith("AI: TTS local", StringComparison.Ordinal)).ToList();
        var keys = items.Count(i => i.Name.StartsWith("AI: API key", StringComparison.Ordinal) && i.Available);

        // API key và model tuỳ chọn thiếu là bình thường; chỉ thành phần bắt buộc mới tính là "cần kiểm tra".
        List<HealthLine> lines = [.. items.Where(i => !i.Name.Contains(':')).Select(i => new HealthLine(i.Name, i.Available, i.Detail ?? ""))];
        lines.Add(new("Công cụ (ffmpeg, yt-dlp…)", !missingTools.Any(t => RequiredTools.Any(r => r.ToString() == t)),
            missingTools.Count == 0 ? $"Đủ {tools.Count} công cụ" : "Thiếu " + string.Join(", ", missingTools)));
        lines.Add(new("Giọng đọc local", tts.Where(i => !i.Name.Contains("(tuỳ chọn)")).All(i => i.Available),
            $"{tts.Count(i => i.Available)}/{tts.Count} thành phần đã cài"));
        lines.Add(new("API key", keys > 0, keys > 0 ? $"{keys} nhà cung cấp đã nhập" : "Chưa nhập key nào (chỉ cần khi dùng AI cloud)"));
        Components = lines;

        var problems = lines.Count(l => !l.Ok);
        HealthOk = problems == 0;
        HealthSummary = HealthOk ? "Hệ thống sẵn sàng" : $"{problems} mục cần kiểm tra";
    }

    private static double Percent(long part, long total) => total <= 0 ? 0 : Math.Clamp(part * 100d / total, 0, 100);
}
