using System.Collections.ObjectModel;
using System.IO;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;
using SuperMOOO.AI.Tts;
using SuperMOOO.App.Controls;
using SuperMOOO.Workflows.TtsStudio;
using SuperMOOO.App.Features.S01;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Workflows;
using SuperMOOO.Workflows.Steps;
using SuperMOOO.Workflows.Talkshow;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S03;

/// <summary>S03 tab Talkshow: kịch bản MC/Khách (AI hoặc mẫu offline), hai giọng, sân khấu màn xanh, xem trước khung và xuất.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class TalkshowViewModel(TalkshowService talkshow, VoiceCatalogService voices, VoiceSpeed speed, IAudioPlayer player,
    ShellService shell, JobTracker tracker, IDialogService dialogs, ISettingsStore settings, LlmProviderRegistry llm) : JobViewModelBase(tracker, dialogs)
{
    protected override string JobsOwner => JobOwner.Screen("S03");

    public static IReadOnlyList<int> Heights { get; } = [720, 1080];
    public IReadOnlyList<string> Topics => TalkshowService.Topics;
    public IReadOnlyList<LanguageChoice> Languages { get; } = [.. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];
    public ObservableCollection<VoiceInfo> Voices { get; } = [];
    public LlmPickerModel ScriptAi { get; } = new(llm, settings, dialogs);

    [ObservableProperty] public partial string Title { get; set; } = "Chuyện bên tách trà";
    [ObservableProperty] public partial string Topic { get; set; } = TalkshowService.Topics[0];
    [ObservableProperty] public partial int Minutes { get; set; } = 5;
    [ObservableProperty] public partial string Prompt { get; set; } = "";
    [ObservableProperty] public partial string ScriptText { get; set; } = "";
    [ObservableProperty] public partial LanguageChoice? Language { get; set; }
    [ObservableProperty] public partial VoiceInfo? HostVoice { get; set; }
    [ObservableProperty] public partial VoiceInfo? GuestVoice { get; set; }
    [ObservableProperty] public partial double HostSpeed { get; set; } = 1.0;
    [ObservableProperty] public partial double GuestSpeed { get; set; } = 1.0;
    [ObservableProperty] public partial string StagePath { get; set; } = "";
    [ObservableProperty] public partial string SceneryPath { get; set; } = "";
    [ObservableProperty] public partial string MusicPath { get; set; } = "";
    [ObservableProperty] public partial double MusicVolume { get; set; } = 0.06;
    [ObservableProperty] public partial int Height { get; set; } = 720;
    [ObservableProperty] public partial bool Captions { get; set; } = true;
    [ObservableProperty] public partial bool ScreenKey { get; set; } = true;
    [ObservableProperty] public partial string OutputFolder { get; set; } = "";
    [ObservableProperty] public partial string? PreviewFramePath { get; set; }
    [ObservableProperty] public partial string? OutputVideo { get; set; }

    public event EventHandler<string>? ResultReady;

    public async Task LoadAsync()
    {
        Language ??= Languages.FirstOrDefault(l => l.Code == "vi") ?? Languages[0];
        await GuardAsync(async () =>
        {
            foreach (var v in await voices.ListAsync(Language.Code)) Voices.Add(v);
            HostVoice ??= Voices.FirstOrDefault(v => v.Gender == "male") ?? Voices.FirstOrDefault();
            GuestVoice ??= Voices.FirstOrDefault(v => v.Gender == "female") ?? Voices.FirstOrDefault();
        });
    }

    [RelayCommand]
    private void LoadDemo()
    {
        ScriptText = TalkshowScript.Format(TalkshowScript.Demo());
        Title = "Một cuộc gọi về nhà";
        Status = "Đã nạp kịch bản mẫu (viết sẵn, không dùng AI).";
    }

    [RelayCommand]
    private async Task GenerateScriptAsync()
    {
        if (ScriptText.Trim().Length > 0 && !await Dialogs.ConfirmAsync("Thay kịch bản", "Kịch bản hiện tại sẽ bị thay bằng bản AI viết. Tiếp tục?", "Thay", "Giữ lại")) return;
        await RunDirectAsync(async (progress, ct) =>
        {
            var turns = await talkshow.GenerateScriptAsync(Topic, Prompt, Minutes, Language?.Code ?? "vi", progress, ct,
                ScriptAi.ProviderId, ScriptAi.Model);
            ScriptText = TalkshowScript.Format(turns);
            Status = $"AI đã viết {turns.Count} lượt thoại; bạn có thể sửa trước khi xuất.";
        }, "AI đang viết kịch bản…");
    }

    [RelayCommand]
    private void Pick(string target)
    {
        var filter = target switch
        {
            "music" => "Audio|*.mp3;*.wav;*.m4a;*.aac;*.flac|Tất cả|*.*",
            "scenery" => "Ảnh/clip|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.mp4;*.mov|Tất cả|*.*",
            _ => "Ảnh|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Tất cả|*.*"
        };
        if (Dialogs.OpenFile("Chọn tệp", filter) is not { } file) return;
        switch (target)
        {
            case "stage": StagePath = file; break;
            case "scenery": SceneryPath = file; break;
            case "music": MusicPath = file; break;
        }
    }

    [RelayCommand]
    private void ClearFile(string target)
    {
        switch (target)
        {
            case "stage": StagePath = ""; break;
            case "scenery": SceneryPath = ""; break;
            case "music": MusicPath = ""; break;
        }
    }

    [RelayCommand]
    private void ChooseOutputFolder()
    {
        if (Dialogs.PickFolder("Chọn thư mục xuất") is { } folder) OutputFolder = folder;
    }

    [RelayCommand]
    private Task PreviewStageAsync() => RunDirectAsync(async (_, ct) =>
        PreviewFramePath = await talkshow.PreviewFrameAsync(StagePath, SceneryPath, ScreenKey, ct), "Đang dựng khung sân khấu…");

    [RelayCommand]
    private Task PreviewVoiceAsync(string role) => RunDirectAsync(async (_, ct) =>
    {
        var voice = role == "guest" ? GuestVoice : HostVoice;
        if (voice is null) throw new InvalidOperationException("Chưa chọn giọng đọc.");
        player.Play(await speed.PreviewAsync(voice.Id, Language?.Code, role == "guest" ? GuestSpeed : HostSpeed, ct));
    }, "Đang đọc thử…");

    [RelayCommand]
    private async Task StartAsync()
    {
        if (HostVoice is null || GuestVoice is null) { Dialogs.Error("Chọn giọng cho cả MC và khách mời."); return; }
        TalkshowProject project;
        try
        {
            project = new TalkshowProject
            {
                Title = Title, Topic = Topic, Prompt = Prompt, Language = Language?.Code ?? "vi", Turns = [.. TalkshowScript.Parse(ScriptText)],
                Host = new VoiceSetting { VoiceId = HostVoice.Id, Speed = HostSpeed }, Guest = new VoiceSetting { VoiceId = GuestVoice.Id, Speed = GuestSpeed },
                Stage = StagePath, Scenery = SceneryPath, Music = MusicPath, MusicVolume = MusicVolume, Height = Height, Captions = Captions,
                ScreenKey = ScreenKey, OutputFolder = OutputFolder.Length > 0 ? OutputFolder : null
            };
        }
        catch (FormatException ex)
        {
            Dialogs.Error(ex.Message);
            return;
        }

        var job = await RunJobAsync(ct => talkshow.StartAsync(project, ct), "Đang dựng talkshow…");
        if (job is null) return;
        if (PipelineResult.Value(job.Result, "video") is { } video) ShowResult(video);
    }

    /// <summary>Kết quả của lần dựng vừa xong hoặc của job đã xong được chọn ở khung tác vụ.</summary>
    public void ShowResult(string video)
    {
        OutputVideo = video;
        ResultReady?.Invoke(this, video);
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

    private void Open(Action action)
    {
        try { action(); }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }
}
