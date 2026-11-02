using System.Collections.ObjectModel;
using SuperMOOO.Infrastructure.Health;

namespace SuperMOOO.App.Features.S16;

public sealed record HealthRow(string Name, string State, string Detail);

[Service(ServiceLifetime.Transient)]
public sealed partial class HealthViewModel(SystemHealthService health, CacheJanitor cache) : ObservableObject
{
    public ObservableCollection<HealthRow> Items { get; } = [];
    public IReadOnlyList<string> CacheScopes { get; } = ["all", .. cache.Scopes];

    [ObservableProperty]
    public partial string Stats { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedScope { get; set; } = "all";

    [ObservableProperty]
    public partial string? Status { get; set; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var stats = await health.GetStatsAsync();
        Stats = $"{stats.Os} · {stats.CpuCores} nhân · RAM {stats.TotalMemoryMb:N0} MB (app dùng {stats.ProcessMemoryMb:N0} MB) · đĩa dữ liệu trống {stats.DataDriveFreeMb:N0} MB";

        var report = await health.CheckAsync();
        Items.Clear();
        foreach (var item in report.Items)
            Items.Add(new HealthRow(item.Name, item.Available ? "Tốt" : "Thiếu / lỗi", item.Detail ?? ""));
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        var result = await cache.ClearAsync(SelectedScope);
        Status = $"Đã xoá {result.FilesDeleted} file ({result.BytesFreed / (1024 * 1024):N1} MB).";
    }
}
