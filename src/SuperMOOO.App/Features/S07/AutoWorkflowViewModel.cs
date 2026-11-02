using System.Collections.ObjectModel;
using System.IO;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;
using SuperMOOO.AI.Tts;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S01;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.App.Features.S14;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;
using SuperMOOO.Publishing.Scheduling;
using SuperMOOO.Workflows;
using SuperMOOO.Workflows.Auto;
using SuperMOOO.Workflows.Delivery;

namespace SuperMOOO.App.Features.S07;

public sealed record AutoModeChoice(string Key, string Label);

public sealed record AutoJobRow(JobSnapshot Snapshot)
{
    public string Id => Snapshot.Id;
    public string Title => Snapshot.Title;
    public string Status => Labels.Job(Snapshot.Status);
    public string Progress => Snapshot.Status == JobStatus.Succeeded ? "100%" : $"{Math.Clamp(Snapshot.Progress, 0, 1):P0}";
    public string Detail => Snapshot.Error ?? Snapshot.Message ?? "";
    public string? Output => PipelineResult.Value(Snapshot.Result, "video") ?? PipelineResult.Value(Snapshot.Result, "outputAudio")
        ?? PipelineResult.Value(Snapshot.Result, "subtitleFile") ?? PipelineResult.Files(Snapshot.Result).FirstOrDefault();
}

/// <summary>Một bài đăng (pub_publications) của tác vụ/video tự động; dùng chung cho S07 và S08.</summary>
public sealed record DeliveryRow(Publication Post, string Account)
{
    public string Id => Post.Id;
    public string Title => Post.Title;
    public string Status => PublishLabels.Status(Post.Status.ToString());
    public string Due => Post.ScheduledUtc is { } at ? VietnamTime.ToLocal(at).ToString("dd/MM/yyyy HH:mm") : "";
    public string Error => Post.LastError ?? "";
}

/// <summary>S07: một form cho 8 chế độ Auto A→Z; theo dõi các tác vụ đã xếp và duyệt/huỷ bài đăng của từng tác vụ.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class AutoWorkflowViewModel(AutoWorkflowService auto, DeliveryService delivery, VoiceCatalogService voices, ShellService shell,
    OAuthConnectService oauth, PublicationService publications, IDialogService dialogs,
    LlmProviderRegistry llm, ISettingsStore settings) : ObservableObject
{
    public LlmPickerModel ScriptAi { get; } = new(llm, settings, dialogs);
    public static IReadOnlyList<AutoModeChoice> Modes { get; } = [.. AutoModes.All.Select(m => new AutoModeChoice(m.Key, m.Label))];
    public static IReadOnlyList<string> Aspects { get; } = ["original", "16:9", "9:16", "1:1", "4:5", "3:4"];
    public static IReadOnlyList<int> Resolutions { get; } = [360, 720, 1080];
    public static IReadOnlyList<string> Fits { get; } = ["contain", "cover"];
    public static IReadOnlyList<string> Operations { get; } = ["trim_silence", "denoise", "vad"];
    public static IReadOnlyList<string> DeliveryModesList { get; } = [DeliveryModes.Review, DeliveryModes.Automatic];
    public static IReadOnlyList<string> Privacies { get; } = ["private", "unlisted", "public"];
    public IReadOnlyList<LanguageChoice> Languages { get; } = [.. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];
    public IReadOnlyList<LanguageChoice> SourceLanguages { get; } = [new("auto", "Tự nhận diện (auto)"), .. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];

    public ObservableCollection<VoiceInfo> Voices { get; } = [];
    public ObservableCollection<AutoJobRow> Jobs { get; } = [];
    public DestinationPickerViewModel Destinations { get; } = new(oauth);
    public ObservableCollection<DeliveryRow> Deliveries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextMode), nameof(IsVideoMode), nameof(IsReviewMode), nameof(IsScriptMode), nameof(IsAudioMode), nameof(UsesVoice), nameof(UsesLanguages), nameof(UsesVideoOutput), nameof(CanDeliver))]
    public partial AutoModeChoice Mode { get; set; } = Modes[0];

    [ObservableProperty] public partial string SourcesText { get; set; } = "";
    [ObservableProperty] public partial string ScriptText { get; set; } = "";
    [ObservableProperty] public partial string SrtPath { get; set; } = "";
    [ObservableProperty] public partial LanguageChoice? SourceLanguage { get; set; }
    [ObservableProperty] public partial LanguageChoice? TargetLanguage { get; set; }
    [ObservableProperty] public partial bool Translate { get; set; }
    [ObservableProperty] public partial VoiceInfo? SelectedVoice { get; set; }
    [ObservableProperty] public partial double Speed { get; set; } = 1.0;
    [ObservableProperty] public partial string Aspect { get; set; } = "16:9";
    [ObservableProperty] public partial int Resolution { get; set; } = 720;
    [ObservableProperty] public partial string Fit { get; set; } = "contain";
    [ObservableProperty] public partial double Volume { get; set; } = 0.15;
    [ObservableProperty] public partial string MusicPath { get; set; } = "";
    [ObservableProperty] public partial double MusicVolume { get; set; } = 0.12;
    [ObservableProperty] public partial bool Captions { get; set; } = true;
    [ObservableProperty] public partial bool UseAi { get; set; } = true;
    [ObservableProperty] public partial bool Narration { get; set; } = true;
    [ObservableProperty] public partial int TargetSeconds { get; set; } = 60;
    [ObservableProperty] public partial string Style { get; set; } = "Kịch tính";
    [ObservableProperty] public partial string SlidePath { get; set; } = "";
    [ObservableProperty] public partial string BackgroundVideo { get; set; } = "";
    [ObservableProperty] public partial string Operation { get; set; } = "trim_silence";
    [ObservableProperty] public partial string OutputFolder { get; set; } = "";

    [ObservableProperty] public partial bool DeliverEnabled { get; set; }
    [ObservableProperty] public partial string DeliveryMode { get; set; } = DeliveryModes.Review;
    [ObservableProperty] public partial string DeliveryTitle { get; set; } = "";
    [ObservableProperty] public partial string DeliveryCaption { get; set; } = "";
    [ObservableProperty] public partial string Privacy { get; set; } = "private";
    [ObservableProperty] public partial int DelayMinutes { get; set; }
    [ObservableProperty] public partial int SpacingMinutes { get; set; } = 10;
    [ObservableProperty] public partial int DailyLimit { get; set; } = 20;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelJobCommand), nameof(RetryJobCommand), nameof(OpenOutputCommand))]
    public partial AutoJobRow? SelectedJob { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(CancelPostCommand))]
    public partial DeliveryRow? SelectedDelivery { get; set; }

    [ObservableProperty] public partial string Status { get; set; } = "";

    public bool IsTextMode => Mode.Key is AutoModes.Script or AutoModes.Voice;
    public bool IsVideoMode => !IsTextMode;
    public bool IsReviewMode => Mode.Key is AutoModes.Review or AutoModes.Summary;
    public bool IsScriptMode => Mode.Key == AutoModes.Script;
    public bool IsAudioMode => Mode.Key == AutoModes.Audio;
    public bool UsesVoice => Mode.Key is AutoModes.Localize or AutoModes.Script or AutoModes.Voice or AutoModes.Review or AutoModes.Summary;
    public bool UsesLanguages => Mode.Key is not (AutoModes.Reup or AutoModes.Audio);
    public bool UsesVideoOutput => Mode.Key is not (AutoModes.Subtitles or AutoModes.Voice or AutoModes.Audio);
    public bool CanDeliver => UsesVideoOutput;

    public async Task LoadAsync()
    {
        SourceLanguage ??= SourceLanguages[0];
        TargetLanguage ??= Languages.FirstOrDefault(l => l.Code == "vi") ?? Languages[0];
        try
        {
            foreach (var v in await voices.ListAsync(TargetLanguage.Code)) Voices.Add(v);
            // §8 #5: mặc định giọng local (VieNeu), không tự chọn giọng cloud tính phí.
            SelectedVoice ??= Voices.FirstOrDefault(v => v.ProviderId == VoiceIds.VieNeu)
                ?? Voices.FirstOrDefault(v => v.ProviderId == VoiceIds.OmniVoice)
                ?? Voices.FirstOrDefault();
            await ReloadAccountsAsync();
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ProviderException)
        {
            Status = "Lỗi: " + ex.Message;
        }
    }

    private Task ReloadAccountsAsync() => Destinations.LoadAsync();

    /// <summary>Gọi định kỳ khi màn đang hiện: cập nhật danh sách tác vụ và bài đăng của tác vụ đang chọn.</summary>
    public async Task RefreshAsync()
    {
        var keep = SelectedJob?.Id;
        var jobs = await auto.ListJobsAsync();
        _syncing = true;
        try
        {
            Jobs.Sync([.. jobs.Select(j => new AutoJobRow(j))], j => j.Id, j => (j.Title, j.Status, j.Progress, j.Detail, j.Output));
            SelectedJob = Jobs.FirstOrDefault(j => j.Id == keep);
        }
        finally { _syncing = false; }
        await RefreshDeliveriesAsync();
    }

    private bool _syncing;

    partial void OnSelectedJobChanged(AutoJobRow? value)
    {
        if (!_syncing) _ = RefreshDeliveriesAsync();
    }

    private async Task RefreshDeliveriesAsync()
    {
        if (SelectedJob is null)
        {
            Deliveries.Clear();
            return;
        }

        var jobId = SelectedJob.Id;
        var keep = SelectedDelivery?.Id;
        var posts = await delivery.ListAsync(PublicationOrigins.AutoWorkflow, jobId);
        if (SelectedJob?.Id != jobId) return;
        Deliveries.Sync([.. posts.Select(p => new DeliveryRow(p, Destinations.NameOf(p.DestinationId)))], d => d.Id, d => (d.Title, d.Status, d.Due, d.Account));
        SelectedDelivery = Deliveries.FirstOrDefault(d => d.Id == keep);
    }

    [RelayCommand]
    private void AddFiles()
    {
        if (Dialogs.OpenFile(IsTextMode ? "Chọn kịch bản" : "Chọn video/audio", IsTextMode ? "Kịch bản|*.txt;*.docx|Tất cả|*.*" : "Media|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.wav;*.mp3;*.m4a;*.flac;*.ogg|Tất cả|*.*") is { } file)
            AppendSource(file);
    }

    [RelayCommand]
    private void AddFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục nguồn") is { } folder) AppendSource(folder);
    }

    public void AddSources(IEnumerable<string> paths)
    {
        foreach (var p in paths) AppendSource(p);
    }

    private void AppendSource(string source) => SourcesText = string.IsNullOrWhiteSpace(SourcesText) ? source : SourcesText.TrimEnd() + Environment.NewLine + source;

    [RelayCommand]
    private void Pick(string target)
    {
        var filter = target switch
        {
            "music" => "Audio|*.mp3;*.wav;*.m4a;*.aac;*.flac|Tất cả|*.*",
            "srt" => "SRT (*.srt)|*.srt",
            "slide" => "Ảnh|*.png;*.jpg;*.jpeg;*.webp|Tất cả|*.*",
            _ => "Video|*.mp4;*.mkv;*.mov;*.webm|Tất cả|*.*"
        };
        if (Dialogs.OpenFile("Chọn tệp", filter) is not { } file) return;
        switch (target)
        {
            case "music": MusicPath = file; break;
            case "srt": SrtPath = file; break;
            case "slide": SlidePath = file; break;
            case "background": BackgroundVideo = file; break;
        }
    }

    [RelayCommand]
    private void ClearFile(string target)
    {
        switch (target)
        {
            case "music": MusicPath = ""; break;
            case "srt": SrtPath = ""; break;
            case "slide": SlidePath = ""; break;
            case "background": BackgroundVideo = ""; break;
        }
    }

    [RelayCommand]
    private void ChooseOutputFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục xuất") is { } folder) OutputFolder = folder;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        try
        {
            var request = BuildRequest();
            using var owner = JobOwner.Begin(JobOwner.Screen("S07"));
            var jobs = await auto.StartAsync(request);
            Status = $"Đã đưa {jobs.Count} tác vụ vào hàng đợi.";
            Dialogs.Info("Đã đưa vào hàng đợi", Status + " Theo dõi ở bảng Tác vụ Auto.");
            await RefreshAsync();
            SelectedJob = Jobs.FirstOrDefault(j => j.Id == jobs[0].JobId);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException or FormatException)
        {
            Status = "Lỗi: " + ex.Message;
            Dialogs.Error(ex.Message);
        }
    }

    private AutoRequest BuildRequest() => new()
    {
        Mode = Mode.Key,
        Sources = [.. SourcesText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        Text = ScriptText, SrtPath = Empty(SrtPath), SourceLanguage = SourceLanguage?.Code ?? "auto", TargetLanguage = TargetLanguage?.Code ?? "vi",
        Translate = Translate, Ai = UseAi, Narration = Narration, TargetSeconds = TargetSeconds, Style = Style,
        ProviderId = IsReviewMode ? ScriptAi.ProviderId : null, Model = IsReviewMode ? ScriptAi.Model : null,
        VoiceId = SelectedVoice?.Id ?? "", Speed = Speed, Aspect = Aspect, Resolution = Resolution, Fit = Fit, Volume = Volume, MusicPath = Empty(MusicPath),
        MusicVolume = MusicVolume, Captions = Captions, Slide = Empty(SlidePath), BackgroundVideo = Empty(BackgroundVideo), Operation = Operation,
        OutputFolder = Empty(OutputFolder),
        Delivery = DeliverEnabled && CanDeliver ? new DeliveryOptions
        {
            Mode = DeliveryMode, DestinationIds = Destinations.SelectedIds, Title = DeliveryTitle, Caption = DeliveryCaption,
            Privacy = Privacy, DelayMinutes = DelayMinutes, SpacingMinutes = SpacingMinutes, DailyLimit = DailyLimit
        } : null
    };

    private static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private IDialogService Dialogs => dialogs;

    [RelayCommand(CanExecute = nameof(HasJob))]
    private Task CancelJobAsync() => Guard(() => auto.CancelAsync(SelectedJob!.Id));

    [RelayCommand(CanExecute = nameof(HasJob))]
    private Task RetryJobAsync() => Guard(async () =>
    {
        await auto.RetryAsync(SelectedJob!.Id);
        await RefreshAsync();
    });

    [RelayCommand(CanExecute = nameof(HasOutput))]
    private void OpenOutput()
    {
        if (SelectedJob?.Output is { } path) Open(() => shell.RevealFile(path));
    }

    [RelayCommand(CanExecute = nameof(HasDelivery))]
    private Task ApproveAsync() => Guard(async () =>
    {
        var id = SelectedDelivery!.Id;
        await new PostActions(publications, dialogs).ConfirmWarningsAsync(await delivery.ApproveAsync(id, false), ack => delivery.ApproveAsync(id, ack));
        await RefreshDeliveriesAsync();
    });

    [RelayCommand(CanExecute = nameof(HasDelivery))]
    private Task CancelPostAsync() => Guard(async () =>
    {
        if (!await dialogs.ConfirmAsync("Huỷ bài đăng", "Huỷ bài đăng này? Bài chưa bắt đầu đăng sẽ không được đăng nữa.")) return;
        await delivery.CancelAsync(SelectedDelivery!.Id);
        await RefreshDeliveriesAsync();
    });

    private bool HasJob() => SelectedJob is not null;
    private bool HasOutput() => SelectedJob?.Output is not null;
    private bool HasDelivery() => SelectedDelivery is not null;

    private async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or NotSupportedException)
        {
            Status = "Lỗi: " + ex.Message;
            dialogs.Error(ex.Message);
        }
    }

    private void Open(Action action)
    {
        try { action(); }
        catch (IOException ex) { dialogs.Error(ex.Message); }
    }
}
