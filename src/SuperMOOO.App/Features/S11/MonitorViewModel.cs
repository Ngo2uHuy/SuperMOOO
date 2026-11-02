using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Social.Listening;

namespace SuperMOOO.App.Features.S11;

public sealed record WatchRow(WatchTarget Target)
{
    public long Id => Target.Id;
    public string Name => Target.Name;
    public string Platform => Target.Platform;
    public string Type => Target.TargetType;
    public string Url => Target.Url;
    public string Frequency => $"{Target.FrequencyHours} giờ";
    public string LastChecked => Target.LastCheckedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "chưa";
}

public sealed record AuditRow(AuditEntry Entry)
{
    public string At => Entry.CreatedAt.ToLocalTime().ToString("dd/MM HH:mm:ss");
    public string Source => Entry.Source;
    public string Action => Entry.Action;
    public string Target => Entry.Target ?? "";
    public string Status => Entry.Status;
    public string Records => Entry.RecordsCount?.ToString() ?? "";
}

[Service(ServiceLifetime.Transient)]
public sealed partial class MonitorViewModel(WatchlistService watchlist, MetricService metrics, AuditLogService audit, IDialogService dialogs)
    : ObservableObject
{
    public IReadOnlyList<string> Platforms { get; } = ["youtube", "tiktok", "facebook", "instagram", "douyin", "xiaohongshu", "weibo", "bilibili", "kuaishou"];
    public IReadOnlyList<string> TargetTypes { get; } = ["channel", "profile", "hashtag", "search"];
    public IReadOnlyList<string> MetricNames { get; } = ["views", "likes", "comments", "followers", "shares"];

    public ObservableCollection<WatchRow> Watch { get; } = [];
    public ObservableCollection<MetricEntity> Entities { get; } = [];
    public ObservableCollection<AuditRow> Audit { get; } = [];

    public IReadOnlyList<MetricSample> ChartSamples { get; private set; } = [];

    public event EventHandler? ChartChanged;

    [ObservableProperty]
    public partial WatchRow? SelectedWatch { get; set; }

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial string NewUrl { get; set; } = "";

    [ObservableProperty]
    public partial string NewPlatform { get; set; } = "youtube";

    [ObservableProperty]
    public partial string NewType { get; set; } = "channel";

    [ObservableProperty]
    public partial int NewFrequency { get; set; } = 24;

    [ObservableProperty]
    public partial MetricEntity? SelectedEntity { get; set; }

    [ObservableProperty]
    public partial string SelectedMetric { get; set; } = "views";

    [ObservableProperty]
    public partial string? Status { get; set; }

    public async Task LoadAsync()
    {
        await RefreshWatchAsync();
        await RefreshEntitiesAsync();
        await RefreshInfoAsync();
    }

    [RelayCommand]
    public async Task RefreshWatchAsync()
    {
        Watch.Clear();
        foreach (var target in await watchlist.ListAsync()) Watch.Add(new WatchRow(target));
    }

    [RelayCommand]
    private async Task AddWatchAsync()
    {
        if (await Guard(async () =>
            {
                await watchlist.AddAsync(NewName, NewUrl, NewPlatform, NewType, NewFrequency);
                NewName = NewUrl = "";
            }))
        {
            Status = "Đã thêm vào danh sách theo dõi.";
            await RefreshWatchAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteWatchAsync()
    {
        if (SelectedWatch is not { } row || !await dialogs.ConfirmAsync("Xoá theo dõi", $"Xoá \"{row.Name}\" khỏi danh sách theo dõi?")) return;
        await watchlist.DeleteAsync(row.Id);
        await RefreshWatchAsync();
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (SelectedWatch is not { } row) return;
        if (await Guard(async () => Status = $"Đã xếp job kiểm tra \"{row.Name}\" (theo dõi ở khung Tác vụ của màn này): {await watchlist.CheckNowAsync(row.Id)}"))
            await RefreshEntitiesAsync();
    }

    [RelayCommand]
    public async Task RefreshEntitiesAsync()
    {
        Entities.Clear();
        foreach (var e in await metrics.ListEntitiesAsync()) Entities.Add(e);
        SelectedEntity ??= Entities.FirstOrDefault();
    }

    [RelayCommand]
    public async Task LoadChartAsync()
    {
        if (SelectedEntity is not { } entity)
        {
            Status = "Chưa chọn thực thể để xem chỉ số.";
            return;
        }

        ChartSamples = await metrics.GetHistoryAsync(entity.EntityKey, SelectedMetric, 200);
        ChartChanged?.Invoke(this, EventArgs.Empty);
        Status = ChartSamples.Count == 0 ? "Chưa có dữ liệu cho chỉ số này." : $"{ChartSamples.Count} điểm.";
    }

    [RelayCommand]
    public async Task RefreshInfoAsync()
    {
        Audit.Clear();
        foreach (var e in await audit.QueryAsync(100)) Audit.Add(new AuditRow(e));
    }

    private async Task<bool> Guard(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            dialogs.Error(ex.Message);
            return false;
        }
    }
}
