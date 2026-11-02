using System.IO;
using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Infrastructure.Logging;
using Serilog.Events;

namespace SuperMOOO.App.Features.S16;

[Service(ServiceLifetime.Transient)]
public sealed partial class LogViewModel(LogService logs, IDialogService dialogs) : ObservableObject
{
    public IReadOnlyList<LogEventLevel> Levels { get; } = [LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Warning, LogEventLevel.Error];
    public ObservableCollection<string> Lines { get; } = [];

    [ObservableProperty]
    public partial LogEventLevel Level { get; set; } = LogEventLevel.Information;

    [ObservableProperty]
    public partial string? Status { get; set; }

    partial void OnLevelChanged(LogEventLevel value) => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        Lines.Clear();
        foreach (var line in logs.Tail(500, Level)) Lines.Add($"{line.At.LocalDateTime:HH:mm:ss} [{line.Level}] {line.Message}");
        Status = $"{Lines.Count} dòng";
    }

    [RelayCommand]
    private void Clear()
    {
        logs.ClearBuffer();
        Refresh();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = dialogs.SaveFile("Lưu nhật ký", "Văn bản (*.txt)|*.txt|Log (*.log)|*.log", $"aivs-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        if (path is null) return;
        try
        {
            await logs.ExportAsync(path);
            Status = $"Đã lưu {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialogs.Error(ex.Message);
        }
    }
}
