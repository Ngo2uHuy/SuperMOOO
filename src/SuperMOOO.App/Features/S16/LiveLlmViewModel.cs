using System.IO;
using SuperMOOO.AI.Llm;
using SuperMOOO.AI.Worker;

namespace SuperMOOO.App.Features.S16;

[Service(ServiceLifetime.Transient)]
public sealed partial class LiveLlmViewModel : ObservableObject
{
    private readonly LlmProviderRegistry _llm;
    private readonly ISettingsStore _settings;
    private readonly IProcessRunner _runner;
    private readonly AppPaths _paths;

    public LiveLlmViewModel(LlmProviderRegistry llm, ISettingsStore settings, IProcessRunner runner, AppPaths paths)
    {
        _llm = llm;
        _settings = settings;
        _runner = runner;
        _paths = paths;
        var s = settings.Get<LiveSettings>("live");
        LocalProvider = s.LocalProvider;
        LocalModel = s.LocalModel;
        CloudProvider = s.CloudProvider;
        CloudModel = s.CloudModel;
        NumCtx = s.NumCtx;
        ReplyCacheSeconds = s.ReplyCacheSeconds;
        RefreshModelState();
    }

    public IReadOnlyList<string> LocalProviders { get; } = [LlmProviderSpec.Bundled.Id, LlmProviderSpec.Local.Id];

    public IReadOnlyList<string> CloudProviders { get; } = LlmProviderSpec.All
        .Where(p => p.Id != LlmProviderSpec.Bundled.Id && p.Id != LlmProviderSpec.Local.Id).Select(p => p.Id).ToList();

    private string ModelPath => BundledLlmProvider.ModelPathOf(_paths);
    private string VenvPython => Path.Combine(_paths.WorkerVenv, "Scripts", "python.exe");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOllama), nameof(IsBundled))]
    public partial string LocalProvider { get; set; } = "";

    [ObservableProperty]
    public partial string LocalModel { get; set; } = "";

    [ObservableProperty]
    public partial string CloudProvider { get; set; } = "";

    [ObservableProperty]
    public partial string CloudModel { get; set; } = "";

    [ObservableProperty]
    public partial int NumCtx { get; set; }

    [ObservableProperty]
    public partial int ReplyCacheSeconds { get; set; }

    [ObservableProperty]
    public partial string ModelState { get; set; } = "";

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string Log { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(DownloadCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsOllama => LocalProvider == LlmProviderSpec.Local.Id;
    public bool IsBundled => LocalProvider == LlmProviderSpec.Bundled.Id;

    private void RefreshModelState() =>
        ModelState = File.Exists(ModelPath) ? $"Đã có model: {ModelPath}" : $"Chưa có model (dự kiến tại {ModelPath}).";

    [RelayCommand]
    private async Task SaveAsync()
    {
        await SaveCoreAsync();
        Status = "Đã lưu.";
    }

    private async Task SaveCoreAsync()
    {
        var s = _settings.Get<LiveSettings>("live");
        s.LocalProvider = LocalProvider;
        s.LocalModel = LocalModel.Trim();
        s.CloudProvider = CloudProvider;
        s.CloudModel = CloudModel.Trim();
        s.NumCtx = Math.Clamp(NumCtx, 1024, 16384);
        s.ReplyCacheSeconds = Math.Clamp(ReplyCacheSeconds, 0, 3600);
        await _settings.SetAsync("live", s);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckAsync()
    {
        await SaveCoreAsync();
        IsBusy = true;
        try { Status = (await _llm.Get(LocalProvider).CheckAsync(CancellationToken.None)).Message; }
        finally { IsBusy = false; }
    }

    // Dùng lại cơ chế tải model của AI worker (install_models.py); cần worker đã cài ở mục AI worker.
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DownloadAsync()
    {
        if (!File.Exists(VenvPython))
        {
            Status = "Chưa cài AI worker. Cài ở mục AI worker trước.";
            return;
        }

        IsBusy = true;
        Status = "Đang tải model, có thể mất vài phút…";
        try
        {
            var lines = new Progress<string>(line => Log = TrimLog(Log + line + Environment.NewLine));
            var result = await _runner.RunAsync(new ProcessSpec(VenvPython,
                ["-E", "-X", "utf8", "-s", "-m", "aivs_worker.install_models", "--models-dir", _paths.Models, "--models", "livellm"], WorkerLocation.Directory(_paths)),
                lines, CancellationToken.None);
            Status = result.ExitCode == 0 ? "Đã tải model." : $"Tải model thất bại (mã {result.ExitCode}). Xem nhật ký bên dưới.";
        }
        finally
        {
            IsBusy = false;
            RefreshModelState();
        }
    }

    private bool CanRun() => !IsBusy;

    private static string TrimLog(string log) => log.Length > 20000 ? log[^20000..] : log;
}
