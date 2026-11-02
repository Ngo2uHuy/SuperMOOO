using System.IO;
using System.Collections.ObjectModel;
using SuperMOOO.AI.Worker;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;
using System.Text.Json.Nodes;
using SuperMOOO.AI.Worker.V1;

namespace SuperMOOO.App.Features.S16;

[Service(ServiceLifetime.Transient)]
public sealed partial class AiWorkerViewModel : ObservableObject
{
    private readonly IToolLocator _tools;
    private readonly IJobQueue _jobs;
    private readonly JobTracker _tracker;
    private readonly IDialogService _dialogs;
    private readonly IAiWorkerClient _worker;
    private readonly AppPaths _paths;

    public AiWorkerViewModel(IToolLocator tools, IJobQueue jobs, JobTracker tracker, IDialogService dialogs, IAiWorkerClient worker, AppPaths paths)
    {
        _tools = tools;
        _jobs = jobs;
        _tracker = tracker;
        _dialogs = dialogs;
        _worker = worker;
        _paths = paths;
        RefreshStatus();
    }

    public ObservableCollection<string> Capabilities { get; } = [];

    private string VenvPython => Path.Combine(_paths.WorkerVenv, "Scripts", "python.exe");

    [ObservableProperty]
    public partial string PythonStatus { get; set; } = "";

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string Log { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallWorkerCommand), nameof(CheckCapabilitiesCommand))]
    public partial bool IsBusy { get; set; }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task InstallWorkerAsync() => RunAsync(async () =>
    {
        var active = await _jobs.ListAsync(new JobQuery(TypePrefix: ComponentSetup.JobType,
            Statuses: [JobStatus.Queued, JobStatus.Running]));
        var id = active.FirstOrDefault()?.Id ?? await _jobs.EnqueueAsync(new JobRequest(ComponentSetup.JobType,
            "Cài đầy đủ thành phần SuperMOOO", new JsonObject(), JobLane.AiWorker, Owner: JobOwner.Screen("S16")));
        _dialogs.Info("Thiết lập SuperMOOO", "Đang tự cài môi trường và model. Theo dõi tiến độ, huỷ hoặc thử lại trong khung tác vụ bên dưới.");
        var result = await _tracker.WaitAsync(id, snapshot =>
        {
            Status = snapshot.Message;
            if (!string.IsNullOrEmpty(snapshot.Message) && !Log.EndsWith(snapshot.Message + Environment.NewLine, StringComparison.Ordinal))
                Log = TrimLog(Log + snapshot.Message + Environment.NewLine);
        }, CancellationToken.None);
        Status = result.Error ?? result.Message;
        if (result.Status == JobStatus.Succeeded) _dialogs.Info("Đã thiết lập xong", "Môi trường và model AI đã được cài đầy đủ.");
        else if (result.Status is JobStatus.Failed or JobStatus.WaitingManualRetry) _dialogs.Error(result.Error ?? "Cài đặt thất bại. Chọn tác vụ và bấm Thử lại.");
        else Status = "Đã huỷ cài đặt. Bấm Cài đầy đủ thành phần để tiếp tục.";
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckCapabilitiesAsync()
    {
        await RunAsync(async () =>
        {
            Capabilities.Clear();
            try
            {
                var caps = await _worker.GetCapabilitiesAsync(CancellationToken.None);
                Capabilities.Add($"Worker {caps.WorkerVersion}, Python {caps.PythonVersion}");
                Capabilities.Add(Describe("Nhận diện giọng nói (ASR)", caps.Asr));
                Capabilities.Add(Describe("Phát hiện lời nói (VAD)", caps.Vad));
                Capabilities.Add(Describe("Khử nhiễu AI", caps.Denoise));
                Capabilities.Add(Describe("Chuyển giọng RVC (tuỳ chọn)", caps.VoiceConversion));
                foreach (var model in caps.Models)
                    Capabilities.Add($"Model {model.Name}: {(model.Installed ? "đã cài" : "chưa cài")}{(model.Loaded ? ", đang nạp" : "")}");
                Status = "Đã kiểm tra.";
            }
            catch (AiWorkerException e)
            {
                Status = e.Message;
                _dialogs.Error(e.Message);
            }
        });
    }

    [RelayCommand]
    private void RefreshStatus() =>
        PythonStatus = _tools.TryResolve(ToolKind.Python, out var path)
            ? $"Python: {path}{(File.Exists(VenvPython) ? " (venv đã cài)" : "")}"
            : "Chưa tìm thấy Python.";

    private bool CanRun() => !IsBusy;

    private static string Describe(string name, ModuleStatus status) =>
        status.Available ? $"{name}: sẵn sàng" : $"{name}: không dùng được. {status.Reason}";

    private async Task RunAsync(Func<Task> work)
    {
        IsBusy = true;
        Status = null;
        try { await work(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Status = e.Message;
            _dialogs.Error(e.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    private static string TrimLog(string log) => log.Length > 20000 ? log[^20000..] : log;
}
