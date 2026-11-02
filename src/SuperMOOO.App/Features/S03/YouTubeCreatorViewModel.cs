using System.Collections.ObjectModel;
using System.IO;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Tts;
using SuperMOOO.App.Controls;
using SuperMOOO.Workflows.TtsStudio;
using SuperMOOO.App.Features.S01;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Workflows;
using SuperMOOO.Workflows.Creator;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S03;

public sealed partial class ScriptRow(ScriptFile file) : ObservableObject
{
    public ScriptFile File => file;
    public string Name => file.Name;
    public string Detail => $"{file.Words:N0} từ";

    [ObservableProperty] public partial bool IsSelected { get; set; } = true;
}

public sealed record WaveColor(string Name, string Hex);

/// <summary>S03 tab YouTube: kịch bản TXT/DOCX (thư mục hoặc dán), slide ảnh/clip, giọng đọc, hiệu ứng, intro/outro; mỗi kịch bản một video.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class YouTubeCreatorViewModel(YouTubeCreatorService creator, ScriptLibrary scripts, SlideLibrary slides, VoiceCatalogService voices,
    VoiceSpeed voiceSpeed, IAudioPlayer player, ShellService shell, JobTracker tracker, IDialogService dialogs) : JobViewModelBase(tracker, dialogs)
{
    protected override string JobsOwner => JobOwner.Screen("S03");

    public static IReadOnlyList<int> Heights { get; } = [720, 1080, 1440];

    public static IReadOnlyList<WaveColor> WaveColors { get; } =
    [
        new("Xanh neon", "#00FFCC"), new("Đỏ rực", "#FF3366"), new("Vàng gold", "#FFCC00"), new("Trắng tinh", "#FFFFFF"), new("Tím cyber", "#AA00FF")
    ];

    public static IReadOnlyList<SlideOrderChoice> SlideOrders { get; } =
    [
        new(SlideOrder.Random, "Ngẫu nhiên"), new(SlideOrder.Alphabetical, "Theo tên (A→Z)"), new(SlideOrder.Sequential, "Theo thứ tự đã chọn")
    ];

    public IReadOnlyList<LanguageChoice> Languages { get; } = [.. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];
    public ObservableCollection<ScriptRow> Scripts { get; } = [];
    public ObservableCollection<string> Slides { get; } = [];
    public ObservableCollection<VoiceInfo> Voices { get; } = [];
    public ObservableCollection<string> Results { get; } = [];

    [ObservableProperty] public partial string ScriptFolder { get; set; } = "";
    [ObservableProperty] public partial string PastedName { get; set; } = "";
    [ObservableProperty] public partial string PastedText { get; set; } = "";
    [ObservableProperty] public partial string SlideFolder { get; set; } = "";
    [ObservableProperty] public partial SlideOrderChoice SelectedSlideOrder { get; set; } = SlideOrders[0];
    [ObservableProperty] public partial LanguageChoice? Language { get; set; }
    [ObservableProperty] public partial VoiceInfo? SelectedVoice { get; set; }
    [ObservableProperty] public partial double Speed { get; set; } = 1.0;
    [ObservableProperty] public partial int Height { get; set; } = 1080;
    [ObservableProperty] public partial bool KenBurns { get; set; } = true;
    [ObservableProperty] public partial bool Fade { get; set; } = true;
    [ObservableProperty] public partial bool Waveform { get; set; } = true;
    [ObservableProperty] public partial WaveColor SelectedWaveColor { get; set; } = WaveColors[0];
    [ObservableProperty] public partial string IntroPath { get; set; } = "";
    [ObservableProperty] public partial string OutroPath { get; set; } = "";
    [ObservableProperty] public partial string OutputFolder { get; set; } = "";
    [ObservableProperty] public partial string? OutputVideo { get; set; }

    public string SelectedSummary => Scripts.Count == 0 ? "" : $"Đã chọn {Scripts.Count(s => s.IsSelected)}/{Scripts.Count} kịch bản";

    public event EventHandler<string>? ResultReady;

    public async Task LoadAsync()
    {
        Language ??= Languages.FirstOrDefault(l => l.Code == "vi") ?? Languages[0];
        await ReloadVoicesAsync();
    }

    partial void OnLanguageChanged(LanguageChoice? value)
    {
        if (value is not null && Voices.Count > 0) _ = ReloadVoicesAsync();
    }

    private async Task ReloadVoicesAsync()
    {
        await GuardAsync(async () =>
        {
            var keep = SelectedVoice?.Id;
            var list = await voices.ListAsync(Language?.Code);
            Voices.Clear();
            foreach (var v in list) Voices.Add(v);
            SelectedVoice = Voices.FirstOrDefault(v => v.Id == keep) ?? Voices.FirstOrDefault();
        });
    }

    [RelayCommand]
    private void BrowseScriptFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục kịch bản (.txt, .docx)") is not { } folder) return;
        ScriptFolder = folder;
        ScanScripts();
    }

    [RelayCommand]
    private void ScanScripts()
    {
        if (string.IsNullOrWhiteSpace(ScriptFolder)) { Dialogs.Error("Chọn thư mục kịch bản trước."); return; }
        try
        {
            var found = scripts.Scan(ScriptFolder);
            Scripts.Clear();
            foreach (var f in found) Scripts.Add(new ScriptRow(f));
            Status = found.Count == 0 ? "Thư mục không có file .txt/.docx nào." : $"Tìm thấy {found.Count} kịch bản.";
            OnPropertyChanged(nameof(SelectedSummary));
        }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private void AddScriptFile()
    {
        if (Dialogs.OpenFile("Chọn file kịch bản", "Kịch bản (*.txt;*.docx)|*.txt;*.docx") is not { } file) return;
        try
        {
            var script = scripts.Load(file);
            if (Scripts.Any(s => string.Equals(s.File.Path, script.Path, StringComparison.OrdinalIgnoreCase))) return;
            Scripts.Add(new ScriptRow(script));
            Status = $"Đã thêm kịch bản {script.Name}.";
            OnPropertyChanged(nameof(SelectedSummary));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { Dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private void ToggleAllScripts()
    {
        var on = Scripts.Any(s => !s.IsSelected);
        foreach (var s in Scripts) s.IsSelected = on;
        OnPropertyChanged(nameof(SelectedSummary));
    }

    [RelayCommand]
    private void BrowseSlideFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục ảnh/clip nền") is not { } folder) return;
        SlideFolder = folder;
        try
        {
            Slides.Clear();
            foreach (var s in slides.Scan(folder)) Slides.Add(s);
            Status = Slides.Count == 0 ? "Thư mục không có ảnh/clip nào." : $"Tìm thấy {Slides.Count} slide.";
        }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private void AddSlide()
    {
        if (Dialogs.OpenFile("Chọn ảnh hoặc clip nền", "Ảnh/clip|*.jpg;*.jpeg;*.png;*.webp;*.mp4;*.mov|Tất cả|*.*") is { } file && !Slides.Contains(file)) Slides.Add(file);
    }

    [RelayCommand]
    private void ClearSlides() => Slides.Clear();

    /// <summary>Kéo-thả vào danh sách slide: file ảnh/clip hoặc thư mục.</summary>
    public void AddSlides(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            foreach (var f in Directory.Exists(path) ? slides.Scan(path) : [path])
                if (SlideLibrary.Extensions.Contains(Path.GetExtension(f)) && !Slides.Contains(f)) Slides.Add(f);
    }

    [RelayCommand]
    private void Pick(string target)
    {
        var video = "Video|*.mp4;*.mov;*.mkv|Tất cả|*.*";
        if (Dialogs.OpenFile(target == "intro" ? "Chọn video intro" : "Chọn video outro", video) is not { } file) return;
        if (target == "intro") IntroPath = file;
        else OutroPath = file;
    }

    [RelayCommand]
    private void ChooseOutputFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục xuất video") is { } folder) OutputFolder = folder;
    }

    [RelayCommand]
    private Task PreviewVoiceAsync() => RunDirectAsync(async (_, ct) =>
    {
        if (SelectedVoice is null) throw new InvalidOperationException("Chưa chọn giọng đọc.");
        player.Play(await voiceSpeed.PreviewAsync(SelectedVoice.Id, Language?.Code, Speed, ct));
    }, "Đang đọc thử…");

    /// <summary>Kết quả của lần dựng vừa xong hoặc của job đã xong được chọn ở khung tác vụ.</summary>
    public void ShowResult(string video)
    {
        OutputVideo = video;
        if (!Results.Contains(video)) Results.Insert(0, video);
        ResultReady?.Invoke(this, video);
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        var picked = Scripts.Where(s => s.IsSelected).Select(s => new YouTubeScript(s.Name, null, s.File.Path)).ToList();
        if (!string.IsNullOrWhiteSpace(PastedText))
            picked.Add(new YouTubeScript(string.IsNullOrWhiteSpace(PastedName) ? $"Script_{DateTime.Now:HHmmss}" : PastedName.Trim(), PastedText));
        if (picked.Count == 0) { Dialogs.Error("Chọn kịch bản từ thư mục hoặc dán nội dung kịch bản."); return; }
        if (SelectedVoice is null) { Dialogs.Error("Chưa chọn giọng đọc."); return; }
        var options = Options();

        if (picked.Count == 1)
        {
            var job = await RunJobAsync(async ct => (await creator.StartBatchAsync(picked, [.. Slides], options, ct))[0], "Đang dựng video…");
            if (job is null) return;
            if (PipelineResult.Value(job.Result, "video") is { } video) ShowResult(video);
            return;
        }

        await GuardAsync(async () =>
        {
            if (!await Dialogs.ConfirmAsync("Dựng hàng loạt", $"Dựng {picked.Count} video với cùng thiết lập?")) return;
            using var owner = OwnJobs();
            var ids = await creator.StartBatchAsync(picked, [.. Slides], options);
            Status = $"Đã đưa {ids.Count} video vào hàng đợi (theo dõi ở khung Tác vụ của màn này).";
            Dialogs.Info("Đã đưa vào hàng đợi", Status);
        });
    }

    private YouTubeCreatorOptions Options() => new()
    {
        VoiceId = SelectedVoice!.Id, Language = Language?.Code, Speed = Speed, Height = Height, KenBurns = KenBurns, Fade = Fade, Waveform = Waveform,
        WaveformColor = SelectedWaveColor.Hex, IntroPath = IntroPath.Length > 0 ? IntroPath : null, OutroPath = OutroPath.Length > 0 ? OutroPath : null,
        SlideOrder = SelectedSlideOrder.Value, OutputFolder = OutputFolder.Length > 0 ? OutputFolder : null
    };

    [RelayCommand]
    private void OpenOutput()
    {
        if (OutputVideo is not null) Open(() => shell.OpenFile(OutputVideo));
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (OutputVideo is not null) Open(() => shell.RevealFile(OutputVideo));
    }

    private void Open(Action action)
    {
        try { action(); }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }
}

public sealed record SlideOrderChoice(SlideOrder Value, string Name);
