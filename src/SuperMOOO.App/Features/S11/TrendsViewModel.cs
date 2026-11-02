using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using SuperMOOO.App.Controls;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Social.Crawlers;
using SuperMOOO.Social.Listening;
using SuperMOOO.Social.Reports;

namespace SuperMOOO.App.Features.S11;

public sealed record RegionOption(string Code, string Label);

public sealed partial class TrendModeOption(TrendMode mode, string label) : ObservableObject
{
    public TrendMode Mode => mode;
    public string Label => label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsSupported { get; set; } = true;

    [ObservableProperty]
    public partial string Hint { get; set; } = "";
}

public sealed partial class TrendVideoRow(TrendVideo video) : ObservableObject
{
    public ContentItem Item => video.Item;
    public string Title => Item.Title ?? Item.ExternalId;
    public string Channel => Item.AuthorName ?? Item.AuthorId ?? "";
    public long? Views => Item.ViewCount;
    public long? Likes => Item.LikeCount;
    public long? Comments => Item.CommentCount;
    public DateTime? Published => Item.PublishedAt?.LocalDateTime;

    // Tốc độ = lượt xem trung bình mỗi giờ kể từ lúc đăng (tối thiểu 1 giờ để video vừa đăng không vọt số).
    public double? ViewsPerHour => Views is { } v && Item.PublishedAt is { } at ? v / Math.Max(1, (DateTimeOffset.UtcNow - at).TotalHours) : null;

    public double? Engagement => video.EngagementRate
                                 ?? (Views is > 0 && (Likes is not null || Comments is not null) ? ((Likes ?? 0) + (Comments ?? 0)) / (double)Views.Value : null);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

public sealed record TrendHashtagRow(TrendHashtag Tag)
{
    public int Rank => Tag.Rank;
    public string Name => Tag.Name;
    public long? Posts => Tag.Posts;
    public long? Views => Tag.Views;
    public string Trend => Tag.Trend ?? "";
}

public sealed record TrendCreatorRow(TrendCreator Creator)
{
    public string Name => Creator.Name;
    public string Url => Creator.Url ?? "";
    public long? Followers => Creator.Followers;
    public int Videos => Creator.Videos;
    public long TotalViews => Creator.TotalViews;
}

/// <summary>Tab "Xu hướng" của S11: video trending, top hashtag, video theo hashtag, kênh hot; truy vấn nhanh gọi thẳng (không qua job queue).</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class TrendsViewModel : ObservableObject
{
    private readonly TrendService _trends;
    private readonly WatchlistService _watchlist;
    private readonly ReportExporter _exporter;
    private readonly ShellService _shell;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public TrendsViewModel(TrendService trends, WatchlistService watchlist, ReportExporter exporter, ShellService shell, IDialogService dialogs)
    {
        _trends = trends;
        _watchlist = watchlist;
        _exporter = exporter;
        _shell = shell;
        _dialogs = dialogs;
        foreach (var option in Modes) option.PropertyChanged += OnModeOptionChanged;
        UpdateCapabilities();
    }

    public IReadOnlyList<string> Platforms => TrendCatalog.Platforms;

    public IReadOnlyList<TrendModeOption> Modes { get; } =
    [
        new(TrendMode.Videos, "Video trending"), new(TrendMode.Hashtags, "Top hashtag"),
        new(TrendMode.HashtagVideos, "Video theo hashtag"), new(TrendMode.Creators, "Kênh hot")
    ];

    public ObservableCollection<RegionOption> Regions { get; } = [];
    public ObservableCollection<CategoryOption> Topics { get; } = [];
    public ObservableCollection<TrendVideoRow> Videos { get; } = [];
    public ObservableCollection<TrendHashtagRow> Hashtags { get; } = [];
    public ObservableCollection<TrendCreatorRow> Creators { get; } = [];

    /// <summary>Hàng đợi bên tab Theo dõi cần nạp lại sau khi thêm kênh/hashtag từ đây.</summary>
    public event EventHandler? WatchAdded;

    [ObservableProperty]
    public partial string Platform { get; set; } = "youtube";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideos), nameof(ShowHashtags), nameof(ShowCreators), nameof(KeywordHeader), nameof(KeywordPlaceholder))]
    public partial TrendMode Mode { get; set; } = TrendMode.Videos;

    [ObservableProperty]
    public partial RegionOption? SelectedRegion { get; set; }

    [ObservableProperty]
    public partial CategoryOption? SelectedTopic { get; set; }

    [ObservableProperty]
    public partial string Keyword { get; set; } = "";

    [ObservableProperty]
    public partial int Limit { get; set; } = 50;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsModeSupported { get; set; } = true;

    [ObservableProperty]
    public partial string ModeHint { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegionHint))]
    public partial bool HasRegions { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopicHint))]
    public partial bool HasTopics { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial int ResultCount { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    [ObservableProperty]
    public partial TrendVideoRow? SelectedVideo { get; set; }

    [ObservableProperty]
    public partial TrendHashtagRow? SelectedHashtag { get; set; }

    [ObservableProperty]
    public partial TrendCreatorRow? SelectedCreator { get; set; }

    public string? RegionHint => HasRegions ? null : "Nguồn này không lọc theo khu vực (bảng xếp hạng chung hoặc chỉ có dữ liệu Trung Quốc).";
    public string? TopicHint => HasTopics ? null : "Nguồn của chế độ này không chia theo chủ đề; dùng ô từ khoá để lọc.";
    public bool ShowVideos => Mode is TrendMode.Videos or TrendMode.HashtagVideos;
    public bool ShowHashtags => Mode == TrendMode.Hashtags;
    public bool ShowCreators => Mode == TrendMode.Creators;
    public string KeywordHeader => Mode == TrendMode.HashtagVideos ? "Hashtag / từ khoá" : "Lọc theo từ khoá (tuỳ chọn)";
    public string KeywordPlaceholder => Mode == TrendMode.HashtagVideos ? "#tên_hashtag" : "Chỉ giữ kết quả có chữ này";

    partial void OnPlatformChanged(string value) => UpdateCapabilities();

    partial void OnModeChanged(TrendMode oldValue, TrendMode newValue)
    {
        // Hashtag vừa xem không nên thành bộ lọc ngầm của chế độ khác.
        if (oldValue == TrendMode.HashtagVideos) Keyword = "";
        UpdateCapabilities();
    }

    private void OnModeOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrendModeOption.IsSelected) && sender is TrendModeOption { IsSelected: true } option) Mode = option.Mode;
    }

    /// <summary>Khoá chế độ/khu vực/chủ đề theo bảng khả năng của nền tảng; đổi nền tảng/chế độ thì xoá kết quả cũ để không lẫn.</summary>
    private void UpdateCapabilities()
    {
        foreach (var option in Modes)
        {
            var support = TrendCatalog.Support(Platform, option.Mode);
            option.IsSupported = support.Supported;
            option.Hint = support.Hint;
        }

        if (!TrendCatalog.Support(Platform, Mode).Supported && Modes.FirstOrDefault(m => m.IsSupported) is { } fallback)
        {
            Mode = fallback.Mode;      // OnModeChanged gọi lại hàm này
            return;
        }

        foreach (var option in Modes) option.IsSelected = option.Mode == Mode;
        var current = TrendCatalog.Support(Platform, Mode);
        IsModeSupported = current.Supported;
        ModeHint = current.Hint;

        var previousRegion = SelectedRegion?.Code;
        var codes = TrendCatalog.Regions(Platform, Mode);
        Regions.Clear();
        foreach (var code in codes) Regions.Add(new RegionOption(code, $"{TrendCatalog.RegionNames[code]} ({code})"));
        HasRegions = Regions.Count > 0;
        if (!HasRegions) Regions.Add(new RegionOption("", "Không áp dụng"));
        SelectedRegion = Regions.FirstOrDefault(r => r.Code == previousRegion) ?? Regions.FirstOrDefault(r => r.Code == "VN") ?? Regions[0];

        var previousTopic = SelectedTopic?.Key;
        Topics.Clear();
        foreach (var topic in TrendCatalog.Topics(Platform, Mode)) Topics.Add(topic);
        HasTopics = Topics.Count > 0;
        if (!HasTopics) Topics.Add(new CategoryOption(PlatformCategories.All.Key, "Không áp dụng"));
        SelectedTopic = Topics.FirstOrDefault(t => t.Key == previousTopic) ?? Topics[0];

        ClearResults();
    }

    private void ClearResults()
    {
        Videos.Clear();
        Hashtags.Clear();
        Creators.Clear();
        ResultCount = 0;
        Note = null;
        EmptyText = IsModeSupported ? "Chọn bộ lọc rồi bấm \"Lấy dữ liệu\"." : ModeHint;
    }

    [RelayCommand]
    private Task FetchAsync() => RunAsync(false);

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(true);

    private async Task RunAsync(bool refresh)
    {
        var support = TrendCatalog.Support(Platform, Mode);
        if (!support.Supported)
        {
            _dialogs.Error(support.Hint);
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        Status = "Đang lấy dữ liệu…";
        try
        {
            var result = await _trends.GetAsync(new TrendQuery(Platform, Mode, SelectedRegion?.Code, SelectedTopic?.Key, Keyword, Limit), refresh, cts.Token);
            Show(result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // đã có truy vấn mới thay thế
        }
        catch (Exception ex) when (ex is ProviderException or ArgumentException or NotSupportedException or InvalidOperationException or System.IO.IOException)
        {
            Status = null;
            _dialogs.Error(Describe(ex));
        }
        finally
        {
            if (_cts == cts) IsBusy = false;
        }
    }

    private string Describe(Exception ex) => ex switch
    {
        ProviderException { Kind: ProviderErrorKind.Network or ProviderErrorKind.Timeout } p =>
            $"{Labels.Term(Platform)}: không kết nối được nguồn ({p.Message}). Kiểm tra mạng/proxy rồi thử lại.",
        ProviderException { Kind: ProviderErrorKind.RateLimited or ProviderErrorKind.Forbidden } p =>
            $"{Labels.Term(Platform)}: nguồn đang chặn hoặc giới hạn tần suất ({p.Message}). Thử lại sau vài phút.",
        ProviderException p => $"{Labels.Term(Platform)}: {p.Message}",
        _ => ex.Message
    };

    private void Show(TrendResult result)
    {
        ClearResults();
        foreach (var video in result.Videos) Videos.Add(new TrendVideoRow(video));
        foreach (var tag in result.Hashtags) Hashtags.Add(new TrendHashtagRow(tag));
        foreach (var creator in result.Creators) Creators.Add(new TrendCreatorRow(creator));
        ResultCount = result.Count;
        Note = result.Note;
        EmptyText = "Nguồn không trả kết quả nào cho bộ lọc này.";
        var at = result.FetchedAt.ToLocalTime().ToString("HH:mm");
        Status = $"{result.Count} kết quả · Nguồn: {result.Source} · " + (result.FromCache ? $"bộ nhớ đệm lúc {at}, bấm Làm mới để lấy lại" : $"lấy lúc {at}");
    }

    private string? SelectedUrl => Mode switch
    {
        TrendMode.Hashtags => SelectedHashtag?.Tag.Url,
        TrendMode.Creators => SelectedCreator?.Creator.Url,
        _ => SelectedVideo?.Item.Url
    };

    [RelayCommand]
    private void OpenLink()
    {
        if (SelectedUrl is not { Length: > 0 } url)
        {
            Status = "Chọn một dòng có liên kết trước.";
            return;
        }

        try
        {
            _shell.OpenUrl(url);
        }
        catch (ArgumentException ex)
        {
            _dialogs.Error(ex.Message);
        }
    }

    /// <summary>S09 chưa có cơ chế nhận link từ màn khác nên chép vào clipboard để dán ở ô liên kết của Tải video.</summary>
    [RelayCommand]
    private void CopyForDownload()
    {
        var urls = Videos.Where(v => v.IsChecked).Select(v => v.Item.Url).OfType<string>().ToList();
        if (urls.Count == 0 && SelectedVideo?.Item.Url is { } single) urls.Add(single);
        if (urls.Count == 0)
        {
            Status = "Tích chọn video (cột đầu) hoặc chọn một dòng trước.";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, urls));
            _dialogs.Info("Đã chép liên kết", $"{urls.Count} liên kết đã ở clipboard. Mở màn Tải video (S09), bấm Dán ở ô liên kết rồi Tải.");
        }
        catch (ExternalException ex)
        {
            _dialogs.Error($"Không ghi được clipboard: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task WatchAsync()
    {
        (string Name, string Url, string Type)? target = Mode switch
        {
            TrendMode.Hashtags when SelectedHashtag is { } h => (h.Name, TrendCatalog.WatchHashtag(Platform, h.Name).Url, TrendCatalog.WatchHashtag(Platform, h.Name).Type),
            TrendMode.Creators when SelectedCreator is { Creator.Url: { } url } c => (c.Name, url, "channel"),
            TrendMode.Videos or TrendMode.HashtagVideos when SelectedVideo is { } v && TrendCatalog.ChannelUrl(Platform, v.Item.AuthorId) is { } url => (v.Channel, url, "channel"),
            _ => null
        };
        if (target is not { } t)
        {
            Status = Platform == "instagram" ? "Graph API không trả tài khoản đăng bài nên không thêm theo dõi được." : "Chọn một dòng có kênh/hashtag trước.";
            return;
        }

        try
        {
            await _watchlist.AddAsync(t.Name, t.Url, Platform, t.Type);
            _dialogs.Info("Đã thêm theo dõi", $"\"{t.Name}\" đã vào danh sách ở tab Theo dõi (kiểm tra mỗi 24 giờ).");
            WatchAdded?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException ex)
        {
            _dialogs.Error(ex.Message);
        }
    }

    /// <summary>Hashtag → chế độ "Video theo hashtag"; nguồn không có video theo hashtag (Douyin) thì mở trang tìm kiếm trên web.</summary>
    [RelayCommand]
    private async Task ShowHashtagVideosAsync()
    {
        if (SelectedHashtag is not { } tag) return;
        if (!TrendCatalog.Support(Platform, TrendMode.HashtagVideos).Supported)
        {
            OpenLink();
            return;
        }

        Mode = TrendMode.HashtagVideos;
        Keyword = tag.Name;
        await RunAsync(false);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (ResultCount == 0)
        {
            Status = "Chưa có kết quả để xuất.";
            return;
        }

        var path = _dialogs.SaveFile("Xuất kết quả xu hướng", "CSV (*.csv)|*.csv|Excel (*.xlsx)|*.xlsx",
            $"xu-huong-{Platform}-{Mode.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        if (path is null) return;

        static string N(double? value, string format = "0") => value?.ToString(format, CultureInfo.InvariantCulture) ?? "";
        var (columns, rows) = Mode switch
        {
            TrendMode.Hashtags => ((IReadOnlyList<string>)["Hạng", "Hashtag / chủ đề", "Số bài", "Lượt xem", "Xu hướng", "Liên kết"],
                Hashtags.Select(h => (IReadOnlyList<string>)[N(h.Rank), h.Name, N(h.Posts), N(h.Views), h.Trend, h.Tag.Url ?? ""]).ToList()),
            TrendMode.Creators => (["Kênh", "Liên kết", "Người theo dõi", "Số video trong trending", "Tổng lượt xem"],
                Creators.Select(c => (IReadOnlyList<string>)[c.Name, c.Url, N(c.Followers), N(c.Videos), N(c.TotalViews)]).ToList()),
            _ => (["Tiêu đề", "Kênh", "Lượt xem", "Thích", "Bình luận", "Ngày đăng", "Lượt xem/giờ", "Tỉ lệ tương tác (%)", "Liên kết"],
                Videos.Select(v => (IReadOnlyList<string>)[v.Title, v.Channel, N(v.Views), N(v.Likes), N(v.Comments), v.Published?.ToString("yyyy-MM-dd HH:mm") ?? "",
                    N(v.ViewsPerHour), N(v.Engagement * 100, "0.00"), v.Item.Url ?? ""]).ToList())
        };

        try
        {
            await _exporter.ExportTableAsync(columns, rows, path, "Xu hướng");
            _dialogs.Info("Đã xuất", $"{rows.Count} dòng → {path}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _dialogs.Error(ex.Message);
        }
    }
}
