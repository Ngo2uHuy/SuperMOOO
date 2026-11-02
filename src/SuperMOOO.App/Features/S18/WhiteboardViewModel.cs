using SuperMOOO.AI.Llm;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Image;
using SuperMOOO.AI.Tts;
using System.Windows.Media;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S01;
using SuperMOOO.App.Features.S07;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Infrastructure.Tabular;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Steps;
using SuperMOOO.Workflows.TtsStudio;
using SuperMOOO.Workflows.Whiteboard;

namespace SuperMOOO.App.Features.S18;

public sealed record WhiteboardChoice(string Value, string Label);

/// <summary>S18: tab "Sản xuất" + "Thương hiệu"; mỗi chủ đề một job `whiteboard` (theo dõi ở khung Tác vụ của màn).</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class WhiteboardViewModel(VoiceCatalogService voices, ImageProviderRegistry images, IDialogService dialogs,
    WhiteboardService whiteboard, WhiteboardPreviewFeed feed, ISettingsStore settings, OAuthConnectService oauth, SubtitleStylePresets stylePresets,
    WhiteboardLibraryService library, VoiceSpeed voiceSpeed, IAudioPlayer player, ShellService shell, ModelProfileService models, LlmProviderRegistry llm) : ObservableObject
{
    public LlmPickerModel ScriptAi { get; } = new(llm, settings, dialogs);

    private static readonly string[] HeaderNames = ["topic", "chủ đề", "chu de", "tiêu đề", "title"];

    public static IReadOnlyList<WhiteboardChoice> Styles { get; } =
        [new("whiteboard", "Whiteboard (bảng trắng)"), new("stickman", "Stickman (người que)"), new("cartoon", "Cartoon (hoạt hình)"), new("infographic", "Infographic")];

    public static IReadOnlyList<WhiteboardChoice> Tones { get; } =
        [new("friendly", "Thân thiện, dễ hiểu"), new("professional", "Chuyên nghiệp"), new("humor", "Hài hước"), new("dramatic", "Kịch tính, cuốn hút"),
         new("inspiring", "Kể chuyện truyền cảm hứng"), new("psychology", "Phân tích tâm lý sâu sắc"), new("mystery", "Bí ẩn, gây tò mò"), new("educational", "Giáo dục, dễ hiểu")];

    public static IReadOnlyList<int> FpsChoices { get; } = [24, 25, 30, 60];

    public static IReadOnlyList<WhiteboardChoice> Qualities { get; } =
        [new("fast", "Nhanh (fast)"), new("balanced", "Cân bằng (balanced)"), new("high", "Cao (high)")];

    public static IReadOnlyList<WhiteboardChoice> Encoders { get; } =
        [new("auto", "Tự động"), new("x264", "CPU (libx264)"), new("nvenc", "NVIDIA NVENC"), new("qsv", "Intel QSV"), new("amf", "AMD AMF")];

    public static IReadOnlyList<WhiteboardChoice> Hands { get; } =
        [new(nameof(WhiteboardHandStyle.GoldPen), "Bút máy mạ vàng"), new(nameof(WhiteboardHandStyle.None), "Không hiện tay"), new(nameof(WhiteboardHandStyle.Custom), "Ảnh tay tuỳ chỉnh")];

    public IReadOnlyList<LanguageChoice> Languages { get; } = [.. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];

    public static IReadOnlyList<string> Aspects { get; } = ["16:9", "9:16"];

    public ObservableCollection<VoiceInfo> Voices { get; } = [];
    public IReadOnlyList<string> ImageSources { get; } = images.All.Select(p => p.Id).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopicSummary))]
    public partial string Topics { get; set; } = "";

    [ObservableProperty]
    public partial WhiteboardChoice SelectedStyle { get; set; } = Styles[0];

    [ObservableProperty]
    public partial WhiteboardChoice SelectedTone { get; set; } = Tones[0];

    [ObservableProperty]
    public partial int NumScenes { get; set; } = 6;

    [ObservableProperty]
    public partial string Aspect { get; set; } = "16:9";

    [ObservableProperty]
    public partial VoiceInfo? Voice { get; set; }

    [ObservableProperty] public partial LanguageChoice? SelectedLanguage { get; set; }
    [ObservableProperty] public partial string CustomTone { get; set; } = "";
    [ObservableProperty] public partial double Speed { get; set; } = 1;
    [ObservableProperty] public partial int SuggestCount { get; set; } = 5;
    [ObservableProperty] public partial int Fps { get; set; } = 30;
    [ObservableProperty] public partial WhiteboardChoice SelectedQuality { get; set; } = Qualities[1];
    [ObservableProperty] public partial WhiteboardChoice SelectedEncoder { get; set; } = Encoders[0];
    [ObservableProperty] public partial double DrawSpeed { get; set; } = 1;
    [ObservableProperty] public partial double ColorFadeSec { get; set; } = 0.8;
    [ObservableProperty] public partial double TailHoldSec { get; set; } = 0.4;
    [ObservableProperty] public partial WhiteboardChoice SelectedHand { get; set; } = Hands[0];
    [ObservableProperty] public partial string CustomHandPath { get; set; } = "";
    [ObservableProperty] public partial string OutputFolder { get; set; } = "";
    public ObservableCollection<WhiteboardLibraryItem> Library { get; } = [];
    [ObservableProperty] public partial WhiteboardLibraryItem? SelectedVideo { get; set; }
    public string OutputFolderHint => $"Để trống = {library.DefaultFolder()}";
    public bool IsCustomHand => SelectedHand.Value == nameof(WhiteboardHandStyle.Custom);
    public string TopicSummary => $"{TopicList().Length} chủ đề = {TopicList().Length} video sẽ xuất";

    [ObservableProperty]
    public partial string ImageSource { get; set; } = "";

    [ObservableProperty]
    public partial string Niche { get; set; } = "";

    public BrandingEditorModel Branding { get; } = new(settings, dialogs, stylePresets);
    public DestinationPickerViewModel Destinations { get; } = new(oauth);
    public static IReadOnlyList<string> DeliveryModesList => AutoWorkflowViewModel.DeliveryModesList;
    public static IReadOnlyList<string> Privacies => AutoWorkflowViewModel.Privacies;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrandingGuides))]
    public partial int TabIndex { get; set; }

    [ObservableProperty] public partial string IntroPath { get; set; } = "";
    [ObservableProperty] public partial string OutroPath { get; set; } = "";
    [ObservableProperty] public partial bool DeliverEnabled { get; set; }
    [ObservableProperty] public partial string DeliveryMode { get; set; } = DeliveryModes.Review;
    [ObservableProperty] public partial string DeliveryTitle { get; set; } = "";
    [ObservableProperty] public partial string DeliveryCaption { get; set; } = "";
    [ObservableProperty] public partial string Privacy { get; set; } = "private";
    [ObservableProperty] public partial int DelayMinutes { get; set; }
    [ObservableProperty] public partial int SpacingMinutes { get; set; } = 10;
    [ObservableProperty] public partial int DailyLimit { get; set; } = 20;

    /// <summary>Khung đầy đủ (1920x1080 hoặc 1080x1920, ảnh cảnh đặt trên nền trắng như bản dựng) để kéo thả phụ đề đúng cỡ chữ thật.</summary>
    [ObservableProperty]
    public partial BitmapSource? PreviewImage { get; set; }

    public bool ShowBrandingGuides => TabIndex == 1;

    private string? _lastScene;

    private string Language => SelectedLanguage?.Code ?? "vi";

    private string[] TopicList() => Topics.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    partial void OnAspectChanged(string value) => RenderPreview();

    partial void OnSelectedHandChanged(WhiteboardChoice value) => OnPropertyChanged(nameof(IsCustomHand));

    partial void OnSelectedLanguageChanged(LanguageChoice? value) { if (_ready) _ = ReloadVoicesAsync(); }

    private bool _ready;

    private async Task ReloadVoicesAsync()
    {
        IReadOnlyList<VoiceInfo> list;
        try { list = await voices.ListAsync(Language); }
        catch (ProviderException) { list = []; }
        var keep = Voice?.Id;
        Voices.Clear();
        foreach (var v in list) Voices.Add(v);
        var fallback = models.DefaultVoice(Language).Voice;
        Voice = Voices.FirstOrDefault(v => v.Id == keep) ?? Voices.FirstOrDefault(v => v.Id == fallback) ?? Voices.FirstOrDefault();
    }

    private void LoadForm()
    {
        var f = settings.Get<WhiteboardFormSettings>("whiteboard");
        SelectedStyle = Styles.FirstOrDefault(x => x.Value == f.Style) ?? Styles[0];
        SelectedTone = Tones.FirstOrDefault(x => x.Value == f.Tone) ?? Tones[0];
        (CustomTone, NumScenes, Aspect, Speed) = (f.CustomTone, Math.Clamp(f.SceneCount, 1, 30), f.Aspect is "9:16" ? "9:16" : "16:9", Math.Clamp(f.Speed, 0.5, 2));
        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == f.Language) ?? Languages[0];
        (Fps, DrawSpeed, ColorFadeSec, TailHoldSec) = (FpsChoices.Contains(f.Fps) ? f.Fps : 30, Math.Clamp(f.DrawSpeed, 0.5, 3), Math.Clamp(f.ColorFadeSec, 0, 3), Math.Clamp(f.TailHoldSec, 0, 2));
        SelectedQuality = Qualities.FirstOrDefault(x => x.Value == f.Quality) ?? Qualities[1];
        SelectedEncoder = Encoders.FirstOrDefault(x => x.Value == f.Encoder) ?? Encoders[0];
        SelectedHand = Hands.FirstOrDefault(x => x.Value == f.Hand.ToString()) ?? Hands[0];
        (CustomHandPath, OutputFolder) = (f.CustomHandPath, f.OutputFolder);
    }

    private Task SaveFormAsync() => settings.SetAsync("whiteboard", new WhiteboardFormSettings
    {
        Style = SelectedStyle.Value, Tone = SelectedTone.Value, CustomTone = CustomTone, Language = Language, SceneCount = NumScenes, Aspect = Aspect, Speed = Speed,
        Fps = Fps, Quality = SelectedQuality.Value, Encoder = SelectedEncoder.Value, DrawSpeed = DrawSpeed, ColorFadeSec = ColorFadeSec, TailHoldSec = TailHoldSec,
        Hand = Enum.Parse<WhiteboardHandStyle>(SelectedHand.Value), CustomHandPath = CustomHandPath, OutputFolder = OutputFolder
    });

    public async Task LoadAsync()
    {
        feed.SceneReady += OnSceneReady;
        ImageSource = images.Selected.Id;
        Branding.Load();
        LoadForm();
        RenderPreview();
        await ReloadVoicesAsync();
        _ready = true;
        await RefreshLibraryAsync();
        await Destinations.LoadAsync();
    }

    private void OnSceneReady(string image, string? clip) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _lastScene = image;
            RenderPreview();
        });

    private void RenderPreview()
    {
        var (w, h) = Aspect == "9:16" ? (1080, 1920) : (1920, 1080);
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                if (_lastScene is not null)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(_lastScene);
                    bitmap.DecodePixelWidth = w;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    var scale = Math.Min((double)w / bitmap.PixelWidth, (double)h / bitmap.PixelHeight);
                    var (iw, ih) = (bitmap.PixelWidth * scale, bitmap.PixelHeight * scale);
                    dc.DrawImage(bitmap, new Rect((w - iw) / 2, (h - ih) / 2, iw, ih));
                }
                else
                {
                    var hint = new FormattedText("Ảnh cảnh vừa tạo sẽ hiện ở đây khi chạy.", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), Math.Min(w, h) / 24.0, Brushes.Gray, 1.0);
                    dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
                }
            }

            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            PreviewImage = target;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException)
        {
            // ảnh xem trước hỏng thì giữ khung cũ, job vẫn chạy
        }
    }

    [RelayCommand]
    private void PickIntroOutro(string target)
    {
        if (dialogs.OpenFile(target == "intro" ? "Chọn video intro" : "Chọn video outro", "Video|*.mp4;*.mov;*.mkv|Tất cả|*.*") is not { } file) return;
        if (target == "intro") IntroPath = file;
        else OutroPath = file;
    }

    [RelayCommand]
    private void ImportTopics()
    {
        var path = dialogs.OpenFile("Nhập danh sách chủ đề", "Danh sách chủ đề (*.xlsx;*.csv;*.txt)|*.xlsx;*.csv;*.txt");
        if (path is null) return;
        try
        {
            IEnumerable<string> lines;
            if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                var table = XlsxLite.Read(stream);
                // Dòng đầu là tiêu đề cột thì bỏ, không thì nó cũng là một chủ đề.
                var first = table.Columns.Count > 0 ? table.Columns[0] : "";
                lines = (HeaderNames.Contains(first.Trim(), StringComparer.OrdinalIgnoreCase) ? [] : new[] { first })
                    .Concat(table.Rows.Select(r => r.Count > 0 ? r[0] : ""));
            }
            else lines = File.ReadAllLines(path);

            Topics = string.Join(Environment.NewLine, lines.Select(l => l.Trim()).Where(l => l.Length > 0));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            dialogs.Error($"Không đọc được file chủ đề: {e.Message}");
        }
    }

    [RelayCommand]
    private async Task SuggestTopicsAsync()
    {
        try
        {
            var found = await whiteboard.SuggestTopicsAsync(Niche, Language, SuggestCount, providerId: ScriptAi.ProviderId, model: ScriptAi.Model);
            Topics = string.Join(Environment.NewLine, new[] { Topics.TrimEnd() }.Where(t => t.Length > 0).Concat(found));
            dialogs.Info("Gợi ý chủ đề viral", $"Đã thêm {found.Count} chủ đề vào danh sách.");
        }
        catch (Exception e)
        {
            dialogs.Error($"Không gợi ý được chủ đề: {e.Message}");
        }
    }

    [RelayCommand]
    private void ClearTopics() => Topics = "";

    [RelayCommand]
    private void PickOutputFolder()
    {
        if (dialogs.PickFolder("Chọn thư mục xuất video vẽ tay") is { } folder) OutputFolder = folder;
    }

    [RelayCommand]
    private void PickHand()
    {
        if (dialogs.OpenFile("Chọn ảnh bàn tay (PNG nền trong suốt)", "Ảnh PNG (*.png)|*.png") is { } file && File.Exists(file)) CustomHandPath = file;
    }

    [RelayCommand]
    private async Task PreviewVoiceAsync()
    {
        if (Voice is null) { dialogs.Error("Chưa chọn giọng đọc."); return; }
        try { player.Play(await voiceSpeed.PreviewAsync(Voice.Id, Language, Speed, CancellationToken.None)); }
        catch (Exception e) when (e is not OperationCanceledException) { dialogs.Error($"Nghe thử thất bại: {e.Message}"); }
    }

    [RelayCommand]
    private async Task RefreshLibraryAsync()
    {
        try
        {
            var items = await library.ListAsync(Empty(OutputFolder));
            Library.Clear();
            foreach (var item in items) Library.Add(item);
        }
        catch (IOException e) { dialogs.Error($"Không đọc được thư viện: {e.Message}"); }
    }

    private void Open(string? path)
    {
        if (path is null) return;
        try { shell.OpenFile(path); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { dialogs.Error(e.Message); }
    }

    [RelayCommand] private void OpenVideo(WhiteboardLibraryItem item) => Open(item.VideoPath);

    [RelayCommand] private void OpenThumbnail(WhiteboardLibraryItem item) => Open(item.ThumbnailPath);

    [RelayCommand] private void OpenSeo(WhiteboardLibraryItem item) => Open(item.SeoPath);

    [RelayCommand]
    private void OpenItemFolder(WhiteboardLibraryItem item)
    {
        try { shell.RevealFile(item.VideoPath); }
        catch (IOException e) { dialogs.Error(e.Message); }
    }

    [RelayCommand]
    private void OpenRootFolder()
    {
        try { shell.OpenFolder(Empty(OutputFolder) ?? library.DefaultFolder()); }
        catch (IOException e) { dialogs.Error(e.Message); }
    }

    [RelayCommand]
    private async Task DeleteVideoAsync(WhiteboardLibraryItem item)
    {
        if (!await dialogs.ConfirmAsync("Xoá video", $"Xoá video \"{item.Title}\" (chuyển cả thư mục vào Thùng rác)?", "Xoá")) return;
        try
        {
            library.Delete(item);
            await RefreshLibraryAsync();
            dialogs.Info("Thư viện", "Đã chuyển video vào Thùng rác.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { dialogs.Error(e.Message); }
    }

    private static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [RelayCommand]
    private async Task StartAsync()
    {
        var topics = TopicList();
        if (topics.Length == 0) { dialogs.Error("Nhập ít nhất một chủ đề."); return; }
        if (Voice is null) { dialogs.Error("Chưa chọn giọng đọc."); return; }
        try
        {
            await SaveFormAsync();
            using var owner = JobOwner.Begin(JobOwner.Screen("S18"));
            var ids = await whiteboard.StartBatchAsync(topics, new WhiteboardOptions
            {
                ScriptProvider = ScriptAi.ProviderId, ScriptModel = ScriptAi.Model,
                Style = SelectedStyle.Value, Tone = Empty(CustomTone)?.Trim() ?? SelectedTone.Value, Language = Language, SceneCount = NumScenes, Aspect = Aspect,
                VoiceId = Voice.Id, Speed = Speed, OutputFolder = Empty(OutputFolder),
                Render = new WhiteboardRenderStepOptions
                {
                    Fps = Fps, Quality = SelectedQuality.Value, Encoder = SelectedEncoder.Value, DrawSpeed = DrawSpeed, ColorFadeSec = ColorFadeSec, TailHoldSec = TailHoldSec,
                    Hand = Enum.Parse<WhiteboardHandStyle>(SelectedHand.Value), CustomHandPath = IsCustomHand ? Empty(CustomHandPath) : null
                }, ImageProvider = ImageSource.Length > 0 ? ImageSource : null, Branding = Branding.Build(),
                IntroPath = Empty(IntroPath), OutroPath = Empty(OutroPath),
                Delivery = DeliverEnabled ? new DeliveryOptions
                {
                    Mode = DeliveryMode, DestinationIds = Destinations.SelectedIds, Title = DeliveryTitle, Caption = DeliveryCaption,
                    Privacy = Privacy, DelayMinutes = DelayMinutes, SpacingMinutes = SpacingMinutes, DailyLimit = DailyLimit
                } : null
            });
            dialogs.Info("Đã đưa vào hàng đợi", $"{ids.Count} video vẽ tay đang chờ dựng (theo dõi ở khung Tác vụ của màn này).");
        }
        catch (Exception e)
        {
            dialogs.Error(e.Message);
        }
    }
}
