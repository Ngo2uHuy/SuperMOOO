using System.Collections.ObjectModel;
using SuperMOOO.AI.Tts;
using SuperMOOO.App.Controls;

namespace SuperMOOO.App.Features.S16;

/// <summary>Một dòng bảng Ngôn ngữ → model: chọn model ưu tiên 1, xem model đang dùng thật (đã cài, CPU/GPU).</summary>
public sealed partial class LanguageRouteRow(string key, string name, IReadOnlyList<string> options, string first) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public IReadOnlyList<string> Options { get; } = options;

    [ObservableProperty]
    public partial string First { get; set; } = first;

    [ObservableProperty]
    public partial string Active { get; set; } = "";
}

[Service(ServiceLifetime.Transient)]
public sealed partial class TtsLocalViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly NativeTtsServerManager _server;
    private readonly IReadOnlyList<NativeTtsProvider> _providers;
    private readonly TtsLanguageRouter _router;

    public TtsLocalViewModel(ISettingsStore settings, NativeTtsServerManager server, IEnumerable<ITtsProvider> providers, TtsLanguageRouter router)
    {
        _settings = settings;
        _server = server;
        _router = router;
        _providers = [.. providers.OfType<NativeTtsProvider>()];

        var s = settings.Get<TtsNativeSettings>("tts.native");
        Backend = s.Backend;
        Port = s.Port;
        Threads = s.Threads;
        Device = s.Device;
        MaxLoadedModels = s.MaxLoadedModels;
        ChunkSize = s.ChunkSize;
        Steps = s.Steps;
        Language = s.Language;
        LowMemory = s.LowMemory;
        foreach (var (key, name) in new[] { ("vi", "Tiếng Việt"), ("ja", "Tiếng Nhật"), ("ko", "Tiếng Hàn"), ("zh", "Tiếng Trung · giản thể"), ("zh-TW", "Tiếng Trung · phồn thể"), ("en", "Tiếng Anh"), (TtsLanguageRouter.Other, "Ngôn ngữ khác") })
            Routes.Add(new LanguageRouteRow(key, name, TtsLanguageRouter.Candidates(key), router.Chain(key)[0]));
        Refresh();
    }

    public ObservableCollection<LanguageRouteRow> Routes { get; } = [];

    public IReadOnlyList<string> Backends { get; } = ["cpu", "cuda", "vulkan"];

    [ObservableProperty]
    public partial string Backend { get; set; } = "cpu";

    [ObservableProperty]
    public partial int Port { get; set; }

    [ObservableProperty]
    public partial int Threads { get; set; }

    [ObservableProperty]
    public partial int Device { get; set; }

    [ObservableProperty]
    public partial int MaxLoadedModels { get; set; }

    [ObservableProperty]
    public partial int ChunkSize { get; set; }

    [ObservableProperty]
    public partial int Steps { get; set; }

    [ObservableProperty]
    public partial string Language { get; set; } = "vi";

    [ObservableProperty]
    public partial bool LowMemory { get; set; }

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = "";

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(WarmCommand), nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SaveAsync()
    {
        var value = new TtsNativeSettings
        {
            Backend = Backend, Port = Port, Threads = Threads, Device = Device, MaxLoadedModels = MaxLoadedModels,
            ChunkSize = ChunkSize, Steps = Steps, Language = Language.Trim(), LowMemory = LowMemory,
            LanguagePriority = Routes.Where(r => r.First != TtsLanguageRouter.Defaults[r.Key][0]).ToDictionary(r => r.Key, r => r.First)
        };
        try
        {
            NativeTtsServerManager.Validate(value);
            await _settings.SetAsync("tts.native", value);
            await _server.StopAsync();      // cấu hình mới chỉ có hiệu lực khi server khởi động lại
            Status = "Đã lưu. Server sẽ khởi động lại với cấu hình mới ở lần đọc kế tiếp.";
        }
        catch (ArgumentException e)
        {
            Status = e.Message;
        }

        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync() => await RunAsync(async () =>
    {
        await _server.EnsureStartedAsync(CancellationToken.None);
        Status = "Server đã sẵn sàng.";
    });

    [RelayCommand]
    private async Task StopAsync()
    {
        await _server.StopAsync();
        Status = "Đã dừng server.";
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task WarmAsync() => await RunAsync(async () =>
    {
        // Chỉ nạp model đang được bảng ngôn ngữ dùng, không nạp mọi model đã cài.
        var used = Routes.Select(r => _router.Pick(r.Key)).ToHashSet();
        foreach (var provider in _providers.Where(p => used.Contains(p.Id) && _server.IsInstalled(p.Family)))
            await provider.WarmAsync(CancellationToken.None);
        Status = "Đã nạp model.";
    });

    [RelayCommand]
    private void Refresh()
    {
        var s = _server.GetStatus();
        ServerStatus = s.ExeFound
            ? $"audiocpp_server: {s.ExePath} · VieNeu: {(s.VieNeuInstalled ? "đã cài" : "chưa đủ")} · OmniVoice: {(s.OmniVoiceInstalled ? "đã cài" : "chưa đủ")} ({_server.DeviceLabel("omnivoice")}{(s.VulkanFailed ? ", Vulkan lỗi" : "")}) · Supertonic: {(s.SupertonicInstalled ? "đã cài" : "chưa cài")} · Server chính: {(s.Running ? "đang chạy" : "đang dừng")} · Server v0.9.2 CPU: {(s.SupertonicRunning ? "đang chạy" : "đang dừng")} · Server Vulkan: {(s.VulkanRunning ? "đang chạy" : "đang dừng")}"
            : "Chưa tìm thấy audiocpp_server.exe (đặt ở tools\\native_tts hoặc tab Đường dẫn & công cụ).";

        // Theo cấu hình đã lưu: chọn ưu tiên 1 mới chỉ có hiệu lực sau khi bấm Lưu.
        foreach (var row in Routes)
        {
            var chain = _router.Chain(row.Key);
            var active = _router.Pick(row.Key);
            row.Active = $"Đang dùng: {Name(active)} · " + string.Join(" → ", chain.Select(p => $"{Name(p)} ({DeviceOf(p)})"));
        }
    }

    private static string Name(string provider) => Labels.Term(provider).Replace(" (chạy máy)", "");

    private string DeviceOf(string provider) => !_router.IsAvailable(provider) ? (provider == VoiceIds.Edge ? "chưa bật" : "chưa cài")
        : provider == VoiceIds.Edge ? "mạng" : _server.DeviceLabel(TtsLanguageRouter.Family(provider));

    private bool CanRun() => !IsBusy;

    private async Task RunAsync(Func<Task> work)
    {
        IsBusy = true;
        Status = null;
        try { await work(); }
        catch (Exception e) when (e is InvalidOperationException or ProviderException or ArgumentException)
        {
            Status = e.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }
}
