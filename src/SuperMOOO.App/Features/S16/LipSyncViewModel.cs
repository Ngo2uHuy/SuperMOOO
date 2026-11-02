using System.IO;
using System.Net.Http;
using System.Collections.ObjectModel;
using SuperMOOO.AI.LipSync;
using SuperMOOO.App.Controls;

namespace SuperMOOO.App.Features.S16;

[Service(ServiceLifetime.Transient)]
public sealed partial class LipSyncViewModel(MuseTalkInstaller installer, ISettingsStore settings, IDialogService dialogs) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Components { get; } = [];

    public string SizeNote => MuseTalkInstaller.SizeNote;
    public string SpeedNote => MuseTalkInstaller.SpeedNote;

    [ObservableProperty]
    public partial string PythonPath { get; set; } = settings.Get<LipSyncSettings>("lipsync").Python310;

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string Log { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(ImportCommand), nameof(RefreshCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    public async Task LoadStatusAsync()
    {
        var s = settings.Get<LipSyncSettings>("lipsync");
        s.Python310 = PythonPath.Trim();
        await settings.SetAsync("lipsync", s);
        Components.Clear();
        foreach (var c in await installer.CheckAsync(CancellationToken.None))
            Components.Add($"{(c.Ok ? "Đã có" : "Thiếu")} · {c.Name}: {c.Detail}");
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RefreshAsync() => LoadStatusAsync();

    [RelayCommand]
    private void PickPython()
    {
        if (dialogs.OpenFile("Chọn python.exe của Python 3.10", "python.exe|python.exe|Tất cả|*.*") is { } path) PythonPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallAsync()
    {
        if (!await dialogs.ConfirmAsync("Cài MuseTalk",
                $"{MuseTalkInstaller.SizeNote}\n\n{MuseTalkInstaller.SpeedNote}\n\nApp sẽ tạo venv Python 3.10 riêng và tải gói, mã, weights từ mạng. Tiếp tục?", "Cài")) return;
        await RunAsync(async (log, ct) =>
        {
            await LoadStatusAsync();
            return await installer.InstallAsync(log, ct);
        }, "Đã cài xong MuseTalk.");
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ImportAsync()
    {
        if (dialogs.PickFolder("Chọn thư mục AILive đã cài MuseTalk") is not { } dir) return;
        await RunAsync((log, ct) => installer.ImportFromAiLiveAsync(dir, log, ct), "Đã nhập MuseTalk từ AILive.");
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanRun() => !IsBusy;
    private bool CanCancel() => IsBusy;

    private async Task RunAsync(Func<IProgress<string>, CancellationToken, Task<string?>> work, string done)
    {
        IsBusy = true;
        Status = null;
        _cts = new CancellationTokenSource();
        try
        {
            var log = new Progress<string>(line => Log = TrimLog(Log + line + Environment.NewLine));
            var error = await work(log, _cts.Token);
            Status = error ?? done;
        }
        catch (OperationCanceledException) { Status = "Đã huỷ. Bấm Cài lại để tiếp tục phần còn thiếu."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
        {
            Status = "Lỗi: " + e.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            await LoadStatusAsync();
        }
    }

    private static string TrimLog(string log) => log.Length > 20000 ? log[^20000..] : log;
}
