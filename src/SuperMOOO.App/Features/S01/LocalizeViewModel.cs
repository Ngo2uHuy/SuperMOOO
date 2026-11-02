using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Tts;
using SuperMOOO.AI.Vision;
using SuperMOOO.AI.Worker;
using SuperMOOO.App.Controls;
using SuperMOOO.Workflows.TtsStudio;
using SuperMOOO.Infrastructure.Health;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Media.Compose;
using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Preview;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Media.Tools;
using SuperMOOO.Workflows;
using SuperMOOO.Workflows.Localize;
using SuperMOOO.Workflows.Subtitles;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S01;

public sealed record LanguageChoice(string Code, string Display);

public sealed partial class BgmSlot(int index) : ObservableObject
{
    public int Index => index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial string Path { get; set; } = "";

    public string Label => string.IsNullOrEmpty(Path) ? $"Nhạc {index + 1} (trống)" : $"Nhạc {index + 1}: {System.IO.Path.GetFileName(Path)}";

    // Tên truy cập của mục danh sách (UI Automation gọi ToString()).
    public override string ToString() => Label;
}

/// <summary>S01: cấu hình lồng tiếng / chỉ phụ đề, xếp job (1 file hoặc cả thư mục), theo dõi tiến độ và xem kết quả.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class LocalizeViewModel(LocalizeService localize, SubtitleEditingService subtitles, VoiceCatalogService voices,
    ModelProfileService models, SubtitleStylePresets stylePresets, PreviewRenderer preview, IMediaProbe probe, VoiceSpeed voiceSpeed, CacheJanitor janitor,
    ShellService shell, IAudioPlayer player, ISettingsStore settings, AppPaths paths, JobTracker tracker, IDialogService dialogs, BurnedSubtitleService burned)
    : JobViewModelBase(tracker, dialogs)
{
    protected override string JobsOwner => JobOwner.Screen("S01");

    private const string MediaFilter = "Media (*.mp4;*.mkv;*.mov;*.avi;*.flv;*.ts;*.webm;*.mp3;*.wav)|*.mp4;*.mkv;*.mov;*.avi;*.flv;*.ts;*.webm;*.mp3;*.wav|Tất cả|*.*";
    private const string AllFilter = "Tất cả|*.*";
    private static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$");

    public static IReadOnlyList<string> Contexts { get; } =
    [
        "AI tự nhận diện ngữ cảnh (Tổng hợp)", "Phim hành động / kịch tính", "Phim tình cảm / đời sống",
        "Review phim / Tóm tắt", "Tin tức / Phóng sự", "Hài hước / Viral"
    ];

    public IReadOnlyList<LanguageChoice> SourceLanguages { get; } =
        [new("auto", "Tự nhận diện (auto)"), .. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];

    public IReadOnlyList<LanguageChoice> TargetLanguages { get; } = [.. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];
    public IReadOnlyList<string> DenoiseModes { get; } = ["Off", "Fast", "Ai"];
    public IReadOnlyList<string> SeparationQualities { get; } = [Core.Models.SeparationQuality.Roformer, Core.Models.SeparationQuality.Mdx];
    public IReadOnlyList<string> Aspects { get; } = ["original", "1:1", "4:5", "3:4", "9:16", "16:9"];
    public IReadOnlyList<string> Fonts { get; } = ["Arial", "Segoe UI", "Roboto", "Montserrat", "Be Vietnam Pro", "Tahoma"];
    public IReadOnlyList<MaskRegionPreset> MaskRegions { get; } = [MaskRegionPreset.Bottom, MaskRegionPreset.BottomThick, MaskRegionPreset.Top, MaskRegionPreset.Center];
    public IReadOnlyList<MaskStyle> MaskStyles { get; } = [MaskStyle.Blur, MaskStyle.Gradient, MaskStyle.BlackBox];
    public IReadOnlyList<SubtitleStylePreset> StylePresets => stylePresets.All;
    public ObservableCollection<string> Sources { get; } = [];
    public ObservableCollection<VoiceInfo> Voices { get; } = [];
    public ObservableCollection<BgmSlot> BgmSlots { get; } = [new(0), new(1), new(2), new(3), new(4)];

    // --- Nguồn & luồng ---
    [ObservableProperty] public partial string InputPath { get; set; } = "";
    [ObservableProperty] public partial string? SelectedSource { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DubLayout))]
    public partial bool SubtitlesOnly { get; set; }

    // Làm mờ, logo/canvas/PiP và vị trí phụ đề chỉ áp ở luồng lồng tiếng (LocalizeComposer); luồng "Chỉ phụ đề" dùng vùng che + kiểu có sẵn.
    public bool DubLayout => !SubtitlesOnly;
    [ObservableProperty] public partial bool UseAsr { get; set; } = true;
    [ObservableProperty] public partial bool UseEditedSubtitles { get; set; } = true;
    [ObservableProperty] public partial bool Translate { get; set; } = true;
    [ObservableProperty] public partial bool Dub { get; set; } = true;
    [ObservableProperty] public partial bool BurnSubtitles { get; set; } = true;
    [ObservableProperty] public partial bool Mirror { get; set; }
    [ObservableProperty] public partial bool CutMeta { get; set; } = true;
    [ObservableProperty] public partial bool ReuseCache { get; set; } = true;
    [ObservableProperty] public partial string DenoiseMode { get; set; } = "Off";
    [ObservableProperty] public partial string AsrModel { get; set; } = "";

    // --- Dịch ---
    [ObservableProperty] public partial LanguageChoice? SourceLanguage { get; set; }
    [ObservableProperty] public partial LanguageChoice? TargetLanguage { get; set; }
    [ObservableProperty] public partial string TranslationContext { get; set; } = Contexts[0];
    [ObservableProperty] public partial string SubPreview { get; set; } = "";

    // --- Giọng ---
    [ObservableProperty] public partial VoiceInfo? SelectedVoice { get; set; }
    [ObservableProperty] public partial double Speed { get; set; } = 1.0;
    [ObservableProperty] public partial bool AutoFit { get; set; } = true;
    [ObservableProperty] public partial bool SkipFailedSentences { get; set; }

    // --- Âm thanh (dB như Craw) ---
    [ObservableProperty] public partial bool MuteOriginal { get; set; }
    [ObservableProperty] public partial double OriginalDb { get; set; } = -18;
    [ObservableProperty] public partial double VoiceDb { get; set; }
    [ObservableProperty] public partial bool SmartAudio { get; set; } = true;
    [ObservableProperty] public partial bool SeparateVocals { get; set; }
    [ObservableProperty] public partial string SeparationQuality { get; set; } = Core.Models.SeparationQuality.Default;
    [ObservableProperty] public partial bool BgmEnabled { get; set; }
    [ObservableProperty] public partial double BgmPercent { get; set; } = 12;

    // --- Hình & phụ đề ---
    [ObservableProperty] public partial string AspectRatio { get; set; } = "original";
    [ObservableProperty] public partial bool BlurEnabled { get; set; } = true;
    [ObservableProperty] public partial bool BlurOnlyDuringSubtitles { get; set; } = true;
    [ObservableProperty] public partial bool BlurOnText { get; set; }
    [ObservableProperty] public partial bool InpaintSubtitles { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InpaintQuality))]
    public partial bool InpaintFast { get; set; }

    /// <summary>Nút chọn "Chất lượng" (đối của InpaintFast).</summary>
    public bool InpaintQuality
    {
        get => !InpaintFast;
        set { if (value) InpaintFast = false; }
    }
    [ObservableProperty] public partial double BlurStrength { get; set; } = 25;
    [ObservableProperty] public partial double BlurFeather { get; set; } = 10;
    [ObservableProperty] public partial string BlurBoxText { get; set; } = "5,82,90,12";
    [ObservableProperty] public partial string FontName { get; set; } = "Arial";
    [ObservableProperty] public partial int FontSize { get; set; } = 33;
    [ObservableProperty] public partial string TextColor { get; set; } = "#FFFFFF";
    [ObservableProperty] public partial string BackColor { get; set; } = "#000000";
    [ObservableProperty] public partial bool BoxBackground { get; set; }
    [ObservableProperty] public partial bool Karaoke { get; set; } = true;
    [ObservableProperty] public partial string WordColor { get; set; } = "#FF6600";
    [ObservableProperty] public partial double SubtitleY { get; set; } = 82;
    [ObservableProperty] public partial double SubtitleLeft { get; set; } = 5;
    [ObservableProperty] public partial double SubtitleWidth { get; set; } = 90;
    [ObservableProperty] public partial string LogoFile { get; set; } = "";
    [ObservableProperty] public partial string LogoBoxText { get; set; } = "5,5,15,15";
    [ObservableProperty] public partial string CanvasFile { get; set; } = "";
    [ObservableProperty] public partial string CanvasBoxText { get; set; } = "0,0,100,100";
    [ObservableProperty] public partial string PipFile { get; set; } = "";
    [ObservableProperty] public partial string PipBoxText { get; set; } = "70,70,25,25";
    [ObservableProperty] public partial string OutroFile { get; set; } = "";

    // --- Chế độ "Chỉ phụ đề" ---
    [ObservableProperty] public partial bool MaskEnabled { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskBoxText))]
    public partial MaskRegionPreset MaskRegion { get; set; } = MaskRegionPreset.Bottom;

    /// <summary>Vùng che của preset theo % ("x,y,w,h"), lấy từ chính MaskBox.Preset để khung xem trước khớp lúc xuất.</summary>
    public string MaskBoxText
    {
        get
        {
            var b = MaskBox.Preset(MaskRegion.ToString(), 1000, 1000);
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b.X / 10.0:0.#},{b.Y / 10.0:0.#},{b.W / 10.0:0.#},{b.H / 10.0:0.#}");
        }
    }
    [ObservableProperty] public partial MaskStyle MaskStyle { get; set; } = MaskStyle.Blur;
    [ObservableProperty] public partial SubtitleStylePreset? SelectedStylePreset { get; set; }

    // --- Xem trước & kết quả ---
    [ObservableProperty] public partial string? PreviewFramePath { get; set; }
    [ObservableProperty] public partial double ScrubSeconds { get; set; } = 2;
    [ObservableProperty] public partial double ScrubMax { get; set; } = 600;

    private CancellationTokenSource? _scrub;
    [ObservableProperty] public partial string? OutputVideo { get; set; }
    [ObservableProperty] public partial string? OutputFolder { get; set; }

    public event EventHandler<string>? ResultReady;

    public async Task LoadAsync()
    {
        SourceLanguage = SourceLanguages[0];
        TargetLanguage = TargetLanguages[0];
        SelectedStylePreset = StylePresets.FirstOrDefault();
        var bgm = settings.Get<AudioBgmSettings>("audio.bgm");
        BgmEnabled = bgm.Enabled;
        BgmPercent = Math.Round(bgm.Volume * 100);
        for (var i = 0; i < BgmSlots.Count; i++) BgmSlots[i].Path = i < bgm.Files.Count ? bgm.Files[i] : "";
        await ReloadVoicesAsync();
    }

    partial void OnTargetLanguageChanged(LanguageChoice? value) => _ = ReloadVoicesAsync();

    partial void OnSelectedSourceChanged(string? value)
    {
        if (value is not null) _ = GuardAsync(() => LoadPreviewAsync(value));
    }

    /// <summary>Dán/kéo thả đường dẫn một file vào ô Nguồn thì dùng luôn file đó như khi bấm Chọn file.</summary>
    partial void OnInputPathChanged(string value)
    {
        var path = value.Trim();
        if (path == SelectedSource || !File.Exists(path)) return;
        Sources.Clear();
        Sources.Add(path);
        SelectedSource = path;
    }

    partial void OnScrubSecondsChanged(double value)
    {
        if (SelectedSource is not { } source) return;
        // Kéo thanh sinh rất nhiều giá trị; chỉ chụp khung khi người dùng dừng tay để không chạy ffmpeg liên tục.
        _scrub?.Cancel();
        _scrub = new CancellationTokenSource();
        var ct = _scrub.Token;
        _ = GuardAsync(async () =>
        {
            // Lượt bị lượt kéo sau thay thế: bỏ qua lặng lẽ, không để GuardAsync báo "Đã huỷ.".
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return; }
            await RefreshFrameAsync(source);
        });
    }

    private async Task LoadPreviewAsync(string source)
    {
        var info = await probe.ProbeAsync(source, CancellationToken.None);
        ScrubMax = Math.Max(0, Math.Floor(info.Duration.TotalSeconds - 0.1));
        if (ScrubSeconds > ScrubMax) ScrubSeconds = Math.Min(2, ScrubMax);
        if (info.Video is not null) await RefreshFrameAsync(source);
    }

    private async Task ReloadVoicesAsync()
    {
        var language = TargetLanguage?.Code ?? "vi";
        IReadOnlyList<VoiceInfo> list;
        try { list = await voices.ListAsync(language); }
        catch (ProviderException) { list = []; }

        var keep = SelectedVoice?.Id;
        Voices.Clear();
        foreach (var v in list) Voices.Add(v);
        var fallback = models.DefaultVoice(language).Voice;
        SelectedVoice = Voices.FirstOrDefault(v => v.Id == keep) ?? Voices.FirstOrDefault(v => v.Id == fallback) ?? Voices.FirstOrDefault();
    }

    private async Task RefreshFrameAsync(string source)
    {
        Directory.CreateDirectory(paths.CacheDir("frames"));
        var frame = Path.Combine(paths.CacheDir("frames"), $"s01-{Guid.NewGuid():N}.jpg");
        await preview.ExtractFrameAsync(source, TimeSpan.FromSeconds(ScrubSeconds), frame, CancellationToken.None);
        PreviewFramePath = frame;
    }

    // --- Chọn nguồn ---
    [RelayCommand]
    private void BrowseFile()
    {
        if (Dialogs.OpenFile("Chọn video hoặc audio", MediaFilter) is not { } file) return;
        InputPath = file;
        Sources.Clear();
        Sources.Add(file);
        SelectedSource = file;
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục chứa hàng loạt video") is { } folder) LoadFolder(folder, recursive: false);
    }

    [RelayCommand]
    private void ScanDeep()
    {
        if (!Directory.Exists(InputPath)) Dialogs.Info("Quét 2 lớp", "Hãy chọn một thư mục trước.");
        else LoadFolder(InputPath, recursive: true);
    }

    private void LoadFolder(string folder, bool recursive)
    {
        try
        {
            InputPath = folder;
            Sources.Clear();
            foreach (var f in LocalizeService.ScanSources(folder, recursive)) Sources.Add(f);
            Status = Sources.Count == 0 ? $"Không tìm thấy video nào trong: {folder}" : $"Tìm thấy {Sources.Count} video.";
            SelectedSource = Sources.FirstOrDefault();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private Task ClearCacheAsync() => GuardAsync(async () =>
    {
        if (IsRunning) throw new InvalidOperationException("Hãy đợi tác vụ kết thúc trước khi xoá bộ nhớ đệm.");
        var swept = await janitor.ClearAsync("asr");
        Status = swept.FilesDeleted == 0 ? "Bộ nhớ đệm nhận diện đang trống." : $"Đã xoá {swept.FilesDeleted} mục nhận diện.";
    });

    // --- Chạy ---
    [RelayCommand]
    private async Task RunSingleAsync()
    {
        if (CurrentSource() is not { } source) return;
        if (!TryBuild(out var options)) return;
        var job = await RunJobAsync(ct => SubtitlesOnly ? localize.StartSubtitlesAsync(source, options, ct) : localize.StartAsync(source, options, ct),
            SubtitlesOnly ? "Đang tạo phụ đề…" : "Đang lồng tiếng…");
        if (job is null) return;

        if ((PipelineResult.Value(job.Result, "video") ?? PipelineResult.Value(job.Result, "subtitleFile")) is { } output) ShowResult(output);
    }

    /// <summary>Kết quả của lần chạy vừa xong hoặc của job đã xong được chọn ở khung tác vụ.</summary>
    public void ShowResult(string output)
    {
        OutputVideo = output;
        OutputFolder = Path.GetDirectoryName(output);
        ResultReady?.Invoke(this, output);
    }

    [RelayCommand]
    private async Task RunAutoBatchAsync()
    {
        IReadOnlyList<string> files = Sources.Count > 0 ? [.. Sources] : CurrentSource() is { } one ? [one] : [];
        if (files.Count == 0 || !TryBuild(out var options)) return;
        if (!await Dialogs.ConfirmAsync("Xác nhận chạy hàng loạt", $"Đưa {files.Count} video vào hàng đợi?")) return;
        await GuardAsync(async () =>
        {
            using var owner = OwnJobs();
            var ids = await localize.StartBatchAsync(files, options);
            Status = $"Đã đưa {ids.Count} video vào hàng đợi (theo dõi ở khung Tác vụ của màn này).";
            Dialogs.Info("Đã đưa vào hàng đợi", Status);
        });
    }

    /// <summary>ASR thủ công: bóc phụ đề gốc, lưu vào dự án để chạy lồng tiếng (và S02) dùng lại.</summary>
    [RelayCommand]
    private async Task TranscribeAsync()
    {
        if (CurrentSource() is not { } source) return;
        var options = new LocalizeOptions
        {
            SourceLanguage = SourceLanguage?.Code ?? "auto", TargetLanguage = TargetLanguage?.Code ?? "vi", Translate = false, BurnSubtitles = false,
            ReuseCache = ReuseCache, UseEditedSubtitles = false, AsrModel = string.IsNullOrWhiteSpace(AsrModel) ? null : AsrModel.Trim(), Karaoke = Karaoke
        };
        var job = await RunJobAsync(ct => localize.StartSubtitlesAsync(source, options, ct), "Đang bóc phụ đề gốc…");
        if (job is null || PipelineResult.Value(job.Result, "segmentsFile") is not { } file) return;

        await GuardAsync(async () =>
        {
            var entries = await SubtitleEntries.ReadAsync(file);
            await subtitles.ApplyToProjectAsync(source, entries);
            SubPreview = string.Join(' ', entries.Take(3).Select(e => e.Text));
            Status = $"Đã bóc {entries.Count} câu. Mở S02 để chỉnh sửa trước khi lồng tiếng.";
        });
    }

    [RelayCommand]
    private Task TranslateSampleAsync() => GuardAsync(async () =>
    {
        SubPreview = await subtitles.TranslateLineAsync(SubPreview, TargetLanguage?.Code ?? "vi", ContextOrNull());
        Status = "Đã dịch câu mẫu.";
    });

    [RelayCommand]
    private Task PreviewVoiceAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var voice = SelectedVoice ?? throw new InvalidOperationException("Chưa chọn giọng đọc.");
        var language = TargetLanguage?.Code;
        var text = SubPreview.Trim();
        player.Play(text.Length == 0
            ? await voiceSpeed.PreviewAsync(voice.Id, language, Speed, ct)
            : await voiceSpeed.SynthesizeAsync(new TtsRequest(text, voice.Id, Path.Combine(paths.CacheDir("preview"), $"s01-{Guid.NewGuid():N}.wav"), language), Speed, ct));
    });

    /// <summary>Dừng cả khi giọng còn đang tạo (TTS chậm/treo): nút loa bị khoá trong lúc chờ nên nút dừng phải huỷ được lượt đó.</summary>
    [RelayCommand]
    private void StopAudio()
    {
        PreviewVoiceCommand.Cancel();
        player.Stop();
    }

    /// <summary>AI worker lấy mẫu khung tìm dải phụ đề cứng; điền vào vùng làm mờ (theo khung nguồn).</summary>
    [RelayCommand]
    private async Task LocateSubtitlesAsync()
    {
        if (CurrentSource() is not { } source) return;
        await RunDirectAsync(async (_, ct) =>
        {
            TextBandResult? found;
            try { found = await burned.LocateAsync(source, ct); }
            catch (AiWorkerException e) { throw new InvalidOperationException(e.Message, e); }
            if (found is null)
            {
                Status = "Không thấy phụ đề cứng lặp lại trong video.";
                Dialogs.Info("Tự tìm vùng phụ đề", Status);
                return;
            }

            var b = found.Box;
            BlurBoxText = string.Join(",", new[] { b.X, b.Y, b.W, b.H }.Select(v => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
            BlurEnabled = true;
            Status = $"Vùng phụ đề gốc: {BlurBoxText} (tin cậy {found.Confidence:P0}, có chữ ở {found.FramesWithText}/{found.FramesSampled} khung mẫu).";
            Dialogs.Info("Tự tìm vùng phụ đề", found.Confidence < 0.5 ? Status + " Độ tin cậy thấp, hãy xem lại khung xem trước." : Status);
        }, "Đang tìm vùng phụ đề gốc…");
    }

    [RelayCommand]
    private Task SnapshotAsync() => GuardAsync(async () =>
    {
        var source = CurrentSource() ?? throw new InvalidOperationException("Chưa chọn video nguồn.");
        await RefreshFrameAsync(source);
        if (Dialogs.SaveFile("Lưu ảnh bìa", "JPEG (*.jpg)|*.jpg", Path.GetFileNameWithoutExtension(source) + "_cover.jpg") is { } target)
        {
            await preview.ExtractFrameAsync(source, TimeSpan.FromSeconds(ScrubSeconds), target, CancellationToken.None);
            Status = $"Đã lưu ảnh bìa: {target}";
        }
    });

    [RelayCommand]
    private void ApplyStylePreset()
    {
        if (SelectedStylePreset is not { Style: var s }) return;
        FontName = s.FontName;
        FontSize = s.FontSize;
        TextColor = s.PrimaryColor;
        BackColor = s.BackColor;
        BoxBackground = s.BoxBackground;
    }

    /// <summary>Smart Audio: giảm tiếng gốc 6 dB, giữ giọng đọc 0 dB và bật ducking tự động.</summary>
    [RelayCommand]
    private void ApplySmartAudio()
    {
        OriginalDb = -6;
        VoiceDb = 0;
        SmartAudio = true;
    }

    [RelayCommand]
    private Task PickBgmAsync(BgmSlot slot) => GuardAsync(async () =>
    {
        if (Dialogs.OpenFile($"Chọn nhạc nền {slot.Index + 1}", "Audio (*.mp3;*.wav;*.m4a;*.aac)|*.mp3;*.wav;*.m4a;*.aac|Tất cả|*.*") is { } file)
        {
            slot.Path = file;
            await SaveBgmAsync();
        }
    });

    [RelayCommand]
    private Task ClearBgmAsync() => GuardAsync(async () =>
    {
        foreach (var slot in BgmSlots) slot.Path = "";
        await SaveBgmAsync();
    });

    [RelayCommand]
    private void Pick(string target)
    {
        if (Dialogs.OpenFile("Chọn tệp", AllFilter) is not { } file) return;
        switch (target)
        {
            case "logo": LogoFile = file; break;
            case "canvas": CanvasFile = file; break;
            case "pip": PipFile = file; break;
            case "outro": OutroFile = file; break;
        }
    }

    [RelayCommand]
    private void OpenOutput()
    {
        if (OutputVideo is not null) Guard(() => shell.OpenFile(OutputVideo));
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (OutputFolder is not null) Guard(() => shell.OpenFolder(OutputFolder));
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }

    private Task SaveBgmAsync() => settings.SetAsync("audio.bgm", new AudioBgmSettings
    {
        Files = [.. BgmSlots.Select(s => s.Path).Where(p => p.Length > 0)], Volume = BgmPercent / 100, Enabled = BgmEnabled
    });

    private string? CurrentSource()
    {
        var source = SelectedSource ?? (File.Exists(InputPath) ? InputPath : null);
        if (source is null) Dialogs.Error("Chọn một file video hoặc thư mục chứa video trước.");
        return source;
    }

    private string? ContextOrNull() => TranslationContext == Contexts[0] ? null : TranslationContext;

    private static double Linear(double db) => Math.Pow(10, db / 20);

    private bool TryBuild(out LocalizeOptions options)
    {
        try
        {
            options = BuildOptions();
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            options = new LocalizeOptions();
            Dialogs.Error(ex.Message);
            return false;
        }
    }

    /// <summary>Gom lựa chọn trên màn thành LocalizeOptions; cấu hình sai (màu, vùng %, giọng) báo lỗi trước khi xếp job.</summary>
    public LocalizeOptions BuildOptions()
    {
        if (!SubtitlesOnly && Dub && SelectedVoice is null) throw new InvalidOperationException("Chưa chọn giọng đọc.");
        var style = new SubtitleStyle(FontName, FontSize, Color(TextColor, "Màu chữ"), "#000000", 2, 40, "bottom", Color(BackColor, "Màu nền"),
            0.5, BoxBackground, true, SubtitleY, BoxLeft: SubtitleLeft, BoxWidth: SubtitleWidth);
        var selected = SelectedStylePreset?.Id ?? "CapCutYellow";

        return new LocalizeOptions
        {
            SourceLanguage = SourceLanguage?.Code ?? "auto",
            TargetLanguage = TargetLanguage?.Code ?? "vi",
            Context = ContextOrNull(),
            UseAsr = UseAsr, UseEditedSubtitles = UseEditedSubtitles, Translate = Translate, Dub = Dub, BurnSubtitles = BurnSubtitles,
            Mirror = Mirror, CutMeta = CutMeta, ReuseCache = ReuseCache, Denoise = DenoiseMode,
            AsrModel = string.IsNullOrWhiteSpace(AsrModel) ? null : AsrModel.Trim(),
            VoiceId = SelectedVoice?.Id ?? "", Speed = Speed, AutoFit = AutoFit, SkipFailedSentences = SkipFailedSentences,
            MuteOriginal = MuteOriginal, OriginalVolume = Linear(OriginalDb), VoiceVolume = Linear(VoiceDb), SmartAudio = SmartAudio, SeparateVocals = SeparateVocals, SeparationQuality = SeparationQuality,
            BgmEnabled = BgmEnabled, BgmFiles = [.. BgmSlots.Select(s => s.Path).Where(p => p.Length > 0)], BgmVolume = BgmPercent / 100,
            AspectRatio = AspectRatio,
            BlurBox = BlurEnabled ? ParseBox(BlurBoxText, "Vùng làm mờ") : null,
            BlurStrength = (int)BlurStrength, BlurFeather = (int)BlurFeather, BlurOnlyDuringSubtitles = BlurOnlyDuringSubtitles,
            BlurOnText = BlurOnText, InpaintSubtitles = InpaintSubtitles, InpaintFast = InpaintFast,
            Logo = Overlay(LogoFile, LogoBoxText, "Logo"), Canvas = Overlay(CanvasFile, CanvasBoxText, "Canvas"), Pip = Overlay(PipFile, PipBoxText, "PiP"),
            OutroFile = string.IsNullOrWhiteSpace(OutroFile) ? null : OutroFile,
            SubtitleStyle = style, Karaoke = Karaoke, KaraokeColor = Color(WordColor, "Màu chữ khi nói"),
            Mask = MaskEnabled, MaskRegion = MaskRegion, MaskStyle = MaskStyle,
            SubtitleStylePreset = selected, SubtitleFontSize = FontSize
        };
    }

    /// <summary>"x,y,w,h" theo % khung hình; ngoài 0–100 hoặc rộng/cao ≤ 0 là lỗi.</summary>
    public static PercentBox ParseBox(string text, string label)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || parts.Any(p => !double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
            throw new FormatException($"{label}: nhập 4 số x,y,w,h (%), ví dụ 5,82,90,12.");
        var v = parts.Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (v.Any(x => x < 0 || x > 100) || v[2] <= 0 || v[3] <= 0 || v[0] + v[2] > 100.001 || v[1] + v[3] > 100.001)
            throw new FormatException($"{label}: vùng phải nằm trong khung hình (0–100%).");
        return new PercentBox(v[0], v[1], v[2], v[3]);
    }

    private static OverlayAsset? Overlay(string file, string box, string label) =>
        string.IsNullOrWhiteSpace(file) ? null : new OverlayAsset(file, ParseBox(box, label));

    private static string Color(string value, string label) =>
        Hex.IsMatch(value.Trim()) ? value.Trim() : throw new FormatException($"{label}: nhập mã màu dạng #RRGGBB.");
}
