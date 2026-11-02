using System.Collections.ObjectModel;
using System.IO;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Tts;
using SuperMOOO.AI.Voice;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S01;
using SuperMOOO.Data.Repositories.Files;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Media.Effects;
using SuperMOOO.Workflows;
using SuperMOOO.Workflows.TtsStudio;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S05;

public sealed record HistoryItem(string Path, string Operation, string Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>S05: tạo giọng (kịch bản, hàng loạt .txt), hiệu ứng giọng, xử lý audio AI, clone, TTS local; RVC chỉ hiện khi worker có.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class TtsStudioViewModel(TtsStudioService studio, VoiceEffectPresets effects, MediaFileRepository history,
    RvcVoiceConverter rvc, VoiceSpeed voiceSpeed, IAudioPlayer player, ShellService shell, JobTracker tracker, IDialogService dialogs) : JobViewModelBase(tracker, dialogs)
{
    protected override string JobsOwner => JobOwner.Screen("S05");

    private static readonly string[] HistoryOperations = ["tts", "voice", "audio"];
    private string? _lastAudio;

    public IReadOnlyList<string> Engines { get; } = ["Tất cả", .. VoiceIds.AllProviders];
    public IReadOnlyList<LanguageChoice> Languages { get; } = [.. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];
    public IReadOnlyList<VoiceEffect> Effects => effects.All;
    public IReadOnlyList<string> Formats { get; } = ["wav", "mp3", "flac"];
    public IReadOnlyList<string> DenoiseModes { get; } = ["Ai", "Fast"];
    public ObservableCollection<VoiceInfo> Voices { get; } = [];
    public ObservableCollection<HistoryItem> History { get; } = [];
    public ObservableCollection<RvcModel> RvcModels { get; } = [];

    [ObservableProperty] public partial string SelectedEngine { get; set; } = "Tất cả";
    [ObservableProperty] public partial LanguageChoice? Language { get; set; }
    [ObservableProperty] public partial VoiceInfo? SelectedVoice { get; set; }
    [ObservableProperty] public partial double Speed { get; set; } = 1.0;
    [ObservableProperty] public partial double Pitch { get; set; }
    [ObservableProperty] public partial VoiceEffect? SelectedEffect { get; set; }
    [ObservableProperty] public partial double EffectPitch { get; set; }
    [ObservableProperty] public partial string Format { get; set; } = "wav";
    [ObservableProperty] public partial string ScriptText { get; set; } = "";
    [ObservableProperty] public partial string CountText { get; set; } = "";
    [ObservableProperty] public partial bool HasAudio { get; set; }
    [ObservableProperty] public partial HistoryItem? SelectedHistory { get; set; }

    // Xử lý audio AI
    [ObservableProperty] public partial string AudioFile { get; set; } = "";
    [ObservableProperty] public partial bool DoDenoise { get; set; }
    [ObservableProperty] public partial string DenoiseMode { get; set; } = "Ai";
    [ObservableProperty] public partial bool DoDetect { get; set; }
    [ObservableProperty] public partial bool DoTrim { get; set; }

    // RVC (plugin tuỳ chọn)
    [ObservableProperty] public partial bool RvcAvailable { get; set; }
    [ObservableProperty] public partial RvcModel? SelectedRvcModel { get; set; }
    [ObservableProperty] public partial int RvcPitch { get; set; }

    public async Task LoadAsync()
    {
        Language ??= Languages[0];
        SelectedEffect ??= Effects.FirstOrDefault();
        if (ScriptText.Length == 0) ScriptText = TtsStudioService.SampleText(Language.Code);
        await ReloadVoicesAsync();
        await RefreshHistoryAsync();
        await ReloadRvcAsync();
    }

    partial void OnScriptTextChanged(string value) =>
        CountText = $"{value.Length:N0} ký tự • {value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length} từ";

    partial void OnSelectedEngineChanged(string value) => _ = ReloadVoicesAsync();
    partial void OnLanguageChanged(LanguageChoice? value) => _ = ReloadVoicesAsync();

    [RelayCommand]
    private async Task ReloadVoicesAsync()
    {
        await GuardAsync(async () =>
        {
            var keep = SelectedVoice?.Id;
            var list = await studio.ListVoicesAsync(Language?.Code, SelectedEngine == "Tất cả" ? null : SelectedEngine);
            Voices.Clear();
            foreach (var v in list) Voices.Add(v);
            SelectedVoice = Voices.FirstOrDefault(v => v.Id == keep) ?? Voices.FirstOrDefault();
        });
    }

    [RelayCommand]
    private void LoadSample() => ScriptText = TtsStudioService.SampleText(Language?.Code);

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (!TryRequest(out var request)) return;
        var job = await RunJobAsync(ct => studio.StartAsync(request, ct), "Đang sinh giọng đọc…");
        if (job is null || PipelineResult.Value(job.Result, "outputAudio") is not { } file) return;
        _lastAudio = file;
        HasAudio = true;
        await GuardAsync(async () =>
        {
            player.Play(file);
            Status = "Đã tạo audio: có thể nghe lại hoặc lưu.";
            await RefreshHistoryAsync();
        });
    }

    [RelayCommand]
    private Task BatchAsync() => GuardAsync(async () =>
    {
        if (Dialogs.PickFolder("Chọn thư mục kịch bản .txt") is not { } folder) return;
        var files = Directory.EnumerateFiles(folder, "*.txt").Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) throw new InvalidOperationException("Thư mục không có file .txt nào.");
        if (!TryRequest(out var template, requireText: false)) return;
        using var owner = OwnJobs();
        var ids = await studio.StartBatchAsync(files, template);
        Status = $"Đã đưa {ids.Count} kịch bản vào hàng đợi (theo dõi ở khung Tác vụ của màn này).";
        Dialogs.Info("Đã đưa vào hàng đợi", Status);
    });

    [RelayCommand]
    private Task PreviewVoiceAsync() => GuardAsync(async () =>
    {
        var voice = SelectedVoice ?? throw new InvalidOperationException("Chưa chọn giọng đọc.");
        player.Play(await voiceSpeed.PreviewAsync(voice.Id, Language?.Code, Speed, CancellationToken.None));
    });

    [RelayCommand]
    private void PlayLast()
    {
        if (_lastAudio is not null) Play(_lastAudio);
    }

    [RelayCommand]
    private void PlayHistory()
    {
        if (SelectedHistory is not null) Play(SelectedHistory.Path);
    }

    [RelayCommand]
    private void Stop() => player.Stop();

    [RelayCommand]
    private void SaveAs()
    {
        if (_lastAudio is null || !File.Exists(_lastAudio)) { Dialogs.Info("Chưa có audio", "Hãy tạo giọng đọc trước."); return; }
        var ext = Path.GetExtension(_lastAudio);
        if (Dialogs.SaveFile("Lưu file âm thanh", $"Audio (*{ext})|*{ext}", Path.GetFileName(_lastAudio)) is not { } target) return;
        try
        {
            File.Copy(_lastAudio, target, overwrite: true);
            Status = $"Đã lưu: {target}";
        }
        catch (IOException ex)
        {
            Dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_lastAudio is not null) shell.RevealFile(_lastAudio);
    }

    [RelayCommand]
    private void BrowseAudio()
    {
        if (Dialogs.OpenFile("Chọn file audio", "Audio|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.ogg|Tất cả|*.*") is { } file) AudioFile = file;
    }

    [RelayCommand]
    private async Task ProcessAudioAsync()
    {
        var job = await RunJobAsync(ct => studio.StartAudioProcessAsync(new AudioProcessRequest(AudioFile, DoDenoise, DenoiseMode, DoDetect, DoTrim), ct),
            "Đang xử lý audio…");
        if (job is null) return;
        if (PipelineResult.Value(job.Result, "outputAudio") is { } file)
        {
            _lastAudio = file;
            HasAudio = true;
        }

        if (PipelineResult.Value(job.Result, "speechSeconds") is { } seconds)
            Status = $"Phát hiện {double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture):0.0} giây có lời nói.";
        await RefreshHistoryAsync();
    }

    /// <summary>Job đã xong được chọn ở khung tác vụ: audio đó thành mục "nghe lại / lưu".</summary>
    public void ShowResult(string audio)
    {
        _lastAudio = audio;
        HasAudio = true;
        Status = $"Đã chọn: {Path.GetFileName(audio)} (bấm nghe lại hoặc lưu).";
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        var rows = await history.QueryAsync(["output"], null, 200);
        History.Clear();
        foreach (var f in rows.Where(f => f.Operation is not null && HistoryOperations.Contains(f.Operation)))
            History.Add(new HistoryItem(f.Path, f.Operation!, f.SizeBytes is { } b ? $"{b / 1048576.0:N1} MB" : ""));
    }

    // --- RVC ---
    private async Task ReloadRvcAsync()
    {
        RvcAvailable = await rvc.IsAvailableAsync(CancellationToken.None);
        RvcModels.Clear();
        if (!RvcAvailable) return;
        await GuardAsync(async () =>
        {
            foreach (var m in await rvc.ListModelsAsync(CancellationToken.None)) RvcModels.Add(m);
            SelectedRvcModel = RvcModels.FirstOrDefault();
        });
    }

    [RelayCommand]
    private Task ImportRvcModelAsync() => GuardAsync(async () =>
    {
        if (Dialogs.OpenFile("Chọn model RVC (.pth)", "RVC (*.pth)|*.pth") is not { } model) return;
        var index = Path.ChangeExtension(model, ".index");
        await rvc.ImportModelAsync(model, File.Exists(index) ? index : null, CancellationToken.None);
        await ReloadRvcAsync();
    });

    [RelayCommand]
    private Task ConvertRvcAsync() => GuardAsync(async () =>
    {
        var model = SelectedRvcModel ?? throw new InvalidOperationException("Chọn model RVC.");
        var input = _lastAudio ?? (File.Exists(AudioFile) ? AudioFile : throw new InvalidOperationException("Tạo giọng hoặc chọn file audio nguồn trước."));
        if (Dialogs.SaveFile("Lưu audio đã chuyển giọng", "WAV (*.wav)|*.wav", Path.GetFileNameWithoutExtension(input) + "_rvc.wav") is not { } output) return;
        Status = "Đang chuyển giọng…";
        await rvc.ConvertAsync(new RvcConvertRequest(input, output, model.ModelPath, model.IndexPath, RvcPitch), null, CancellationToken.None);
        Status = $"Đã lưu: {output}";
    });

    private void Play(string path) => _ = GuardAsync(() =>
    {
        player.Play(path);
        return Task.CompletedTask;
    });

    private bool TryRequest(out TtsStudioRequest request, bool requireText = true)
    {
        request = new TtsStudioRequest(ScriptText, SelectedVoice?.Id ?? "", Language?.Code, Speed, Pitch, SelectedEffect?.Id, EffectPitch, Format);
        if (SelectedVoice is null) { Dialogs.Error("Chưa chọn giọng đọc."); return false; }
        if (requireText && string.IsNullOrWhiteSpace(ScriptText)) { Dialogs.Error("Nhập văn bản cần đọc."); return false; }
        return true;
    }
}
