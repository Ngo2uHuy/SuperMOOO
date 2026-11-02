using System.Reflection;
using System.Windows.Threading;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Shell;

public sealed class ScreenUsageSettings
{
    public Dictionary<string, ScreenVisit> Visits { get; set; } = [];
}

public sealed record ScreenVisit(long Count, long LastVisited);

[Service]
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly HelpService _help;
    private readonly Dictionary<string, object> _views = [];

    private readonly IJobQueue _jobs;
    private bool _polling;
    private readonly ScreenRegistry _registry;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, ScreenVisit> _visits;
    private readonly SemaphoreSlim _usageSave = new(1, 1);
    private readonly bool _trackUsage = SmokeRunner.ParseArgs(Environment.GetCommandLineArgs()) is null;

    public MainViewModel(ScreenRegistry registry, IServiceProvider services, HelpService help, IJobQueue jobs,
        ISettingsStore settings, IDialogService dialogs)
    {
        _services = services;
        _help = help;
        _jobs = jobs;
        _registry = registry;
        _settings = settings;
        _dialogs = dialogs;
        _visits = (settings.Get<ScreenUsageSettings>("ui.screenUsage").Visits ?? [])
            .Where(x => x.Value is { Count: > 0 } && registry.Find(x.Key) is not null)
            .ToDictionary(x => x.Key, x => x.Value);
        RefreshFrequentScreens();
        Selected = registry.All[0];
        // Thanh trạng thái chỉ cần số liệu thô, poll thưa là đủ và không phụ thuộc màn nào đang mở.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await RefreshJobsAsync();
        timer.Start();
    }

    public string Version { get; } = "Phiên bản " + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    [ObservableProperty]
    public partial string JobSummary { get; set; } = "Sẵn sàng";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial ScreenDescriptor? Selected { get; set; }

    [ObservableProperty]
    public partial object? CurrentView { get; set; }

    public IReadOnlyList<ScreenDescriptor> FrequentScreens { get; private set; } = [];

    public void Select(ScreenDescriptor screen)
    {
        if (Selected?.Id == screen.Id) return;
        Selected = screen;
        // Màn mở tự động khi khởi động và lượt duyệt của smoke không phản ánh thói quen người dùng.
        if (!_trackUsage || screen.Id is "S15" or "S16") return;
        var count = _visits.GetValueOrDefault(screen.Id)?.Count ?? 0;
        _visits[screen.Id] = new ScreenVisit(count < long.MaxValue ? count + 1 : count, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        RefreshFrequentScreens();
        _ = SaveUsageAsync();
    }

    private void RefreshFrequentScreens()
    {
        var frequent = _registry.All.Where(s => s.Id is not ("S15" or "S16") && _visits.ContainsKey(s.Id))
            .OrderByDescending(s => _visits[s.Id].Count)
            .ThenByDescending(s => _visits[s.Id].LastVisited)
            .ThenBy(s => s.Order).Take(5).ToList();
        if (FrequentScreens.Select(s => s.Id).SequenceEqual(frequent.Select(s => s.Id))) return;
        FrequentScreens = frequent;
        OnPropertyChanged(nameof(FrequentScreens));
    }

    private async Task SaveUsageAsync()
    {
        await _usageSave.WaitAsync();
        try
        {
            await _settings.SetAsync("ui.screenUsage", new ScreenUsageSettings { Visits = new Dictionary<string, ScreenVisit>(_visits) });
        }
        catch (Exception ex)
        {
            _dialogs.Error("Không lưu được danh sách màn hay dùng: " + ex.Message);
        }
        finally { _usageSave.Release(); }
    }

    partial void OnSelectedChanged(ScreenDescriptor? value)
    {
        if (value is null) return;
        if (!_views.TryGetValue(value.Id, out var view))
            _views[value.Id] = view = value.ViewType is null
                ? new PlaceholderView(value)
                : _services.GetRequiredService(value.ViewType);
        CurrentView = view;
    }

    [RelayCommand]
    private Task ShowHelpAsync() => Selected is null ? Task.CompletedTask : _help.ShowAsync(Selected.Id);

    private async Task RefreshJobsAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var active = await _jobs.ListAsync(new JobQuery(Statuses: [JobStatus.Running, JobStatus.Queued], Limit: 500));
            var running = active.Count(j => j.Status == JobStatus.Running);
            var queued = active.Count - running;
            IsBusy = running > 0;
            JobSummary = (running, queued) switch
            {
                (0, 0) => "Sẵn sàng",
                (_, 0) => $"{running} tác vụ đang chạy",
                (0, _) => $"{queued} tác vụ chờ",
                _ => $"{running} tác vụ đang chạy · {queued} chờ"
            };
        }
        catch (Exception)
        {
            // Hàng đợi chưa sẵn sàng (đang khởi động/tắt): giữ trạng thái cũ, lần poll sau thử lại.
        }
        finally
        {
            _polling = false;
        }
    }
}
