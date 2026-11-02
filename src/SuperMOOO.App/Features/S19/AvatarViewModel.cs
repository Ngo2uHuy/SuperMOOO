using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using SuperMOOO.AI.LipSync;
using SuperMOOO.AI.Tts;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Workflows.Avatars;
using SuperMOOO.Workflows.Live;

namespace SuperMOOO.App.Features.S19;

[Service(ServiceLifetime.Transient)]
public sealed partial class AvatarViewModel(AvatarService avatars, SalesScriptService scripts, VoiceCatalogService voices, ISettingsStore settings,
    IDialogService dialogs, IMediaProbe probe, ILipSyncEngine engine) : ObservableObject
{
    // Tốc độ đọc tiếng Việt ~14 ký tự/giây, chỉ để ước tính khi chưa có audio thật.
    private const double CharsPerSecond = 14;
    private bool _ready;

    public ObservableCollection<Avatar> Avatars { get; } = [];
    public ObservableCollection<VoiceInfo> Voices { get; } = [];
    public IReadOnlyList<int> FaceSecondsChoices { get; } = [5, 10, 20, 40];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrepareFaceCommand), nameof(DeleteAvatarCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedFaceText))]
    public partial Avatar? Selected { get; set; }

    [ObservableProperty]
    public partial int FaceSeconds { get; set; } = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseTts))]
    public partial bool UseAudioFile { get; set; }

    public bool UseTts => !UseAudioFile;

    [ObservableProperty]
    public partial string ScriptText { get; set; } = "";

    [ObservableProperty]
    public partial string ProductName { get; set; } = "";

    [ObservableProperty]
    public partial string ProductFacts { get; set; } = "";

    [ObservableProperty]
    public partial VoiceInfo? Voice { get; set; }

    [ObservableProperty]
    public partial double Speed { get; set; } = 1.0;

    [ObservableProperty]
    public partial string AudioPath { get; set; } = "";

    [ObservableProperty]
    public partial double AudioSeconds { get; set; }

    [ObservableProperty]
    public partial string RuntimeMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuntimeMissing))]
    public partial bool RuntimeReady { get; set; }

    public bool RuntimeMissing => !RuntimeReady;

    [ObservableProperty]
    public partial string EstimateText { get; set; } = "";

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SuggestScriptCommand))]
    public partial bool IsBusy { get; set; }

    public async Task LoadAsync()
    {
        var s = settings.Get<LipSyncSettings>("lipsync");
        try
        {
            Voices.Clear();
            foreach (var v in await voices.ListAsync("vi")) Voices.Add(v);
        }
        catch (ProviderException e) { Status = "Chưa lấy được danh sách giọng đọc: " + e.Message; }

        Voice = Voices.FirstOrDefault(v => v.Id == s.VoiceId) ?? Voices.FirstOrDefault();
        await ReloadAsync(s.AvatarId);
        _ready = true;
        RefreshRuntime();
    }

    public async Task ReloadAsync(string? selectId)
    {
        var list = await avatars.ListAsync();
        // Xoá/nạp lại danh sách làm Selected nhảy về null; không để nó ghi đè avatar đã lưu trong cài đặt.
        var was = _ready;
        _ready = false;
        Avatars.Clear();
        foreach (var a in list) Avatars.Add(a);
        Selected = Avatars.FirstOrDefault(a => a.Id == selectId) ?? Avatars.FirstOrDefault();
        _ready = was;
        if (was) OnSelectedChanged(Selected);
    }

    [RelayCommand]
    private Task RefreshListAsync() => ReloadAsync(Selected?.Id);

    public Task OnJobSucceededAsync() => ReloadAsync(Selected?.Id);

    private void RefreshRuntime()
    {
        var status = engine.GetStatus();
        RuntimeReady = status.Ready;
        RuntimeMessage = status.Ready ? status.Message : status.Message + " " + MuseTalkInstaller.SpeedNote;
        RefreshEstimate();
    }

    partial void OnSelectedChanged(Avatar? value)
    {
        if (!_ready) return;
        var s = settings.Get<LipSyncSettings>("lipsync");
        s.AvatarId = value?.Id ?? "";
        _ = settings.SetAsync("lipsync", s);
        RefreshEstimate();
    }

    partial void OnVoiceChanged(VoiceInfo? value)
    {
        if (!_ready || value is null) return;
        var s = settings.Get<LipSyncSettings>("lipsync");
        s.VoiceId = value.Id;
        _ = settings.SetAsync("lipsync", s);
    }

    partial void OnScriptTextChanged(string value) => RefreshEstimate();
    partial void OnSpeedChanged(double value) => RefreshEstimate();
    partial void OnUseAudioFileChanged(bool value) => RefreshEstimate();
    partial void OnAudioSecondsChanged(double value) => RefreshEstimate();

    private double ExpectedSeconds() => UseAudioFile ? AudioSeconds : ScriptText.Trim().Length / CharsPerSecond / Math.Max(0.5, Speed);

    private void RefreshEstimate()
    {
        var seconds = ExpectedSeconds();
        if (Selected is not { } avatar || seconds <= 0) { EstimateText = ""; return; }
        var estimate = avatars.Estimate(avatar, seconds);
        var minutes = Math.Max(1, (int)Math.Round(estimate.Seconds / 60.0));
        EstimateText = $"Lời thoại ~{seconds:0} giây → ước tính ~{minutes} phút trên CPU" +
                       (estimate.FaceDetectionNeeded ? " (gồm dò mặt lần đầu; bấm \"Dò mặt trước\" để chạy riêng một lần)." : ".");
    }

    [RelayCommand]
    private async Task AddAvatarAsync()
    {
        if (dialogs.OpenFile("Chọn video avatar (MP4, MOV, WebM, MKV; tối đa 250 MB và 3 phút)", "Video|*.mp4;*.mov;*.webm;*.mkv") is not { } path) return;
        IsBusy = true;
        Status = "Đang kiểm tra và chuẩn hoá video avatar…";
        try
        {
            var avatar = await avatars.AddAsync(path, null);
            await ReloadAsync(avatar.Id);
            dialogs.Info("Avatar", "Đã thêm avatar. Bấm \"Dò mặt trước\" để các lần dựng sau nhanh hơn.");
            Status = null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or MediaProbeException or FfmpegException)
        {
            Status = null;
            dialogs.Error(e.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task DeleteAvatarAsync()
    {
        if (Selected is not { } avatar) return;
        if (!await dialogs.ConfirmAsync("Xoá avatar", $"Xoá \"{avatar.Name}\" cùng bản chép video và toạ độ mặt đã dò? Không thể khôi phục.")) return;
        try
        {
            await avatars.DeleteAsync(avatar);
            await ReloadAsync(null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { dialogs.Error(e.Message); }
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task PrepareFaceAsync()
    {
        if (Selected is not { } avatar) return;
        if (!EnsureRuntime()) return;
        try
        {
            using var owner = JobOwner.Begin(JobOwner.Screen("S19"));
            await avatars.PrepareFaceAsync(avatar, FaceSeconds);
            dialogs.Info("Dò mặt", "Đã đưa vào hàng đợi (theo dõi ở khung Tác vụ). Việc này chậm trên CPU, ~3 giây mỗi khung.");
        }
        catch (Exception e) { dialogs.Error(e.Message); }
    }

    private bool HasSelected() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SuggestScriptAsync()
    {
        IsBusy = true;
        Status = "Đang soạn gợi ý lời thoại…";
        try
        {
            ScriptText = await scripts.SuggestAsync(ProductName, ProductFacts, CancellationToken.None);
            Status = "Đã gợi ý. Hãy kiểm tra giá và thông tin trước khi dùng.";
        }
        catch (Exception e) when (e is LiveBusyException or ProviderException or ArgumentException or InvalidOperationException)
        {
            Status = null;
            dialogs.Error(e is ArgumentException or LiveBusyException ? e.Message : "Chưa có model trả lời: " + e.Message + " Bạn vẫn có thể tự nhập lời thoại.");
        }
        finally { IsBusy = false; }
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand]
    private async Task PickAudioAsync()
    {
        if (dialogs.OpenFile("Chọn file audio lời thoại (tối đa 3 phút)", "Âm thanh|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg|Tất cả|*.*") is not { } path) return;
        try
        {
            var info = await probe.ProbeAsync(path, CancellationToken.None);
            if (info.Audio is null) { dialogs.Error("File không có âm thanh hợp lệ."); return; }
            if (info.Duration.TotalSeconds > AvatarLimits.MaxSeconds) { dialogs.Error("File audio dài tối đa 3 phút."); return; }
            AudioPath = path;
            AudioSeconds = info.Duration.TotalSeconds;
            UseAudioFile = true;
        }
        catch (MediaProbeException e) { dialogs.Error(e.Message); }
    }

    private bool EnsureRuntime()
    {
        RefreshRuntime();
        if (RuntimeReady) return true;
        dialogs.Error(RuntimeMessage);
        return false;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (Selected is not { } avatar) { dialogs.Error("Chọn hoặc thêm một avatar trước."); return; }
        if (UseAudioFile ? AudioPath.Length == 0 : string.IsNullOrWhiteSpace(ScriptText)) { dialogs.Error("Nhập lời thoại hoặc chọn file audio."); return; }
        if (!UseAudioFile && Voice is null) { dialogs.Error("Chưa chọn giọng đọc."); return; }
        if (ExpectedSeconds() > AvatarLimits.MaxSeconds) { dialogs.Error("Lời thoại dài hơn 3 phút. Tách thành nhiều video ngắn hơn."); return; }
        if (!EnsureRuntime()) return;
        try
        {
            var s = settings.Get<LipSyncSettings>("lipsync");
            using var owner = JobOwner.Begin(JobOwner.Screen("S19"));
            await avatars.StartAsync(new AvatarVideoOptions
            {
                AvatarId = avatar.Id, Text = UseAudioFile ? "" : ScriptText, AudioPath = UseAudioFile ? AudioPath : null,
                VoiceId = Voice?.Id ?? s.VoiceId, Speed = Speed, OutputFolder = string.IsNullOrWhiteSpace(s.OutputFolder) ? null : s.OutputFolder
            });
            dialogs.Info("Đã đưa vào hàng đợi", "Video khớp môi đang chờ dựng (theo dõi ở khung Tác vụ của màn này). Trên CPU việc này rất chậm.");
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException) { dialogs.Error(e.Message); }
    }

    public string SelectedFaceText => Selected is not { } a ? "" : a.FaceStatus switch
    {
        AvatarFaceStatus.Ready => $"Đã dò mặt cho lời thoại tới {a.FaceSeconds.ToString(CultureInfo.InvariantCulture)} giây · video {a.DurationSec:0} giây.",
        AvatarFaceStatus.Failed => "Dò mặt lỗi lần trước (xem khung Tác vụ). Bấm \"Dò mặt trước\" để thử lại.",
        _ => $"Chưa dò mặt · video {a.DurationSec:0} giây."
    };
}
