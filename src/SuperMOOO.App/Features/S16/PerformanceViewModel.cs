using SuperMOOO.App.Controls;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.App.Features.S16;

[Service(ServiceLifetime.Transient)]
public sealed partial class PerformanceViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IRuntimeResources _resources;
    private readonly EncoderDetector _encoders;
    private readonly IDialogService _dialogs;
    private readonly int _cpuBudgetPercent;  // chưa có trên UI: giữ giá trị đã lưu khi bấm Lưu
    public PerformanceViewModel(ISettingsStore settings, IRuntimeResources resources, EncoderDetector encoders, IDialogService dialogs)
    {
        _settings = settings; _resources = resources; _encoders = encoders; _dialogs = dialogs;
        var value = settings.Get<PerformanceSettings>("performance");
        Automatic = value.Automatic; MediaParallel = value.MediaParallel; MediaThreads = value.MediaThreads;
        AiThreads = value.AiThreads; ReservedMemoryGb = value.ReservedMemoryGb; Encoder = value.Encoder; _cpuBudgetPercent = value.CpuBudgetPercent;
        ShowPlan(); ShowLoad();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Automatic) or nameof(MediaParallel) or nameof(MediaThreads) or nameof(AiThreads) or nameof(ReservedMemoryGb) or nameof(Encoder)) ShowPlan();
        };
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Manual))] public partial bool Automatic { get; set; } = true;
    [ObservableProperty] public partial int MediaParallel { get; set; } = 1;
    [ObservableProperty] public partial int MediaThreads { get; set; } = 4;
    [ObservableProperty] public partial int AiThreads { get; set; } = 2;
    [ObservableProperty] public partial int ReservedMemoryGb { get; set; } = 4;
    [ObservableProperty] public partial string Encoder { get; set; } = "auto";
    [ObservableProperty] public partial string Hardware { get; set; } = "";
    [ObservableProperty] public partial string PlanText { get; set; } = "";
    [ObservableProperty] public partial string LoadText { get; set; } = "";
    [ObservableProperty] public partial string EncoderStatus { get; set; } = "Chưa kiểm tra encoder.";
    [ObservableProperty] public partial string Status { get; set; } = "";
    public bool Manual => !Automatic;
    public int MaxThreads => _resources.Machine.LogicalProcessors;
    public int MaxReservedMemory => Math.Max(1, (int)(_resources.Machine.TotalMemoryBytes / 1073741824) - 1);
    public static IReadOnlyList<string> Encoders { get; } = ["auto", "x264", "qsv", "nvenc"];
    private PerformanceSettings Build() => new() { Automatic = Automatic, CpuBudgetPercent = _cpuBudgetPercent, MediaParallel = MediaParallel, MediaThreads = MediaThreads,
        AiThreads = AiThreads, ReservedMemoryGb = ReservedMemoryGb, Encoder = Encoder };
    private void ShowPlan()
    {
        var machine = _resources.Machine;
        var cores = machine.PhysicalCores > 0 ? $"{machine.PhysicalCores} nhân / " : "";
        Hardware = $"{machine.CpuName}\n{cores}{machine.LogicalProcessors} luồng CPU · RAM {machine.TotalMemoryBytes / 1073741824d:0.0} GB · còn trống {machine.AvailableMemoryBytes / 1073741824d:0.0} GB";
        var plan = PerformancePlanner.Calculate(machine, Build());
        PlanText = $"Media: tối đa {plan.MediaParallel} tiến trình × {plan.MediaThreads} luồng CPU\nAI local: {plan.AiThreads} luồng · ngưỡng RAM dự phòng: {plan.ReservedMemoryGb} GB\nTác vụ mạng: {plan.NetworkParallel} · AI worker: 1 · cloud: 2 · đăng bài: 1";
    }
    /// <summary>Gọi định kỳ khi màn hình đang mở (PerformanceSection).</summary>
    public void ShowLoad()
    {
        var load = _resources.Load;
        var state = !_resources.Plan.Automatic ? "chế độ thủ công, không tự giảm tải"
            : load.Pressure switch
            {
                ResourcePressure.Critical => "thiếu RAM: hoãn job media bổ sung, tắt server AI local đang rảnh",
                ResourcePressure.High => "máy bận: hoãn job media bổ sung, giảm luồng FFmpeg khởi động mới",
                _ => "bình thường"
            };
        if (load.ChildrenBelowNormal) state += " · tiến trình nền ưu tiên thấp";
        LoadText = $"Tải hiện tại: CPU {load.CpuPercent:0}% (ứng dụng khác {load.OtherCpuPercent:0}%) · RAM trống {load.AvailableMemoryBytes / 1073741824d:0.0} GB\nĐiều tiết: {state}";
    }
    [RelayCommand]
    public async Task RefreshAsync()
    {
        ShowPlan();
        try
        {
            var selected = await _encoders.SelectAsync(Automatic ? "auto" : Encoder, CancellationToken.None);
            var available = await _encoders.AvailableAsync(CancellationToken.None);
            EncoderStatus = $"Encoder sử dụng cho Timeline/Review: {selected.Key.ToUpperInvariant()}\nĐã kiểm tra encode thực tế: {string.Join(", ", available.Select(e => e.Key.ToUpperInvariant()))}.";
        }
        catch (Exception ex) when (JobViewModelBase.IsExpected(ex)) { EncoderStatus = ex.Message; }
    }
    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await _settings.SetAsync("performance", Build());
            Status = "Đã lưu. FFmpeg áp dụng ở lần chạy tiếp theo; khởi động lại app để cập nhật số worker và luồng AI local.";
            await RefreshAsync();
        }
        catch (Exception ex) when (JobViewModelBase.IsExpected(ex)) { _dialogs.Error(ex.Message); }
    }
    [RelayCommand]
    private async Task AutoConfigureAsync() { Automatic = true; await SaveAsync(); }
}
