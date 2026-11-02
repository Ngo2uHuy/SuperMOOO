using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SuperMOOO.AI.Tts;
using SuperMOOO.AI.Image;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperMOOO.Workflows.LiveGame;

namespace SuperMOOO.App.Features.S24;

[Service(ServiceLifetime.Transient)]
public sealed partial class LiveGameViewModel : ObservableObject
{
    private readonly LiveGameSessionService _session;
    private readonly LiveGameEngine _engine;
    private readonly ISettingsStore _settings;
    private readonly ILiveGameStore _store;
    private readonly VoiceCatalogService _voices;
    private readonly IDialogService _dialogs;
    private readonly IJobQueue _jobs;
    private readonly LiveGameGiftCatalog _catalog;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _attached;
    private string _lastConnection = "";

    public LiveGameViewModel(LiveGameSessionService session, LiveGameEngine engine, ISettingsStore settings,
        ILiveGameStore store, VoiceCatalogService voices, IDialogService dialogs, ImageProviderRegistry images, IJobQueue jobs,
        LiveGameGiftCatalog catalog, AppPaths paths)
    {
        _session = session; _engine = engine; _settings = settings; _store = store; _voices = voices; _dialogs = dialogs;
        _jobs = jobs; _catalog = catalog; ImageProviders = images.All;
        ImageProvider = images.Selected.Id;
        var s = settings.Get<LiveGameSettings>("livegame");
        Port = s.Port; TikFinityUrl = s.TikFinityUrl; VoiceId = s.VoiceId; Volume = s.Volume;
        SentencesPerMinute = s.SentencesPerMinute; EnableAi = s.EnableAi; EnableSpeech = s.EnableSpeech;
        DiagnosticLogging = s.DiagnosticLogging; Fps = s.Fps; BannedWords = string.Join(Environment.NewLine, s.BannedWords);
        GiftRules = new(s.GiftRules.Select(r => new GiftRuleEditor { MinCoins = r.MinCoins, Action = r.Action, Label = r.Label }));
        IdleReconnectMinutes = s.IdleReconnectMinutes; CommandsPerMinute = s.CommandsPerMinute;
        Actions = [.. LiveGameActions.BuiltIn.Select(a => new LiveRuleOption(a.Id, a.Name))];
        // Hành động riêng của gói chủ đề; theme lỗi thì chỉ còn hành động dựng sẵn, engine sẽ báo lỗi khi bắt đầu phiên.
        try { foreach (var a in LiveGameTheme.Load(paths, s.ThemeId).Actions) Actions.Add(new(a.Id, $"{a.Icon} {a.Label} (chủ đề)".Trim())); }
        catch (Exception e) when (e is InvalidOperationException or IOException or JsonException) { }
        foreach (var gift in catalog.List()) ObservedGifts.Add(gift);
        Rules = new(s.Rules.Select(r =>
        {
            // Giữ giá trị hành động không còn trong theme để không âm thầm đổi luật.
            if (Actions.All(a => a.Value != r.Action)) Actions.Add(new(r.Action, r.Action + " (không có trong chủ đề)"));
            return LiveRuleEditor.From(r, ObservedGifts.FirstOrDefault(g => g.GiftId.Length > 0 && g.GiftId == r.GiftId));
        }));
        _timer.Tick += (_, _) => Refresh();
    }

    public ObservableCollection<VoiceInfo> Voices { get; } = [];
    public ObservableCollection<GiftRuleEditor> GiftRules { get; }
    public ObservableCollection<LiveRuleEditor> Rules { get; }
    public ObservableCollection<LiveGameObservedGift> ObservedGifts { get; } = [];
    public ObservableCollection<LiveRuleOption> Actions { get; }
    public IReadOnlyList<LiveRuleOption> RuleSources { get; } = [new("gift", "Quà"), new("chat", "Bình luận")];
    public IReadOnlyList<LiveRuleOption> MatchModes { get; } = [new("exact", "Đúng nguyên tên/câu"), new("contains", "Có chứa")];
    public ObservableCollection<string> Events { get; } = [];
    public ObservableCollection<LiveGameDonor> Top { get; } = [];
    public ObservableCollection<LiveGameDonor> Weekly { get; } = [];
    public IReadOnlyList<int> FrameRates { get; } = [30, 60];
    public IReadOnlyList<IImageProvider> ImageProviders { get; }
    [ObservableProperty] public partial string ImageProvider { get; set; } = "";
    [ObservableProperty] public partial int Port { get; set; } = 18121;
    [ObservableProperty] public partial string TikFinityUrl { get; set; } = "";
    [ObservableProperty] public partial string VoiceId { get; set; } = "";
    [ObservableProperty] public partial double Volume { get; set; } = 0.8;
    [ObservableProperty] public partial int SentencesPerMinute { get; set; } = 6;
    [ObservableProperty] public partial bool EnableAi { get; set; }
    [ObservableProperty] public partial bool EnableSpeech { get; set; } = true;
    [ObservableProperty] public partial bool DiagnosticLogging { get; set; }
    [ObservableProperty] public partial int Fps { get; set; } = 30;
    [ObservableProperty] public partial string BannedWords { get; set; } = "";
    [ObservableProperty] public partial int IdleReconnectMinutes { get; set; } = 10;
    [ObservableProperty] public partial int CommandsPerMinute { get; set; } = 20;
    [ObservableProperty] public partial bool Simulated { get; set; } = true;
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial bool Running { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Chưa bắt đầu";
    [ObservableProperty] public partial string Stats { get; set; } = "";
    [ObservableProperty] public partial string Notice { get; set; } = "";
    [ObservableProperty] public partial string Url { get; set; } = "";
    public bool CanConfigure => !Running && !Busy;
    public bool CanStart => !Running && !Busy;
    public bool CanStop => Running && !Busy;
    public bool CanSimulate => Running && _session.Simulated && !Busy;
    public bool CanEditRules => !Busy;
    partial void OnBusyChanged(bool value) => NotifyState();
    partial void OnRunningChanged(bool value) => NotifyState();
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanStop)); OnPropertyChanged(nameof(CanSimulate)); OnPropertyChanged(nameof(CanEditRules));
    }

    public async Task LoadAsync()
    {
        try
        {
            var voices = await _voices.ListAsync("vi");
            Voices.Clear();
            foreach (var voice in voices.Where(v => v.ProviderId.StartsWith("native.", StringComparison.Ordinal))) Voices.Add(voice);
            if (Voices.All(v => v.Id != VoiceId)) VoiceId = Voices.FirstOrDefault()?.Id ?? "cpp_vieneu_minh_quan_pro";
            await RefreshWeeklyAsync();
        }
        catch (Exception e) { _dialogs.Error(e.Message); }
    }

    private LiveGameSettings Options() => new()
    {
        Port = Port, TikFinityUrl = TikFinityUrl.Trim(), VoiceId = VoiceId, Volume = Volume,
        SentencesPerMinute = SentencesPerMinute, EnableAi = EnableAi, EnableSpeech = EnableSpeech,
        DiagnosticLogging = DiagnosticLogging, Fps = Fps, IdleReconnectMinutes = IdleReconnectMinutes, CommandsPerMinute = CommandsPerMinute,
        Rules = Rules.Select(r => r.ToRule()).ToList(),
        BannedWords = BannedWords.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        GiftRules = GiftRules.Select(r => new LiveGameGiftRule(r.MinCoins, r.Action, r.Label)).ToList()
    };

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart) return;
        Busy = true;
        try
        {
            var settings = Options(); settings.Validate();
            if (settings.EnableSpeech && !settings.VoiceId.StartsWith("cpp_", StringComparison.Ordinal)) throw new ArgumentException("Chọn giọng local để phát live.");
            await _settings.SetAsync("livegame", settings);
            await _session.StartAsync(settings, Simulated);
            Refresh(); _dialogs.Info("Live tương tác", "Đã bắt đầu. Mở cửa sổ phát để xem cảnh và bật âm thanh.");
        }
        catch (Exception e) { _dialogs.Error(e.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!CanStop) return;
        Busy = true;
        try { await _session.StopAsync(); Refresh(); await RefreshWeeklyAsync(); _dialogs.Info("Live tương tác", "Đã dừng phiên và lưu thống kê."); }
        catch (Exception e) { _dialogs.Error(e.Message); }
        finally { Busy = false; Refresh(); }
    }

    [RelayCommand]
    private void AddRule(string source)
    {
        if (Rules.Count >= LiveGameSettings.MaxRules) { _dialogs.Error($"Tối đa {LiveGameSettings.MaxRules} luật quà/bình luận."); return; }
        Rules.Add(source == "chat"
            ? new LiveRuleEditor { Source = "chat", Match = "contains", ActionId = "chant", CooldownSeconds = 30 }
            : new LiveRuleEditor { Source = "gift", Match = "exact", ActionId = "lotus" });
    }

    [RelayCommand]
    private void RemoveRule(LiveRuleEditor? rule)
    {
        if (rule is not null) Rules.Remove(rule);
    }

    [RelayCommand]
    private void RefreshGifts()
    {
        ObservedGifts.Clear();
        foreach (var gift in _catalog.List()) ObservedGifts.Add(gift);
    }

    [RelayCommand]
    private async Task SaveRulesAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            await SaveRulesCoreAsync();
            _dialogs.Info("Luật quà & bình luận", Running ? "Đã lưu và áp dụng ngay cho phiên đang chạy." : "Đã lưu. Luật được dùng khi bắt đầu phiên.");
        }
        catch (Exception e) { _dialogs.Error(e.Message); }
        finally { Busy = false; }
    }

    private async Task<List<LiveGameRule>> SaveRulesCoreAsync()
    {
        if (CommandsPerMinute is < 1 or > 120) throw new ArgumentException("Lệnh bình luận toàn phòng phải từ 1–120 lần/phút.");
        var rules = LiveGameSettings.CleanRules(Rules.Select(r => r.ToRule()).ToList());
        // CleanRules có thể cấp id mới cho luật trùng id; đồng bộ lại để bắn thử tìm đúng luật.
        for (var i = 0; i < rules.Count; i++) Rules[i].Id = rules[i].Id;
        var stored = _settings.Get<LiveGameSettings>("livegame");
        stored.Rules = rules; stored.CommandsPerMinute = CommandsPerMinute;
        await _settings.SetAsync("livegame", stored);
        _session.ApplyRules(rules, CommandsPerMinute);
        return rules;
    }

    [RelayCommand]
    private async Task TestRuleAsync(LiveRuleEditor? editor)
    {
        if (editor is null || Busy) return;
        Busy = true;
        try
        {
            if (!Running) throw new InvalidOperationException("Bắt đầu phiên (có thể bật Chế độ giả lập) rồi mới bắn thử luật.");
            if (!editor.Enabled) throw new InvalidOperationException("Bật luật này trước khi bắn thử.");
            var rules = await SaveRulesCoreAsync();
            var rule = rules[Rules.IndexOf(editor)];
            _session.TestRule(rule, editor.Gift?.Coins ?? _catalog.Find(rule.GiftId)?.Coins ?? 1);
            _dialogs.Info("Bắn thử", $"Đã gửi sự kiện thử cho luật \"{(rule.Label.Length > 0 ? rule.Label : rule.Trigger)}\". Sự kiện thử không tính vào sổ công đức và thống kê.");
        }
        catch (Exception e) { _dialogs.Error(e.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void OpenWindow()
    {
        try { _session.OpenWindow(); }
        catch (Exception e) { _dialogs.Error(e.Message); }
    }

    [RelayCommand]
    private void OpenPreview()
    {
        try { _session.OpenWindow(true); }
        catch (Exception e) { _dialogs.Error(e.Message); }
    }

    [RelayCommand]
    private void CopyUrl()
    {
        try { Clipboard.SetText(Url); _dialogs.Info("Đã sao chép", "Dán URL vào OBS Browser Source, kích thước 1080 × 1920."); }
        catch (Exception e) { _dialogs.Error(e.Message); }
    }

    [RelayCommand]
    private async Task SimulateAsync(string kind)
    {
        if (!CanSimulate) return;
        Busy = true;
        try { await _session.SimulateAsync(kind == "storm" ? LiveEventKind.Gift : Enum.Parse<LiveEventKind>(kind), kind == "storm", CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception e) { _dialogs.Error(e.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task CreateBackgroundsAsync()
    {
        if (!CanConfigure) return;
        Busy = true;
        try
        {
            await _jobs.EnqueueAsync(new JobRequest(LiveGameThemeJob.JobType, "Chùa Online · tạo ba nền AI",
                new JsonObject { ["provider"] = ImageProvider }, JobLane.Cloud, Owner: JobOwner.Screen("S24")));
            _dialogs.Info("Đã xếp tác vụ", "Tạo ba ảnh và video nền. Theo dõi tiến độ ngay dưới màn này; hoàn tất rồi bắt đầu phiên để dùng nền mới.");
        }
        catch (Exception e) { _dialogs.Error(e.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RefreshWeeklyAsync()
    {
        try
        {
            var rows = await _store.TopAsync(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).AddDays(-6));
            Weekly.Clear(); foreach (var row in rows) Weekly.Add(row);
        }
        catch (Exception e) { _dialogs.Error(e.Message); }
    }

    private void Refresh()
    {
        Running = _session.IsRunning;
        Url = Running ? _session.Url : "";
        var state = _engine.Snapshot();
        Status = state.Status;
        Notice = state.Notice;
        Stats = $"{state.ViewerCount:N0} người xem · {state.Viewers.Count:N0} nhân vật · {state.Incense:N0} nén hương · {state.Prayers:N0} lời niệm · {state.Likes:N0} tim";
        Top.Clear(); foreach (var donor in state.Top) Top.Add(donor);
        while (_pending.TryDequeue(out var message)) { Events.Insert(0, message); if (Events.Count > 100) Events.RemoveAt(100); }
    }

    private void OnEvent(string message) { _pending.Enqueue(message); while (_pending.Count > 100) _pending.TryDequeue(out _); }
    private void OnError(string message) => Application.Current?.Dispatcher.BeginInvoke(() => _dialogs.Error(message));
    private void OnStatus(string message) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        var connection = message.StartsWith("Đã kết nối", StringComparison.Ordinal) ? "connected" : message.StartsWith("Mất kết nối", StringComparison.Ordinal) ? "lost" : "other";
        if (connection != _lastConnection && connection != "other") _dialogs.Info("TikFinity", message);
        _lastConnection = connection; Status = message;
    });

    public void Attach()
    {
        if (_attached) return; _attached = true;
        _engine.EventReceived += OnEvent; _engine.StatusChanged += OnStatus; _session.Error += OnError;
        _timer.Start(); Refresh();
    }
    public void Detach()
    {
        _attached = false; _timer.Stop();
        _engine.EventReceived -= OnEvent; _engine.StatusChanged -= OnStatus; _session.Error -= OnError;
    }
}

public sealed record LiveRuleOption(string Value, string Label);

public sealed partial class LiveRuleEditor : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial string Source { get; set; } = "gift";
    [ObservableProperty] public partial string GiftId { get; set; } = "";
    [ObservableProperty] public partial string Trigger { get; set; } = "";
    [ObservableProperty] public partial string Match { get; set; } = "exact";
    [ObservableProperty] public partial string ActionId { get; set; } = "incense";
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial int CooldownSeconds { get; set; } = 30;
    [ObservableProperty] public partial LiveGameObservedGift? Gift { get; set; }
    public bool IsGift => Source == "gift";
    public bool IsChat => Source == "chat";

    partial void OnSourceChanged(string value) { OnPropertyChanged(nameof(IsGift)); OnPropertyChanged(nameof(IsChat)); }

    // Chọn quà đã thấy thì điền mã và tên quà; lúc nạp (cùng mã) không ghi đè danh sách tên/từ khoá người dùng đã sửa.
    partial void OnGiftChanged(LiveGameObservedGift? value)
    {
        if (value is null || value.GiftId.Length > 0 && value.GiftId == GiftId) return;
        GiftId = value.GiftId;
        Trigger = value.GiftName;
    }

    public static LiveRuleEditor From(LiveGameRule r, LiveGameObservedGift? gift) => new()
    {
        Id = r.Id, Enabled = r.Enabled, Source = r.Source, GiftId = r.GiftId, Trigger = r.Trigger, Match = r.Match,
        ActionId = r.Action, Label = r.Label, CooldownSeconds = r.CooldownSeconds, Gift = gift
    };

    public LiveGameRule ToRule() => new()
    {
        Id = Id, Enabled = Enabled, Source = Source ?? "gift", GiftId = GiftId ?? "", Trigger = Trigger ?? "", Match = Match ?? "exact",
        Action = ActionId ?? "incense", Label = Label ?? "", CooldownSeconds = CooldownSeconds
    };
}

public sealed class GiftRuleEditor
{
    public int MinCoins { get; set; }
    public string Action { get; set; } = "";
    public string Label { get; set; } = "";
}
