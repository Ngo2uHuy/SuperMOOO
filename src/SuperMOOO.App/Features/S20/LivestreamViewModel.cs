using System.Collections.ObjectModel;
using System.IO;
using SuperMOOO.AI.Tts;
using SuperMOOO.App.Controls;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Workflows;
using SuperMOOO.Workflows.Avatars;
using SuperMOOO.Workflows.Live;

namespace SuperMOOO.App.Features.S20;

public sealed record AnswerSourceOption(string Id, string Label);

[Service(ServiceLifetime.Transient)]
public sealed partial class LivestreamViewModel : ObservableObject
{
    private static readonly int[] ReservedPorts = [8765, 18089, 18090, 18091];
    private static readonly string[] VideoExts = [".mp4", ".mov", ".webm", ".mkv", ".m4v"];

    private readonly ILiveStore _store;
    private readonly LiveAnswerService _answers;
    private readonly SalesScriptService _scripts;
    private readonly LiveSpeechService _speech;
    private readonly BroadcastServer _server;
    private readonly VoiceCatalogService _voices;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly IAudioPlayer _player;
    private readonly ShellService _shell;
    private readonly AvatarService _avatars;
    private readonly IJobQueue _jobs;
    private readonly ILipSyncEngine _lipSync;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private bool _loading;

    public LivestreamViewModel(ILiveStore store, LiveAnswerService answers, SalesScriptService scripts, LiveSpeechService speech,
        BroadcastServer server, VoiceCatalogService voices, ISettingsStore settings, IDialogService dialogs, IAudioPlayer player, ShellService shell,
        AvatarService avatars, IJobQueue jobs, ILipSyncEngine lipSync)
    {
        _avatars = avatars;
        _jobs = jobs;
        _lipSync = lipSync;
        _store = store;
        _answers = answers;
        _scripts = scripts;
        _speech = speech;
        _server = server;
        _voices = voices;
        _settings = settings;
        _dialogs = dialogs;
        _player = player;
        _shell = shell;
        LoadSettings();
    }

    public ObservableCollection<LiveProduct> Products { get; } = [];
    public ObservableCollection<LiveFaq> Faqs { get; } = [];
    public ObservableCollection<VoiceInfo> VoiceOptions { get; } = [];

    public IReadOnlyList<AnswerSourceOption> AnswerSources { get; } =
    [
        new(LiveAnswerSources.Local, "Model local + RAG (mặc định)"),
        new(LiveAnswerSources.Auto, "Ưu tiên FAQ rồi model local + RAG"),
        new(LiveAnswerSources.Cloud, "Model đám mây + RAG (gửi bình luận ra ngoài)")
    ];

    public IReadOnlyList<LivePlatform> Platforms => LiveCatalog.Platforms;
    public IReadOnlyList<LiveLayout> Layouts => LiveCatalog.Layouts;
    public IReadOnlyList<string> Fits { get; } = ["contain", "cover"];

    // Sản phẩm & FAQ
    [ObservableProperty]
    public partial LiveProduct? SelectedProduct { get; set; }

    [ObservableProperty]
    public partial string ProductName { get; set; } = "";

    [ObservableProperty]
    public partial string ProductFacts { get; set; } = "";

    [ObservableProperty]
    public partial string ProductScript { get; set; } = "";

    [ObservableProperty]
    public partial string ProductClip { get; set; } = "";

    [ObservableProperty]
    public partial LiveFaq? SelectedFaq { get; set; }

    [ObservableProperty]
    public partial string FaqQuestion { get; set; } = "";

    [ObservableProperty]
    public partial string FaqAnswer { get; set; } = "";

    [ObservableProperty]
    public partial string FaqClip { get; set; } = "";

    // Hỏi đáp thử
    [ObservableProperty]
    public partial string Question { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedSource { get; set; } = LiveAnswerSources.Local;

    [ObservableProperty]
    public partial bool SpeakAnswers { get; set; } = true;

    [ObservableProperty]
    public partial string VoiceId { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoBroadcast { get; set; } = true;

    [ObservableProperty]
    public partial string AnswerText { get; set; } = "";

    [ObservableProperty]
    public partial string AnswerNote { get; set; } = "";

    [ObservableProperty]
    public partial string ContextText { get; set; } = "";

    [ObservableProperty]
    public partial string? AnswerAudio { get; set; }

    [ObservableProperty]
    public partial string? AnswerClip { get; set; }

    [ObservableProperty]
    public partial bool LipSyncAnswer { get; set; }

    // Phát OBS
    [ObservableProperty]
    public partial LivePlatform SelectedPlatform { get; set; } = LiveCatalog.Platforms[^1];

    [ObservableProperty]
    public partial LiveLayout SelectedLayout { get; set; } = LiveCatalog.Layouts[0];

    [ObservableProperty]
    public partial string SelectedFit { get; set; } = "contain";

    [ObservableProperty]
    public partial string LoopVideo { get; set; } = "";

    [ObservableProperty]
    public partial int Port { get; set; } = 18120;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortEditable))]
    public partial bool ServerRunning { get; set; }

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = "";

    [ObservableProperty]
    public partial string BroadcastUrl { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SuggestScriptCommand), nameof(AskCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    public bool PortEditable => !ServerRunning;
    public string ObsDimensions => $"{SelectedLayout.Width} × {SelectedLayout.Height}";
    public string FramingNote => SelectedFit == "cover"
        ? "Video được cắt giữa khung. Kiểm tra để không mất khuôn mặt hoặc sản phẩm."
        : "Giữ nguyên tỷ lệ video và toàn bộ hình; phần trống có nền đen.";
    public string GuideTitle => $"Kết nối {SelectedPlatform.Name} qua OBS";
    public string StudioLabel => $"Mở {SelectedPlatform.StudioName}";

    public async Task LoadAsync()
    {
        await ReloadProductsAsync(SelectedProduct?.Id);
        try
        {
            var voices = await _voices.ListAsync("vi");
            VoiceOptions.Clear();
            foreach (var v in voices) VoiceOptions.Add(v);
            if (VoiceOptions.Count > 0 && VoiceOptions.All(v => v.Id != VoiceId)) VoiceId = VoiceOptions[0].Id;
        }
        catch (Exception e) when (e is ProviderException or InvalidOperationException or IOException)
        {
            // Chưa cài giọng: vẫn dùng giọng đã lưu, lỗi sẽ báo khi đọc.
            Status = "Chưa lấy được danh sách giọng đọc: " + e.Message;
        }
    }

    private void LoadSettings()
    {
        _loading = true;
        var s = _settings.Get<LiveSettings>("live");
        SelectedSource = s.AnswerSource;
        SpeakAnswers = s.SpeakAnswers;
        VoiceId = s.VoiceId;
        SelectedPlatform = LiveCatalog.Platform(s.Platform);
        ApplyProfile(s);
        LoopVideo = s.LoopVideoPath;
        Port = s.Port;
        _loading = false;
        RefreshServer();
    }

    private void ApplyProfile(LiveSettings s)
    {
        var profile = s.Profiles.GetValueOrDefault(SelectedPlatform.Id);
        SelectedLayout = LiveCatalog.Layout(string.IsNullOrEmpty(profile?.Layout) ? SelectedPlatform.DefaultLayout : profile.Layout);
        SelectedFit = profile?.Fit == "cover" ? "cover" : "contain";
    }

    private async Task SaveSettingsAsync()
    {
        if (_loading) return;
        var s = _settings.Get<LiveSettings>("live");
        s.AnswerSource = SelectedSource;
        s.SpeakAnswers = SpeakAnswers;
        s.VoiceId = string.IsNullOrEmpty(VoiceId) ? s.VoiceId : VoiceId;
        s.Platform = SelectedPlatform.Id;
        s.Profiles[SelectedPlatform.Id] = new LiveBroadcastProfile { Layout = SelectedLayout.Id, Fit = SelectedFit };
        s.LoopVideoPath = LoopVideo;
        if (Port is >= 1024 and <= 65535 && !ReservedPorts.Contains(Port)) s.Port = Port;
        await _settings.SetAsync("live", s);
    }

    partial void OnSelectedSourceChanged(string value) => _ = SaveSettingsAsync();
    partial void OnSpeakAnswersChanged(bool value) => _ = SaveSettingsAsync();
    partial void OnVoiceIdChanged(string value) => _ = SaveSettingsAsync();
    partial void OnLoopVideoChanged(string value) { _ = SaveSettingsAsync(); }

    partial void OnSelectedPlatformChanged(LivePlatform value)
    {
        if (_loading) return;
        _loading = true;
        ApplyProfile(_settings.Get<LiveSettings>("live"));
        _loading = false;
        _ = SaveSettingsAsync();
        RefreshFraming();
    }

    partial void OnSelectedLayoutChanged(LiveLayout value) { _ = SaveSettingsAsync(); RefreshFraming(); }
    partial void OnSelectedFitChanged(string value) { _ = SaveSettingsAsync(); RefreshFraming(); }
    partial void OnPortChanged(int value) => RefreshServer();

    private void RefreshFraming()
    {
        OnPropertyChanged(nameof(ObsDimensions));
        OnPropertyChanged(nameof(FramingNote));
        OnPropertyChanged(nameof(GuideTitle));
        OnPropertyChanged(nameof(StudioLabel));
        RefreshServer();
    }

    private void RefreshServer()
    {
        ServerRunning = _server.IsRunning;
        BroadcastUrl = _server.BuildUrl(SelectedPlatform.Id, SelectedLayout.Id, SelectedFit);
        ServerStatus = _server.IsRunning ? $"Đang chạy ở cổng {_server.Port} · hàng phát: {_server.QueueLength}" : "Máy chủ phát đang tắt.";
    }

    private void OnServerChanged(object? sender, EventArgs e)
    {
        if (_ui is null) RefreshServer();
        else _ui.Post(_ => RefreshServer(), null);
    }

    // Sản phẩm
    private async Task ReloadProductsAsync(string? selectId)
    {
        var list = await _store.ListProductsAsync();
        Products.Clear();
        foreach (var p in list) Products.Add(p);
        SelectedProduct = Products.FirstOrDefault(p => p.Id == selectId);
    }

    partial void OnSelectedProductChanged(LiveProduct? value)
    {
        ProductName = value?.Name ?? "";
        ProductFacts = value?.Facts ?? "";
        ProductScript = value?.Script ?? "";
        ProductClip = value?.ClipPath ?? "";
        _ = ReloadFaqsAsync(null);
    }

    private async Task ReloadFaqsAsync(string? selectId)
    {
        Faqs.Clear();
        if (SelectedProduct is { } p)
            foreach (var f in await _store.ListFaqsAsync(p.Id)) Faqs.Add(f);
        SelectedFaq = Faqs.FirstOrDefault(f => f.Id == selectId);
    }

    partial void OnSelectedFaqChanged(LiveFaq? value)
    {
        FaqQuestion = value?.Question ?? "";
        FaqAnswer = value?.Answer ?? "";
        FaqClip = value?.ClipPath ?? "";
    }

    [RelayCommand]
    private void NewProduct() => SelectedProduct = null;

    [RelayCommand]
    private async Task SaveProductAsync()
    {
        if (string.IsNullOrWhiteSpace(ProductName)) { _dialogs.Error("Nhập tên sản phẩm."); return; }
        var id = SelectedProduct?.Id ?? Guid.NewGuid().ToString("N");
        var clip = string.IsNullOrWhiteSpace(ProductClip) ? null : ProductClip.Trim();
        // Người dùng không tự đổi clip thì giữ clip_path mới nhất trong DB (job dựng clip có thể vừa ghi).
        if (SelectedProduct is { } cur && clip == cur.ClipPath)
            clip = (await _store.ListProductsAsync()).FirstOrDefault(x => x.Id == id)?.ClipPath ?? clip;
        await _store.UpsertProductAsync(new LiveProduct(id, ProductName.Trim(), ProductFacts.Trim(), ProductScript.Trim(), clip));
        await ReloadProductsAsync(id);
        _dialogs.Info("Livestream", "Đã lưu sản phẩm.");
    }

    [RelayCommand]
    private async Task DeleteProductAsync()
    {
        if (SelectedProduct is not { } p) return;
        if (!await _dialogs.ConfirmAsync("Xoá sản phẩm", $"Xoá \"{p.Name}\" cùng toàn bộ FAQ của sản phẩm này?")) return;
        await _store.DeleteProductAsync(p.Id);
        await ReloadProductsAsync(null);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SuggestScriptAsync()
    {
        IsBusy = true;
        Status = "Đang soạn gợi ý lời thoại…";
        try
        {
            ProductScript = await _scripts.SuggestAsync(ProductName, ProductFacts, CancellationToken.None);
            Status = "Đã gợi ý. Hãy kiểm tra giá và thông tin trước khi dùng.";
        }
        catch (Exception e) when (e is LiveBusyException or ProviderException or ArgumentException or InvalidOperationException)
        {
            Status = null;
            _dialogs.Error(e is ArgumentException or LiveBusyException ? e.Message : "Chưa có model trả lời: " + e.Message + " Bạn vẫn có thể tự nhập lời thoại.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void PickProductClip()
    {
        if (_dialogs.OpenFile("Chọn clip dựng sẵn của sản phẩm", "Video|*.mp4;*.mov;*.webm;*.mkv;*.m4v") is { } path) ProductClip = path;
    }

    [RelayCommand]
    private void NewFaq() => SelectedFaq = null;

    [RelayCommand]
    private async Task SaveFaqAsync()
    {
        if (SelectedProduct is not { } p) { _dialogs.Error("Chọn hoặc lưu một sản phẩm trước khi thêm FAQ."); return; }
        if (string.IsNullOrWhiteSpace(FaqQuestion) || string.IsNullOrWhiteSpace(FaqAnswer)) { _dialogs.Error("Nhập đủ câu hỏi và câu trả lời."); return; }
        var id = SelectedFaq?.Id ?? Guid.NewGuid().ToString("N");
        var clip = string.IsNullOrWhiteSpace(FaqClip) ? null : FaqClip.Trim();
        if (SelectedFaq is { } cur && clip == cur.ClipPath)
            clip = (await _store.ListFaqsAsync(p.Id)).FirstOrDefault(x => x.Id == id)?.ClipPath ?? clip;
        await _store.UpsertFaqAsync(new LiveFaq(id, p.Id, Truncate(FaqQuestion.Trim(), 300), Truncate(FaqAnswer.Trim(), LiveAnswerService.MaxAnswerChars), clip));
        await ReloadFaqsAsync(id);
    }

    [RelayCommand]
    private async Task DeleteFaqAsync()
    {
        if (SelectedFaq is not { } f) return;
        await _store.DeleteFaqAsync(f.Id);
        await ReloadFaqsAsync(null);
    }

    // Nhập/xuất hàng loạt dạng "câu hỏi | câu trả lời" mỗi dòng (như parseFaqs của AILive); tối đa 100 FAQ mỗi sản phẩm.
    private const int MaxFaqs = 100;

    private static readonly string[] SampleFaqs =
    [
        "Giá bao nhiêu vậy shop? | Dạ sản phẩm đang có giá ưu đãi trong buổi live hôm nay, chị bấm vào giỏ hàng để xem giá chính xác nha.",
        "Có ship tận nơi không? | Dạ shop giao hàng toàn quốc, nhận hàng kiểm tra rồi mới thanh toán ạ.",
        "Có những size nào? | Dạ sản phẩm có đủ size S, M, L, XL. Chị để lại chiều cao cân nặng để shop tư vấn size nha.",
        "Bảo hành bao lâu? | Dạ sản phẩm được đổi trả trong bảy ngày nếu có lỗi từ nhà sản xuất ạ."
    ];

    [ObservableProperty]
    public partial string FaqBulkText { get; set; } = "";

    [RelayCommand]
    private void ExportFaqs() => FaqBulkText = string.Join('\n', Faqs.Select(f => $"{OneLine(f.Question)} | {OneLine(f.Answer)}"));

    [RelayCommand]
    private Task AddSampleFaqsAsync() => ImportLinesAsync(SampleFaqs);

    [RelayCommand]
    private Task ImportFaqsAsync() => ImportLinesAsync(FaqBulkText.Split('\n'));

    private async Task ImportLinesAsync(IEnumerable<string> lines)
    {
        if (SelectedProduct is not { } p) { _dialogs.Error("Chọn hoặc lưu một sản phẩm trước khi thêm FAQ."); return; }
        var parsed = lines.Select(l => l.Split('|')).Where(parts => parts.Length >= 2)
            .Select(parts => (Q: Truncate(parts[0].Trim(), 300), A: Truncate(string.Join('|', parts.Skip(1)).Trim(), LiveAnswerService.MaxAnswerChars)))
            .Where(f => f.Q.Length > 0 && f.A.Length > 0).Take(MaxFaqs).ToList();
        if (parsed.Count == 0) { _dialogs.Error("Không có dòng hợp lệ. Mỗi dòng có dạng: câu hỏi | câu trả lời."); return; }

        var room = MaxFaqs - Faqs.Count;
        if (room <= 0) { _dialogs.Error($"Sản phẩm đã đủ {MaxFaqs} FAQ."); return; }
        foreach (var f in parsed.Take(room)) await _store.UpsertFaqAsync(new LiveFaq(Guid.NewGuid().ToString("N"), p.Id, f.Q, f.A));
        await ReloadFaqsAsync(null);
        var added = Math.Min(parsed.Count, room);
        _dialogs.Info("FAQ", added < parsed.Count ? $"Đã thêm {added}/{parsed.Count} FAQ (giới hạn {MaxFaqs})." : $"Đã thêm {added} FAQ.");
    }

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    // Dựng clip bằng avatar đang chọn ở S19; xong thì pipeline tự ghi clip_path của mục này (mở lại mục để thấy đường dẫn).
    [RelayCommand]
    private async Task BuildProductClipAsync()
    {
        if (SelectedProduct is not { } p) { _dialogs.Error("Chọn hoặc lưu một sản phẩm trước."); return; }
        await QueueClipAsync(ProductScript, "product", p.Id, null, p.Name);
    }

    [RelayCommand]
    private async Task BuildFaqClipAsync()
    {
        if (SelectedProduct is not { } p || SelectedFaq is not { } f) { _dialogs.Error("Chọn hoặc lưu một FAQ trước."); return; }
        await QueueClipAsync(FaqAnswer, "faq", f.Id, p.Id, p.Name + "_faq");
    }

    private async Task QueueClipAsync(string text, string kind, string id, string? productId, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) { _dialogs.Error("Chưa có lời thoại để dựng clip. Nhập hoặc bấm Gợi ý lời thoại rồi lưu."); return; }
        var avatarId = _settings.Get<LipSyncSettings>("lipsync").AvatarId;
        if (avatarId.Length == 0) { _dialogs.Error("Chưa chọn avatar. Thêm và chọn avatar ở màn Avatar khớp môi (S19)."); return; }
        if (string.IsNullOrEmpty(VoiceId)) { _dialogs.Error("Chưa chọn giọng đọc (tab Hỏi đáp thử)."); return; }
        var status = _lipSync.GetStatus();
        if (!status.Ready) { _dialogs.Error(status.Message); return; }
        try
        {
            using var owner = JobOwner.Begin(JobOwner.Screen("S20"));
            await _avatars.StartAsync(new AvatarVideoOptions
            {
                AvatarId = avatarId, Text = Truncate(text.Trim(), AvatarService.MaxTextChars), VoiceId = VoiceId, Name = name,
                LiveKind = kind, LiveId = id, LiveProductId = productId
            });
            _dialogs.Info("Dựng clip", "Đã đưa vào hàng đợi (theo dõi ở khung Tác vụ bên dưới). Trên CPU việc này rất chậm.");
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException) { _dialogs.Error(e.Message); }
    }

    // Job dựng clip xong: nạp lại clip_path mới của mục đang mở, không đụng phần người dùng đang sửa.
    public async Task OnJobSucceededAsync()
    {
        if (SelectedProduct is not { } p) return;
        var product = (await _store.ListProductsAsync()).FirstOrDefault(x => x.Id == p.Id);
        if (product is not null && SelectedProduct?.Id == p.Id && ProductClip == (p.ClipPath ?? "")) ProductClip = product.ClipPath ?? "";

        if (SelectedFaq is not { } f) return;
        var faq = (await _store.ListFaqsAsync(p.Id)).FirstOrDefault(x => x.Id == f.Id);
        if (faq is not null && SelectedFaq?.Id == f.Id && FaqClip == (f.ClipPath ?? "")) FaqClip = faq.ClipPath ?? "";
    }

    [RelayCommand]
    private void PickFaqClip()
    {
        if (_dialogs.OpenFile("Chọn clip trả lời dựng sẵn", "Video|*.mp4;*.mov;*.webm;*.mkv;*.m4v") is { } path) FaqClip = path;
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

    // Hỏi đáp thử
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task AskAsync()
    {
        if (string.IsNullOrWhiteSpace(Question)) return;
        IsBusy = true;
        _player.Stop();
        AnswerText = ContextText = AnswerNote = "";
        AnswerAudio = AnswerClip = null;
        Status = "Đang tìm câu trả lời…";
        try
        {
            var answer = await _answers.AnswerAsync(Question, SelectedSource, SelectedProduct, Faqs.ToList(), CancellationToken.None);
            AnswerText = answer.Text;
            if (answer.Source == "faq")
                AnswerNote = $"Khớp câu soạn sẵn: \"{answer.Faq!.Question}\" · độ khớp {Math.Round(answer.Score * 100)}%";
            else
            {
                AnswerNote = $"{Labels.Term(answer.ProviderId)} + RAG · truy xuất {answer.Sources.Count} đoạn · cần kiểm tra trước khi dùng thật";
                ContextText = answer.Sources.Count == 0
                    ? "Không có đoạn liên quan. Model vẫn có thể chào hỏi; bổ sung thông tin/FAQ cho các câu hỏi sản phẩm."
                    : string.Join(Environment.NewLine, answer.Sources.Select(s => $"{s.Id}: {s.Text}"));
            }

            if (answer.Faq?.ClipPath is { Length: > 0 } clip && File.Exists(clip)) AnswerClip = clip;

            if (SpeakAnswers && AnswerClip is null)
            {
                Status = "Đang đọc câu trả lời…";
                AnswerAudio = await _speech.SpeakAsync(answer.Text, CancellationToken.None);
                PlayAudio();
            }

            Enqueue(auto: true);
            if (LipSyncAnswer && AnswerAudio is not null) await QueueLipSyncAnswerAsync();
            else Status = "Hoàn tất.";
        }
        catch (Exception e) when (e is LiveBusyException or ProviderException or ArgumentException or InvalidOperationException or IOException)
        {
            Status = null;
            _dialogs.Error(AnswerText.Length > 0 ? "Đã trả lời bằng chữ nhưng chưa đọc được: " + e.Message : e.Message);
        }
        finally { IsBusy = false; }
    }

    // Khớp môi câu trả lời: job nền ở S19 (rất chậm trên CPU); xong thì clip thay cho giọng đọc trên OBS.
    private async Task QueueLipSyncAnswerAsync()
    {
        try
        {
            var avatarId = _settings.Get<LipSyncSettings>("lipsync").AvatarId;
            if (avatarId.Length == 0) throw new ArgumentException("Chưa chọn avatar. Thêm và chọn avatar ở màn Avatar khớp môi (S19).");
            using var owner = JobOwner.Begin(JobOwner.Screen("S20"));
            var jobId = await _avatars.StartAsync(new AvatarVideoOptions { AvatarId = avatarId, AudioPath = AnswerAudio, Name = "traloi" });
            Status = "Đã đọc câu trả lời. Đang dựng khẩu hình ở nền (rất chậm trên CPU, theo dõi ở khung Tác vụ bên dưới); xong sẽ tự phát clip.";
            _ = WatchClipAsync(jobId);
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException)
        {
            Status = "Hoàn tất (không dựng được khẩu hình).";
            _dialogs.Error(e.Message);
        }
    }

    private async Task WatchClipAsync(string jobId)
    {
        try
        {
            await foreach (var job in _jobs.WatchAsync(jobId, CancellationToken.None))
            {
                if (job.Status == JobStatus.Succeeded)
                {
                    var path = PipelineResult.Value(job.Result, "finalPath");
                    _ui?.Post(_ => ApplyClip(path), null);
                    return;
                }

                if (job.Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.WaitingManualRetry)
                {
                    _ui?.Post(_ => Status = "Dựng khẩu hình không thành công. Xem khung Tác vụ bên dưới.", null);
                    return;
                }
            }
        }
        catch (InvalidOperationException) { /* hàng đợi đóng khi app tắt */ }
    }

    private void ApplyClip(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        AnswerClip = path;
        Status = "Đã dựng xong clip khẩu hình.";
        Enqueue(auto: true);
    }

    [RelayCommand]
    private void PlayAudio()
    {
        if (AnswerAudio is null) return;
        try { _player.Play(AnswerAudio); }
        catch (IOException e) { _dialogs.Error(e.Message); }
    }

    [RelayCommand]
    private void SendToObs() => Enqueue(auto: false);

    private void Enqueue(bool auto)
    {
        if (auto && !AutoBroadcast) return;
        if (!_server.IsRunning)
        {
            if (!auto) _dialogs.Error("Máy chủ phát đang tắt. Bật ở tab Phát OBS.");
            return;
        }

        if (AnswerClip is not null) _server.Enqueue("clip", AnswerClip, AnswerText);
        else if (AnswerAudio is not null) _server.Enqueue("speech", AnswerAudio, AnswerText);
        else if (!auto) _dialogs.Error("Chưa có giọng đọc để phát. Bật \"Đọc thành tiếng\" rồi hỏi lại.");
    }

    // Phát OBS
    [RelayCommand]
    private async Task ToggleServerAsync()
    {
        try
        {
            if (_server.IsRunning)
            {
                await _server.StopAsync();
                _dialogs.Info("Phát OBS", "Đã tắt máy chủ phát.");
            }
            else
            {
                if (Port is < 1024 or > 65535 || ReservedPorts.Contains(Port))
                {
                    _dialogs.Error("Cổng cần từ 1024 đến 65535 và không trùng cổng OAuth (8765) hay TTS (18089–18091).");
                    return;
                }

                await SaveSettingsAsync();
                _server.Start();
                _dialogs.Info("Phát OBS", $"Máy chủ phát chạy ở cổng {_server.Port}. Dán URL vào OBS Browser Source.");
            }
        }
        catch (InvalidOperationException e) { _dialogs.Error(e.Message); }
        RefreshServer();
    }

    [RelayCommand]
    private void PickLoopVideo()
    {
        if (_dialogs.OpenFile("Chọn video nền lặp", "Video|*.mp4;*.mov;*.webm;*.mkv;*.m4v") is { } path && VideoExts.Contains(Path.GetExtension(path).ToLowerInvariant()))
            LoopVideo = path;
    }

    [RelayCommand]
    private void CopyUrl()
    {
        try
        {
            System.Windows.Clipboard.SetText(BroadcastUrl);
            _dialogs.Info("Phát OBS", "Đã sao chép URL cho OBS.");
        }
        catch (System.Runtime.InteropServices.ExternalException) { _dialogs.Error("Không ghi được vào clipboard. Chọn URL và nhấn Ctrl+C."); }
    }

    [RelayCommand]
    private void OpenBroadcast() => Open(BroadcastUrl);

    [RelayCommand]
    private void OpenStudio() => Open(SelectedPlatform.StudioUrl);

    [RelayCommand]
    private void OpenHelp() => Open(SelectedPlatform.HelpUrl);

    private void Open(string url)
    {
        try { _shell.OpenUrl(url); }
        catch (Exception e) when (e is ArgumentException or System.ComponentModel.Win32Exception) { _dialogs.Error(e.Message); }
    }

    private bool CanRun() => !IsBusy;

    public void Attach()
    {
        _server.StateChanged += OnServerChanged;
        RefreshServer();
    }

    public void Detach() => _server.StateChanged -= OnServerChanged;
}
