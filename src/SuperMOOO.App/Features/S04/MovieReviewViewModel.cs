using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;
using SuperMOOO.AI.Tts;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S01;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Workflows;
using SuperMOOO.Workflows.Review;
using SuperMOOO.Workflows.Subtitles;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S04;

/// <summary>Một cảnh trên bảng sửa: mốc "HH:MM:SS - HH:MM:SS", lời bình, thoại (đọc bằng giọng thứ hai).</summary>
public sealed partial class SceneRow(string time, string narration, string dialog = "", string caption = "") : ObservableObject
{
    private static readonly Regex Interval = new(@"^\s*(\d+:\d{2}:\d{2}(?:\.\d+)?)\s*-\s*(\d+:\d{2}:\d{2}(?:\.\d+)?)\s*$");

    public string Caption { get; } = caption;

    [ObservableProperty] public partial string Time { get; set; } = time;
    [ObservableProperty] public partial string Narration { get; set; } = narration;
    [ObservableProperty] public partial string Dialog { get; set; } = dialog;

    public static SceneRow From(ReviewScene scene) => new($"{Clock(scene.Start)} - {Clock(scene.End)}", scene.Narration, scene.Dialog, scene.Caption);

    public ReviewScene ToScene()
    {
        var match = Interval.Match(Time);
        if (!match.Success) throw new FormatException("Mốc cảnh phải có dạng HH:MM:SS - HH:MM:SS.");
        var (start, end) = (Seconds(match.Groups[1].Value), Seconds(match.Groups[2].Value));
        if (end <= start) throw new FormatException("Thời điểm kết thúc cảnh phải sau thời điểm bắt đầu.");
        return new ReviewScene(start, end, Caption, Narration.Trim(), Dialog.Trim());
    }

    private static double Seconds(string text)
    {
        var p = text.Split(':');
        var (h, m, s) = (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture));
        if (m >= 60 || s >= 60) throw new FormatException("Mốc thời gian không hợp lệ.");
        return h * 3600 + m * 60 + s;
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds + t.Milliseconds / 1000.0:00.###}");
    }
}

public sealed record ReviewModeChoice(string Value, string Name);

/// <summary>S04: chọn phim, AI viết lời bình theo phong cách, sửa từng cảnh rồi xuất; hoặc chạy tự động toàn dự án / cả thư mục phim.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class MovieReviewViewModel(MovieReviewService review, VoiceCatalogService voices, IMediaProbe probe, IAudioPlayer player,
    ShellService shell, JobTracker tracker, IDialogService dialogs, Features.S17.TimelineNavigation timeline,
    LlmProviderRegistry llm, ISettingsStore settings) : JobViewModelBase(tracker, dialogs)
{
    protected override string JobsOwner => JobOwner.Screen("S04");
    public LlmPickerModel ScriptAi { get; } = new(llm, settings, dialogs);

    private const string MovieFilter = "Video|*.mp4;*.mkv;*.mov;*.avi;*.ts;*.webm|Tất cả|*.*";
    private static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$");
    private IReadOnlyList<SubtitleEntry>? _transcript;
    private string? _transcriptFor;

    public static IReadOnlyList<ReviewModeChoice> Modes { get; } =
        [new("review", "Review có nhận xét"), new("summary", "Tóm tắt theo diễn biến"), new("highlights", "Các điểm nổi bật")];

    public static IReadOnlyList<string> Aspects { get; } = ["Original", "9:16", "16:9", "1:1", "4:5", "3:4"];
    public static IReadOnlyList<int> Heights { get; } = [360, 480, 720, 1080];
    public IReadOnlyList<LanguageChoice> Languages { get; } = [.. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];
    public IReadOnlyList<ScriptStyle> Styles => review.Styles;
    public ObservableCollection<string> KnownMovies { get; } = [];
    public ObservableCollection<SceneRow> Scenes { get; } = [];
    public ObservableCollection<VoiceInfo> Voices { get; } = [];

    // --- Phim & kịch bản ---
    [ObservableProperty] public partial string MoviePath { get; set; } = "";
    [ObservableProperty] public partial string MovieInfo { get; set; } = "";
    [ObservableProperty] public partial string SrtPath { get; set; } = "";
    [ObservableProperty] public partial ScriptStyle? SelectedStyle { get; set; }
    [ObservableProperty] public partial int NumScenes { get; set; } = 4;
    [ObservableProperty] public partial int ScenePace { get; set; } = 25;
    [ObservableProperty] public partial LanguageChoice? Language { get; set; }
    [ObservableProperty] public partial SceneRow? SelectedScene { get; set; }

    // --- Giọng & hiển thị ---
    [ObservableProperty] public partial VoiceInfo? NarratorVoice { get; set; }
    [ObservableProperty] public partial VoiceInfo? DialogVoice { get; set; }
    [ObservableProperty] public partial double Speed { get; set; } = 1.0;
    [ObservableProperty] public partial string CaptionColor { get; set; } = "#FFCC00";
    [ObservableProperty] public partial bool CardCaptions { get; set; } = true;
    [ObservableProperty] public partial bool Captions { get; set; } = true;
    [ObservableProperty] public partial double Fade { get; set; } = 0.25;
    [ObservableProperty] public partial string Aspect { get; set; } = "Original";
    [ObservableProperty] public partial int Height { get; set; } = 720;
    [ObservableProperty] public partial double Volume { get; set; } = 1.0;
    [ObservableProperty] public partial string MusicPath { get; set; } = "";
    [ObservableProperty] public partial double MusicVolume { get; set; } = 0.12;
    [ObservableProperty] public partial bool EachSceneOwnFile { get; set; }
    [ObservableProperty] public partial string OutputFolder { get; set; } = "";

    // --- Chạy tự động ---
    [ObservableProperty] public partial ReviewModeChoice AutoMode { get; set; } = Modes[0];
    [ObservableProperty] public partial int TargetSeconds { get; set; } = 60;
    [ObservableProperty] public partial bool UseAi { get; set; } = true;
    [ObservableProperty] public partial bool Narrate { get; set; } = true;
    [ObservableProperty] public partial bool Translate { get; set; }
    [ObservableProperty] public partial bool SplitEvenly { get; set; }
    [ObservableProperty] public partial int SegmentSeconds { get; set; } = 30;

    [ObservableProperty] public partial string? OutputVideo { get; set; }

    public event EventHandler<string>? ResultReady;

    [RelayCommand]
    private Task EditTimelineAsync() => GuardAsync(async () =>
    {
        if (IsRunning) return;
        var source = await timeline.VideoAsync(MoviePath, volume: Volume);
        var clips = new List<TimelineClip>();
        double position = 0;
        foreach (var row in Scenes)
        {
            var scene = row.ToScene();
            var clip = source with { Id = Guid.NewGuid(), Start = position, In = scene.Start, Out = scene.End, Text = scene.Narration };
            clips.Add(clip);
            if (Captions && !string.IsNullOrWhiteSpace(scene.Caption))
                clips.Add(new TimelineClip(Guid.NewGuid(), TimelineTrack.Subtitle, "", position, 0, clip.Duration, 0, scene.Caption));
            position += clip.Duration;
        }
        if (clips.Count == 0) { clips.Add(source); position = source.Duration; }
        await timeline.AddAudioAsync(clips, MusicPath, TimelineTrack.Music, position, 0, MusicVolume, loop: true);
        await timeline.OpenAsync(new TimelineProject(1, Path.GetFileNameWithoutExtension(MoviePath), clips.ToArray(),
            Aspect is "16:9" or "9:16" or "1:1" ? Aspect : "Original", Height),
            "Đã chuyển cảnh nguồn và phụ đề. Lời bình được giữ ở ghi chú clip; thêm file voiceover để phát lời bình.");
    });

    public async Task LoadAsync()
    {
        SelectedStyle ??= Styles.FirstOrDefault();
        Language ??= Languages.FirstOrDefault(l => l.Code == "vi") ?? Languages[0];
        await GuardAsync(async () =>
        {
            foreach (var p in await review.ScanProjectsAsync())
                if (Workflows.Projects.ProjectIndex.SourceOf(p) is { } source && File.Exists(source)) KnownMovies.Add(source);
            foreach (var v in await voices.ListAsync(Language.Code)) Voices.Add(v);
            NarratorVoice ??= Voices.FirstOrDefault(v => v.Gender == "female") ?? Voices.FirstOrDefault();
            DialogVoice ??= Voices.FirstOrDefault(v => v.Gender == "male") ?? NarratorVoice;
        });
    }

    partial void OnMoviePathChanged(string value)
    {
        _transcript = null;
        MovieInfo = "";
        if (File.Exists(value)) _ = GuardAsync(() => ShowInfoAsync(value));
    }

    private async Task ShowInfoAsync(string path)
    {
        var info = await probe.ProbeAsync(path, CancellationToken.None);
        MovieInfo = info.Video is null ? "Không có hình ảnh video." : $"{info.Video.Width}×{info.Video.Height} · {info.Duration:hh\\:mm\\:ss}";
    }

    public void AddMovies(IEnumerable<string> paths)
    {
        if (paths.FirstOrDefault() is { } first) MoviePath = first;
    }

    [RelayCommand]
    private void BrowseMovie()
    {
        if (Dialogs.OpenFile("Chọn video phim", MovieFilter) is { } file) MoviePath = file;
    }

    [RelayCommand]
    private void BrowseSrt()
    {
        if (Dialogs.OpenFile("Chọn file phụ đề SRT", "SRT (*.srt)|*.srt") is { } file) SrtPath = file;
    }

    [RelayCommand]
    private void ClearSrt() => SrtPath = "";

    [RelayCommand]
    private void PickMusic()
    {
        if (Dialogs.OpenFile("Chọn nhạc nền", "Audio|*.mp3;*.wav;*.m4a;*.aac;*.flac|Tất cả|*.*") is { } file) MusicPath = file;
    }

    [RelayCommand]
    private void ClearMusic() => MusicPath = "";

    [RelayCommand]
    private void ChooseOutputFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục xuất") is { } folder) OutputFolder = folder;
    }

    /// <summary>AI viết lời bình; chưa có thoại thì nhận diện (hoặc đọc SRT) trước. Thoại được giữ lại cho lần tạo kế tiếp.</summary>
    [RelayCommand]
    private async Task GenerateScriptAsync()
    {
        var movie = RequireMovie();
        if (movie is null || SelectedStyle is null) return;
        if (Scenes.Count > 0 && !await Dialogs.ConfirmAsync("Thay kịch bản", "Các cảnh hiện tại sẽ bị thay bằng kịch bản AI mới. Tiếp tục?", "Thay", "Giữ lại")) return;

        await RunDirectAsync(async (progress, ct) =>
        {
            if (_transcript is null || _transcriptFor != movie)
            {
                Status = "Đang nhận diện thoại phim…";
                _transcript = await review.TranscribeAsync(movie, SrtPath.Length > 0 ? SrtPath : null, null, progress, ct);
                _transcriptFor = movie;
            }

            Status = "AI đang viết lời bình…";
            var info = await probe.ProbeAsync(movie, ct);
            var scenes = await review.GenerateScriptAsync(Path.GetFileNameWithoutExtension(movie), _transcript, SelectedStyle.Label, NumScenes, ScenePace,
                Language?.Code ?? "vi", info.Duration.TotalSeconds, ct, ScriptAi.ProviderId, ScriptAi.Model);
            Scenes.Clear();
            foreach (var s in scenes) Scenes.Add(SceneRow.From(s));
            SelectedScene = Scenes.FirstOrDefault();
            Status = $"AI đã viết {Scenes.Count} cảnh; sửa lời bình rồi bấm Xuất review.";
        }, "Đang tạo kịch bản…");
    }

    [RelayCommand]
    private void AddScene()
    {
        var start = Scenes.Count == 0 ? "00:00:00" : Scenes[^1].Time.Split('-')[^1].Trim();
        var row = new SceneRow($"{start} - {Next(start, ScenePace)}", "");
        Scenes.Add(row);
        SelectedScene = row;
    }

    [RelayCommand]
    private void DeleteScene(SceneRow? row)
    {
        row ??= SelectedScene;
        if (row is null) return;
        var index = Scenes.IndexOf(row);
        Scenes.Remove(row);
        SelectedScene = Scenes.Count == 0 ? null : Scenes[Math.Min(index, Scenes.Count - 1)];
    }

    [RelayCommand]
    private Task PreviewNarrationAsync(SceneRow? row) => RunDirectAsync(async (_, ct) =>
    {
        row ??= SelectedScene;
        var voice = NarratorVoice ?? throw new InvalidOperationException("Chưa chọn giọng dẫn chuyện.");
        player.Play(await review.PreviewNarrationAsync(row?.Narration ?? "", voice.Id, Language?.Code, Speed, ct));
    }, "Đang đọc thử lời bình…");

    [RelayCommand]
    private async Task ExportAsync()
    {
        var movie = RequireMovie();
        if (movie is null || !TryOptions(out var options)) return;
        if (!TryScenes(out var scenes)) return;
        var request = new ReviewExportRequest(movie, Path.GetFileNameWithoutExtension(movie), scenes, options);

        var job = await RunJobAsync(ct => review.ExportAsync(request, ct), "Đang xuất video review…");
        if (job is null) return;
        OutputVideo = PipelineResult.Value(job.Result, "video");
        if (OutputVideo is not null && !EachSceneOwnFile) ResultReady?.Invoke(this, OutputVideo);
    }

    /// <summary>Tự động toàn bộ: nhận diện → chọn cảnh + lời bình → TTS → dựng, không cần sửa tay.</summary>
    [RelayCommand]
    private async Task RunAutoAsync()
    {
        var movie = RequireMovie();
        if (movie is null || !TryOptions(out var options)) return;
        var job = await RunJobAsync(ct => review.StartAsync(movie, options, ct), "Đang chạy tự động…");
        if (job is null) return;
        OutputVideo = PipelineResult.Value(job.Result, "video");
        if (OutputVideo is not null && !EachSceneOwnFile) ResultReady?.Invoke(this, OutputVideo);
    }

    [RelayCommand]
    private Task RunFolderAsync() => GuardAsync(async () =>
    {
        if (!TryOptions(out var options) || Dialogs.PickFolder("Chọn thư mục chứa các phim") is not { } folder) return;
        var movies = Workflows.Localize.LocalizeService.ScanSources(folder, recursive: false).Where(f => !f.EndsWith(".mp3") && !f.EndsWith(".wav")).ToList();
        if (movies.Count == 0) throw new InvalidOperationException("Thư mục không có video nào.");
        if (!await Dialogs.ConfirmAsync("Chạy hàng loạt", $"Chạy tự động {movies.Count} phim với cùng thiết lập?")) return;
        options.OutputFolder = null;
        using var owner = OwnJobs();
        foreach (var movie in movies) await review.StartAsync(movie, options);
        Status = $"Đã đưa {movies.Count} phim vào hàng đợi (theo dõi ở khung Tác vụ của màn này).";
        Dialogs.Info("Đã đưa vào hàng đợi", Status);
    });

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

    private string? RequireMovie()
    {
        if (File.Exists(MoviePath)) return MoviePath;
        Dialogs.Error("Chọn video phim nguồn hợp lệ trước.");
        return null;
    }

    private bool TryScenes(out IReadOnlyList<ReviewScene> scenes)
    {
        scenes = [];
        if (Scenes.Count == 0) { Dialogs.Error("Chưa có cảnh nào: bấm Tạo kịch bản AI hoặc Thêm cảnh."); return false; }
        try
        {
            scenes = [.. Scenes.Select(s => s.ToScene())];
            return true;
        }
        catch (FormatException ex)
        {
            Dialogs.Error(ex.Message);
            return false;
        }
    }

    private bool TryOptions(out ReviewOptions options)
    {
        options = null!;
        if (!Hex.IsMatch(CaptionColor)) { Dialogs.Error("Màu phụ đề phải có dạng #RRGGBB."); return false; }
        if (Narrate && NarratorVoice is null) { Dialogs.Error("Chưa chọn giọng dẫn chuyện."); return false; }
        options = new ReviewOptions
        {
            Mode = AutoMode.Value, TargetSeconds = TargetSeconds, Ai = UseAi, Style = SelectedStyle?.Label ?? "Kịch tính", Language = Language?.Code ?? "vi",
            ProviderId = ScriptAi.ProviderId, Model = ScriptAi.Model,
            Translate = Translate, Narration = Narrate, VoiceId = NarratorVoice?.Id ?? "", DialogVoiceId = DialogVoice?.Id, Speed = Speed, Aspect = Aspect,
            Height = Height, Fade = Fade, Captions = Captions, CaptionColor = CaptionColor, CardCaptions = CardCaptions, Volume = Volume,
            MusicPath = MusicPath.Length > 0 ? MusicPath : null, MusicVolume = MusicVolume,
            ExportMode = EachSceneOwnFile ? ExportMode.Individual : ExportMode.Montage, SegmentSeconds = SplitEvenly ? SegmentSeconds : 0,
            SrtPath = SrtPath.Length > 0 ? SrtPath : null, OutputFolder = OutputFolder.Length > 0 ? OutputFolder : null
        };
        return true;
    }

    private static string Next(string start, int seconds)
    {
        var p = start.Split(':');
        var total = int.Parse(p[0], CultureInfo.InvariantCulture) * 3600 + int.Parse(p[1], CultureInfo.InvariantCulture) * 60 + (int)double.Parse(p[2], CultureInfo.InvariantCulture) + seconds;
        return $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}";
    }

    private void Open(Action action)
    {
        try { action(); }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }
}
