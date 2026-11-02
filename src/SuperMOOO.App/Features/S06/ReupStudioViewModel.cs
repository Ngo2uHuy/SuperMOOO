using System.Collections.ObjectModel;
using System.IO;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Tts;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S01;
using SuperMOOO.App.Features.S02;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Media.Tools;
using SuperMOOO.Social.Download;
using SuperMOOO.Workflows;
using SuperMOOO.Workflows.Localize;
using SuperMOOO.Workflows.Reup;
using SuperMOOO.Workflows.Subtitles;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S06;

public sealed record DownloadedVideo(string Path, string Name, string Source);

/// <summary>S06: preset + hiệu ứng reup, giọng đọc kịch bản, xem trước đúng filter graph xuất (D2), xuất đơn/hàng loạt qua pipeline `reup`.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class ReupStudioViewModel(ReupStudioService reup, SubtitleEditorViewModel subtitleEditor, VoiceCatalogService voices,
    MediaToolsService tools, MediaLibraryService library, IMediaProbe probe, EncoderDetector encoders, VoiceEffectPresets effects, SubtitleStylePresets stylePresets,
    ShellService shell, JobTracker tracker, IDialogService dialogs, Features.S17.TimelineNavigation timeline) : JobViewModelBase(tracker, dialogs)
{
    protected override string JobsOwner => JobOwner.Screen("S06");

    [RelayCommand]
    private Task EditTimelineAsync() => GuardAsync(async () =>
    {
        if (IsRunning) return;
        var profile = BuildProfile();
        var source = await timeline.VideoAsync(Selected(), profile.Video.TrimStart?.TotalSeconds, profile.Video.TrimEnd?.TotalSeconds,
            profile.Audio.OriginalVolume, profile.Audio.MuteOriginal);
        var clips = new List<TimelineClip> { source };
        await timeline.AddAudioAsync(clips, profile.Audio.VoiceoverPath, TimelineTrack.Voiceover, source.Duration,
            profile.Audio.VoiceoverOffset.TotalSeconds, profile.Audio.VoiceoverVolume);
        await timeline.AddAudioAsync(clips, profile.Audio.BgmPath, TimelineTrack.Music, source.Duration, 0, profile.Audio.BgmVolume, profile.Audio.BgmLoop);
        if (profile.Subtitles is { } subtitles)
            foreach (var cue in subtitles.Document.Cues)
            {
                var offset = subtitles.TimeBase == "Source" ? source.In : 0;
                var start = Math.Max(0, cue.Start.TotalSeconds - offset);
                var end = Math.Min(source.Duration, cue.End.TotalSeconds - offset);
                if (end - start >= TimelineEditing.MinDuration)
                    clips.Add(new TimelineClip(Guid.NewGuid(), TimelineTrack.Subtitle, "", start, 0, end - start, 0, cue.Text));
            }
        await timeline.OpenAsync(new TimelineProject(1, Path.GetFileNameWithoutExtension(source.Path), clips.ToArray(),
            Settings.Aspect is "16:9" or "9:16" or "1:1" ? Settings.Aspect : "Original", Settings.Height, Settings.Fps),
            "Đã chuyển video nguồn, mốc cắt, audio và phụ đề. Các hiệu ứng hình ảnh, tốc độ, logo và xử lý giọng Reup không chuyển sang Timeline.");
    });

    private static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".ts" };

    public static IReadOnlyList<string> Aspects { get; } = ["Original", "9:16", "16:9", "1:1", "4:5", "3:4"];
    public static IReadOnlyList<string> Fits { get; } = ["Contain", "Cover"];
    public static IReadOnlyList<string> LogoPositions { get; } = ["TL", "TR", "BL", "BR", "Center"];
    public static IReadOnlyList<string> DenoiseModes { get; } = ["Off", "Fast", "Ai"];
    public static IReadOnlyList<string> SeparationQualities { get; } = [Core.Models.SeparationQuality.Roformer, Core.Models.SeparationQuality.Mdx];
    public static IReadOnlyList<int> Heights { get; } = [360, 480, 720, 1080, 1440, 2160];
    public static IReadOnlyList<int> FpsChoices { get; } = [24, 25, 30, 60];
    public static IReadOnlyList<string> VoiceLongerModes { get; } = ["FreezeLastFrame", "LoopVideo", "CutVoice"];
    public static IReadOnlyList<string> TimeBases { get; } = ["Source", "Output"];

    public ReupSettingsModel Settings { get; } = new();
    public SubtitleEditorViewModel Subtitles => subtitleEditor;
    public IReadOnlyList<ReupPreset> Presets => reup.Presets;
    public IReadOnlyList<VoiceEffect> Effects => effects.All;
    public IReadOnlyList<SubtitleStylePreset> StylePresets => stylePresets.All;
    public IReadOnlyList<LanguageChoice> Languages { get; } = [.. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];
    public ObservableCollection<string> Sources { get; } = [];
    public ObservableCollection<DownloadedVideo> Downloaded { get; } = [];
    public ObservableCollection<string> Encoders { get; } = [];
    public ObservableCollection<string> EncodePresets { get; } = [];
    public ObservableCollection<VoiceInfo> Voices { get; } = [];

    [ObservableProperty] public partial string? SelectedSource { get; set; }
    [ObservableProperty] public partial string DownloadedFilter { get; set; } = "";
    [ObservableProperty] public partial string InfoText { get; set; } = "";
    [ObservableProperty] public partial ReupPreset? SelectedPreset { get; set; }
    [ObservableProperty] public partial SubtitleStylePreset? SelectedStylePreset { get; set; }
    [ObservableProperty] public partial LanguageChoice? Language { get; set; }
    [ObservableProperty] public partial VoiceInfo? SelectedVoice { get; set; }
    [ObservableProperty] public partial string ScriptText { get; set; } = "";
    [ObservableProperty] public partial double VoiceSpeed { get; set; } = 1.0;
    [ObservableProperty] public partial double VoicePitch { get; set; }
    [ObservableProperty] public partial double ScrubSeconds { get; set; }
    [ObservableProperty] public partial string? PreviewFramePath { get; set; }
    [ObservableProperty] public partial string OutputFolder { get; set; } = "";
    [ObservableProperty] public partial string? OutputVideo { get; set; }

    public event EventHandler<string>? PreviewClipReady;

    public async Task LoadAsync()
    {
        subtitleEditor.ShowTranslation = false;
        subtitleEditor.JobsScreen = "S06";
        subtitleEditor.Initialize();
        Language ??= Languages[0];
        SelectedPreset ??= Presets.FirstOrDefault();
        SelectedStylePreset ??= StylePresets.FirstOrDefault();
        if (SelectedPreset is not null) LoadProfile(reup.DefaultProfile(SelectedPreset.Id));

        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReupSettingsModel.Encoder)) RefreshEncodePresets();
        };
        await GuardAsync(async () =>
        {
            foreach (var e in await encoders.AvailableAsync(CancellationToken.None)) Encoders.Add(e.Key);
            RefreshEncodePresets();
            var list = await voices.ListAsync(Language.Code);
            foreach (var v in list) Voices.Add(v);
            SelectedVoice = Voices.FirstOrDefault();
        });
        await LoadDownloadedAsync();
    }

    partial void OnSelectedSourceChanged(string? value)
    {
        if (value is not null) _ = GuardAsync(() => ShowInfoAsync(value));
    }

    private async Task ShowInfoAsync(string path)
    {
        var info = await probe.ProbeAsync(path, CancellationToken.None);
        var video = info.Video is null ? "chỉ âm thanh" : $"{info.Video.Width}×{info.Video.Height} · {info.Video.Fps:0.##} fps";
        InfoText = $"{video} · {info.Duration:hh\\:mm\\:ss} · {(info.Audio is null ? "không có âm thanh" : $"{info.Audio.SampleRate} Hz")}";
    }

    private void RefreshEncodePresets()
    {
        var profile = EncoderProfile.For(Settings.Encoder);
        EncodePresets.Clear();
        foreach (var p in profile.Presets) EncodePresets.Add(p);
        Settings.EncodePreset = profile.NormalizePreset(Settings.EncodePreset);
    }

    private void LoadProfile(ReupEffectProfile profile)
    {
        Settings.Load(profile);
        subtitleEditor.Load(profile.Subtitles is { } s ? SubtitleEntries.FromDocument(s.Document) : []);
        RefreshEncodePresets();
    }

    private ReupEffectProfile BuildProfile() => Settings.Build(SubtitleEntries.ToDocument(subtitleEditor.Entries, useTranslation: false));

    /// <summary>Nguồn thêm bằng nút hoặc kéo-thả: file đơn → xem thử/xuất; nhiều file → xử lý hàng loạt.</summary>
    public void AddSources(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var files = Directory.Exists(path) ? LocalizeService.ScanSources(path, recursive: false) : [path];
            foreach (var f in files.Where(f => File.Exists(f) && !Sources.Contains(f))) Sources.Add(f);
        }

        SelectedSource ??= Sources.FirstOrDefault();
    }

    [RelayCommand]
    private void AddFile()
    {
        if (Dialogs.OpenFile("Chọn video", "Video|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.ts|Tất cả|*.*") is { } file) AddSources([file]);
    }

    [RelayCommand]
    private void AddFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục video") is { } folder) AddSources([folder]);
    }

    /// <summary>Video đã tải ở S09/S10/S11 (kho media_file kind download), chỉ file video còn trên đĩa.</summary>
    [RelayCommand]
    private Task LoadDownloadedAsync() => GuardAsync(async () =>
    {
        var text = string.IsNullOrWhiteSpace(DownloadedFilter) ? null : DownloadedFilter.Trim();
        var rows = await library.HistoryAsync(text);
        Downloaded.Clear();
        foreach (var r in rows.Where(r => r.Kind == "download" && VideoExtensions.Contains(Path.GetExtension(r.Path)) && File.Exists(r.Path)))
            Downloaded.Add(new DownloadedVideo(r.Path, Path.GetFileNameWithoutExtension(r.Path), r.SourceUrl ?? r.Path));
    });

    partial void OnDownloadedFilterChanged(string value) => LoadDownloadedCommand.Execute(null);

    [RelayCommand]
    private void AddDownloaded(System.Collections.IList? selected)
    {
        var paths = selected?.OfType<DownloadedVideo>().Select(v => v.Path).ToList() ?? [];
        if (paths.Count == 0) { Dialogs.Error("Chọn một hoặc nhiều video đã tải."); return; }
        AddSources(paths);
        Status = $"Đã thêm {paths.Count} video vào danh sách nguồn.";
    }

    [RelayCommand]
    private void RemoveSource()
    {
        if (SelectedSource is null) return;
        var index = Sources.IndexOf(SelectedSource);
        Sources.Remove(SelectedSource);
        SelectedSource = Sources.Count == 0 ? null : Sources[Math.Min(index, Sources.Count - 1)];
    }

    /// <summary>Preset thay toàn bộ thông số hiệu ứng (F11); chỉ giữ logo, phụ đề, intro/outro, giọng đọc và nhạc nền.</summary>
    [RelayCommand]
    private Task ApplyPresetAsync() => GuardAsync(() =>
    {
        if (SelectedPreset is null) return Task.CompletedTask;
        LoadProfile(reup.ApplyPreset(SelectedPreset.Id, BuildProfile()));
        Status = $"Đã áp preset {SelectedPreset.Name}.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private void ApplyStylePreset()
    {
        if (SelectedStylePreset is not { Style: var s }) return;
        (Settings.FontName, Settings.FontSize, Settings.TextColor, Settings.OutlineColor) = (s.FontName, s.FontSize, s.PrimaryColor, s.OutlineColor);
        (Settings.Outline, Settings.BoxBackground) = (s.Outline, s.BoxBackground);
        if (s.PositionY is { } y) Settings.PositionY = y;
    }

    [RelayCommand]
    private void Pick(string target)
    {
        var filter = target is "logo" ? "Ảnh|*.png;*.jpg;*.jpeg;*.webp|Tất cả|*.*"
            : target is "bgm" or "voiceover" ? "Audio|*.mp3;*.wav;*.m4a;*.aac;*.flac|Tất cả|*.*"
            : "Video|*.mp4;*.mkv;*.mov;*.avi;*.webm|Tất cả|*.*";
        if (Dialogs.OpenFile("Chọn tệp", filter) is not { } file) return;
        switch (target)
        {
            case "logo": Settings.LogoPath = file; break;
            case "bgm": Settings.BgmPath = file; break;
            case "voiceover": Settings.VoiceoverPath = file; break;
            case "intro": Settings.IntroPath = file; break;
            case "outro": Settings.OutroPath = file; break;
        }
    }

    [RelayCommand]
    private void ClearFile(string target)
    {
        switch (target)
        {
            case "logo": Settings.LogoPath = ""; break;
            case "bgm": Settings.BgmPath = ""; break;
            case "voiceover": Settings.VoiceoverPath = ""; break;
            case "intro": Settings.IntroPath = ""; break;
            case "outro": Settings.OutroPath = ""; break;
        }
    }

    /// <summary>R06: tách audio của video đang chọn ra MP3.</summary>
    [RelayCommand]
    private Task ExtractAudioAsync() => RunDirectAsync(async (progress, ct) =>
    {
        var source = Selected();
        var result = await tools.ExtractAudioAsync(source, AudioFormat.Mp3, "192k", null, progress, ct);
        Status = $"Đã tách audio: {result.Path}";
    }, "Đang tách audio…");

    /// <summary>R09: TTS từng câu → cue đúng thời lượng thật. Offset chốt ngay lúc bấm; có phụ đề sẵn thì hỏi Thay/Thêm (F10).</summary>
    [RelayCommand]
    private async Task GenerateVoiceoverAsync()
    {
        if (SelectedVoice is null) { Dialogs.Error("Chưa chọn giọng đọc."); return; }
        if (string.IsNullOrWhiteSpace(ScriptText)) { Dialogs.Error("Nhập kịch bản cần đọc."); return; }

        var offset = TimeSpan.FromSeconds(Settings.VoiceoverOffset);
        var merge = subtitleEditor.Entries.Count == 0 ? SubtitleMerge.Replace
            : await Dialogs.ConfirmAsync("Đã có phụ đề", "Thay phụ đề hiện có bằng phụ đề tự sinh, hay thêm vào cuối?", "Thay phụ đề", "Thêm vào cuối")
                ? SubtitleMerge.Replace : SubtitleMerge.Append;
        var voice = new ReupVoiceover(ScriptText, SelectedVoice.Id, Language?.Code, VoiceSpeed, VoicePitch);

        await RunDirectAsync(async (progress, ct) =>
        {
            var result = await reup.GenerateVoiceoverAsync(voice, progress, ct);
            LoadProfile(ReupStudioService.ApplyVoiceover(BuildProfile(), result, offset, merge));
            Status = $"Đã tạo giọng đọc ({result.Duration:mm\\:ss}) và {result.Cues.Count} phụ đề khớp thời lượng.";
            foreach (var w in result.Warnings) Log.Add("Cảnh báo: " + w);
        }, "Đang tạo giọng đọc…");
    }

    [RelayCommand]
    private Task PreviewClipAsync() => RunDirectAsync(async (progress, ct) =>
    {
        var result = await reup.PreviewClipAsync(Selected(), BuildProfile(), TimeSpan.FromSeconds(ScrubSeconds), 6, progress, ct);
        PreviewClipReady?.Invoke(this, result.Path);
        Status = $"Xem trước {result.Duration.TotalSeconds:0.0} s bằng đúng bộ lọc xuất.";
    }, "Đang dựng đoạn xem trước…");

    [RelayCommand]
    private Task PreviewFrameAsync() => RunDirectAsync(async (_, ct) =>
        PreviewFramePath = await reup.PreviewFrameAsync(Selected(), BuildProfile(), TimeSpan.FromSeconds(ScrubSeconds), ct), "Đang dựng khung hình…");

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SelectedSource is not { } source) { Dialogs.Error("Chọn một video nguồn trước."); return; }
        if (!TryProfile(out var profile)) return;
        var job = await RunJobAsync(ct => reup.StartExportAsync(new ReupExportRequest(source, profile, FolderOrNull()), ct), "Đang xuất video…");
        if (job is not null) OutputVideo = PipelineResult.Value(job.Result, "video");
    }

    [RelayCommand]
    private Task ExportBatchAsync() => GuardAsync(async () =>
    {
        if (Sources.Count == 0) throw new InvalidOperationException("Chưa thêm video nào.");
        if (!TryProfile(out var profile) || !await Dialogs.ConfirmAsync("Xuất hàng loạt", $"Xuất {Sources.Count} video với cùng thiết lập?")) return;
        using var owner = OwnJobs();
        var ids = await reup.StartBatchAsync([.. Sources], profile, FolderOrNull());
        Status = $"Đã đưa {ids.Count} video vào hàng đợi (theo dõi ở khung Tác vụ của màn này).";
        Dialogs.Info("Đã đưa vào hàng đợi", Status);
    });

    [RelayCommand]
    private void ChooseOutputFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục xuất") is { } folder) OutputFolder = folder;
    }

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

    private string? FolderOrNull() => string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder;

    private string Selected() => SelectedSource ?? throw new InvalidOperationException("Chọn một video nguồn trước.");

    private bool TryProfile(out ReupEffectProfile profile)
    {
        try
        {
            profile = BuildProfile();
            return true;
        }
        catch (FormatException ex)
        {
            profile = null!;
            Dialogs.Error(ex.Message);
            return false;
        }
    }

    private void Open(Action action)
    {
        try { action(); }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }
}
