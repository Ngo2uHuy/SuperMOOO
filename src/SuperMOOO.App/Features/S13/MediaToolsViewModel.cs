using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Core.Operations;
using SuperMOOO.Infrastructure.Operations;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.App.Features.S13;

/// <summary>S13: danh sách operation nhóm Media + form động; chạy trực tiếp qua OperationInvoker với tiến độ và huỷ.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class MediaToolsViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // giữ nguyên tiếng Việt, không đổi thành mã 
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly OperationInvoker _invoker;
    private readonly IDialogService _dialogs;
    private readonly ShellService _shell;
    private CancellationTokenSource? _cts;

    public MediaToolsViewModel(OperationRegistry registry, OperationInvoker invoker, IDialogService dialogs, ShellService shell)
    {
        _invoker = invoker;
        _dialogs = dialogs;
        _shell = shell;
        Operations = registry.All.Where(d => d.Group == OperationGroup.Media).ToList();
        Selected = Operations.FirstOrDefault();
    }

    public IReadOnlyList<OperationDescriptor> Operations { get; }

    [ObservableProperty]
    public partial OperationDescriptor? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Chọn công cụ, nhập thông số rồi bấm Chạy.";

    [ObservableProperty]
    public partial string ResultText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand), nameof(OpenFolderCommand))]
    public partial string? OutputPath { get; set; }

    /// <summary>Chạy operation đang chọn với request đã dựng từ form (null nếu form chưa hợp lệ).</summary>
    public async Task RunAsync(object? request)
    {
        if (Selected is null || request is null || IsRunning) return;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Progress = 0;
        OutputPath = null;
        ResultText = "";
        Status = $"Đang chạy: {Selected.Label}…";
        _dialogs.Info("Đã bắt đầu", Selected.Label);
        try
        {
            using var owner = JobOwner.Begin(JobOwner.Screen("S13"));   // công cụ xếp job (Cắt Shorts) hiện ở khung Tác vụ của màn này
            var body = JsonSerializer.SerializeToNode(request, Selected.RequestType, Json) as JsonObject ?? [];
            var progress = new Progress<ProgressInfo>(p =>
            {
                if (p.Fraction >= 0) Progress = p.Fraction * 100;
                if (!string.IsNullOrEmpty(p.Message)) Status = p.Message;
            });
            var result = await _invoker.InvokeAsync(Selected.Key, body, progress, _cts.Token);
            ResultText = result?.ToJsonString(Json) ?? "";
            OutputPath = result is JsonObject o && o["path"] is JsonValue v && v.TryGetValue<string>(out var path) ? path : null;
            var queued = result is JsonObject { } q && q["jobId"] is not null;
            Progress = queued ? 0 : 100;
            Status = (result as JsonObject)?["message"]?.ToString() ?? "Xong.";
            _dialogs.Info(queued ? "Đã đưa vào hàng đợi" : "Hoàn tất", OutputPath is null ? Status : "Đã xong; bấm Mở file hoặc Mở thư mục ở khung Kết quả.");
        }
        catch (OperationCanceledException)
        {
            Status = "Đã huỷ.";
        }
        catch (Exception ex) when (Unwrap(ex) is ValidationException or ArgumentException or IOException or MediaProbeException
                                       or FfmpegException or InvalidOperationException)
        {
            // OperationInvoker gọi handler bằng reflection nên lỗi đồng bộ có thể bị bọc trong TargetInvocationException.
            var message = Unwrap(ex).Message;
            Status = "Lỗi: " + message;
            _dialogs.Error(message);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(HasOutput))]
    private void OpenFile()
    {
        if (OutputPath is not null) Guard(() => _shell.OpenFile(OutputPath));
    }

    [RelayCommand(CanExecute = nameof(HasOutput))]
    private void OpenFolder()
    {
        if (OutputPath is not null) Guard(() => _shell.RevealFile(OutputPath));
    }

    private static Exception Unwrap(Exception ex) => ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private bool HasOutput() => !string.IsNullOrEmpty(OutputPath);

    private void Guard(Action action)
    {
        try { action(); }
        catch (IOException ex) { _dialogs.Error(ex.Message); }
    }
}
