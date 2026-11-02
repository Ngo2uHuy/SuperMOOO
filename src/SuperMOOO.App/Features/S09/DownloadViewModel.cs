using System.IO;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Data.Repositories.Files;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Social.Download;

namespace SuperMOOO.App.Features.S09;

public sealed record FormatOption(DownloadFormat Value, string Label);
public sealed record CatalogSortOption(string Value, string Label);

public sealed partial class HistoryRow(MediaFileRecord file) : ObservableObject
{
    public MediaFileRecord File { get; } = file;
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public string Name => System.IO.Path.GetFileName(File.Path);
    public string Path => File.Path;
    public string Kind => Labels.FileKind(File.Kind);
    public string Operation => Labels.FileOperation(File.Operation);
    public string Size => File.SizeBytes is { } b ? $"{b / 1048576.0:N1} MB" : "";
    public string Source => File.SourceUrl ?? "";
}

/// <summary>Thẻ nền tảng: chỉ đổi gợi ý nhập liệu, tải vẫn qua yt-dlp theo link. Thẻ Disabled chỉ để hiển thị, không có chức năng.</summary>
public sealed record PlatformCard(string Id, string Name, string Placeholder, string Hint, bool NeedsCookie = false, bool Disabled = false)
{
    public string Badge => Disabled ? "Đang phát triển" : "";
    public double Dim => Disabled ? 0.5 : 1;
}

public sealed partial class CatalogRow(CatalogEntry entry) : ObservableObject
{
    public CatalogEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public string Author => Entry.Author ?? "";
    public string Duration => Entry.Duration is { } d ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss")) : "";
    public string Published => Entry.PublishedAt?.ToString("dd/MM/yyyy") ?? "";
    public string Views => Entry.ViewCount?.ToString("N0") ?? "";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial ImageSource? Thumb { get; set; }
}

[Service(ServiceLifetime.Transient)]
public sealed partial class DownloadViewModel(BatchDownloadService batch, UrlListImporter importer,
    IDownloader downloader, VideoCatalogService catalog, IHttpClientProvider http, MediaLibraryService library,
    DownloadFolders folders, ISettingsStore settings, IDialogService dialogs, ShellService shell) : ObservableObject
{
    private const string ExcelFilter = "Danh sách link (*.xlsx;*.csv;*.txt)|*.xlsx;*.csv;*.txt";
    private const string NotReady = "Nền tảng này đang phát triển, chưa tải được.";
    private const string CookieNote = " Nền tảng này thường cần cookie: nhập ở Cài đặt > Nền tảng.";
    private const string DefaultPlaceholder = "https://www.youtube.com/watch?v=…\nhttps://www.tiktok.com/@kenh/video/…";
    private CancellationTokenSource? _thumbs;
    private PlatformCard? _platform;
    private IReadOnlyList<CatalogEntry> _catalogEntries = [];
    private string _catalogTitle = "";
    private string? _catalogUrl;
    private string? _catalogSearchPlatform;
    private string? _catalogSearchKeyword;
    private DateTime? _appliedFrom;
    private DateTime? _appliedTo;
    private string _appliedSort = "default";

    public IReadOnlyList<PlatformCard> ChinaPlatforms { get; } =
    [
        new("douyin", "Douyin", "https://www.douyin.com/video/…\nhttps://v.douyin.com/… (link chia sẻ)", "Link dạng douyin.com/video/… hoặc v.douyin.com/… (link chia sẻ).", NeedsCookie: true),
        new("bilibili", "Bilibili", "https://www.bilibili.com/video/BV…\nhttps://b23.tv/…", "Link dạng bilibili.com/video/BV… hoặc b23.tv/…; link danh sách/series: bấm \"Xem thông tin\" để chọn tập. Có ô tìm theo từ khoá.", NeedsCookie: true),
        new("kuaishou", "Kuaishou", "https://www.kuaishou.com/short-video/…\nhttps://v.kuaishou.com/…", "Link dạng kuaishou.com/short-video/… hoặc v.kuaishou.com/…; yt-dlp có thể chưa hỗ trợ đủ nền tảng này.", NeedsCookie: true),
        new("weibo", "Weibo", "https://weibo.com/tv/show/…\nhttps://weibo.com/…", "Link dạng weibo.com/tv/show/… hoặc bài viết có video.", NeedsCookie: true),
        new("toutiao", "Toutiao / Xigua", "https://www.ixigua.com/…\nhttps://www.toutiao.com/video/…", "Link dạng ixigua.com/… hoặc toutiao.com/video/…."),
        new("mgtv", "Mango TV", "https://www.mgtv.com/b/…/….html", "Link dạng mgtv.com/b/…/….html; chỉ tải được nội dung xem miễn phí, công khai."),
        new("acfun", "AcFun", "https://www.acfun.cn/v/ac…", "Link dạng acfun.cn/v/ac…."),
        new("hongguo", "Hongguo (红果短剧)", "", "", Disabled: true)
    ];

    public IReadOnlyList<PlatformCard> GlobalPlatforms { get; } =
    [
        new("youtube", "YouTube", "https://www.youtube.com/watch?v=…\nhttps://youtu.be/…", "Link video, Shorts hoặc playlist (youtube.com/playlist?list=…). Có ô tìm theo từ khoá."),
        new("tiktok", "TikTok", "https://www.tiktok.com/@kenh/video/…\nhttps://vm.tiktok.com/…", "Link dạng tiktok.com/@kênh/video/… hoặc vm.tiktok.com/….", NeedsCookie: true),
        new("facebook", "Facebook", "https://www.facebook.com/watch/?v=…\nhttps://fb.watch/…", "Link dạng facebook.com/watch/?v=…, /reel/… hoặc fb.watch/….", NeedsCookie: true),
        new("instagram", "Instagram", "https://www.instagram.com/reel/…\nhttps://www.instagram.com/p/…", "Link dạng instagram.com/reel/… hoặc /p/….", NeedsCookie: true),
        new("other", "Khác", DefaultPlaceholder, "Dán link của bất kỳ trang nào yt-dlp hỗ trợ."),
        new("reelshort", "ReelShort", "", "", Disabled: true),
        new("plex", "Plex", "", "", Disabled: true)
    ];

    public ObservableCollection<CatalogRow> Entries { get; } = [];
    public IReadOnlyList<CatalogSortOption> SortOptions { get; } =
    [
        new("default", "Mặc định"), new("latest", "Mới nhất"),
        new("oldest", "Cũ nhất"), new("popular", "Phổ biến nhất")
    ];

    [ObservableProperty]
    public partial DateTime? DateFrom { get; set; }

    [ObservableProperty]
    public partial DateTime? DateTo { get; set; }

    [ObservableProperty]
    public partial string SortBy { get; set; } = "default";

    private bool HasCatalogFilter => DateFrom is not null || DateTo is not null || SortBy != "default";
    private bool FilterChanged => _appliedFrom != DateFrom || _appliedTo != DateTo || _appliedSort != SortBy;

    [ObservableProperty]
    public partial PlatformCard? SelectedChina { get; set; }

    [ObservableProperty]
    public partial PlatformCard? SelectedGlobal { get; set; }

    [ObservableProperty]
    public partial string UrlPlaceholder { get; set; } = DefaultPlaceholder;

    [ObservableProperty]
    public partial string PlatformHint { get; set; } = "";

    [ObservableProperty]
    public partial bool CanSearch { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string EntriesTitle { get; set; } = "";

    partial void OnSelectedChinaChanged(PlatformCard? value)
    {
        if (value is null) return;
        SelectedGlobal = null;
        Apply(value);
    }

    partial void OnSelectedGlobalChanged(PlatformCard? value)
    {
        if (value is null) return;
        SelectedChina = null;
        Apply(value);
    }

    private void Apply(PlatformCard card)
    {
        if (card.Disabled)
        {
            _platform = null;
            CanSearch = false;
            PlatformHint = NotReady;
            Status = NotReady;
            return;
        }

        _platform = card;
        UrlPlaceholder = card.Placeholder;
        PlatformHint = card.Hint + (card.NeedsCookie ? CookieNote : "");
        CanSearch = VideoCatalogService.CanSearch(card.Id);
        Status = null;
    }

    public IReadOnlyList<DownloadFormat> Formats { get; } = Enum.GetValues<DownloadFormat>();

    public IReadOnlyList<FormatOption> FormatOptions { get; } = [.. Enum.GetValues<DownloadFormat>().Select(f => new FormatOption(f, Labels.Format(f)))];
    public ObservableCollection<HistoryRow> History { get; } = [];

    [ObservableProperty]
    public partial string UrlsText { get; set; } = "";

    [ObservableProperty]
    public partial DownloadFormat SelectedFormat { get; set; }

    [ObservableProperty]
    public partial bool NoWatermark { get; set; } = true;

    [ObservableProperty]
    public partial string FolderText { get; set; } = "";

    [ObservableProperty]
    public partial string InfoText { get; set; } = "";

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial HistoryRow? Selected { get; set; }

    public async Task LoadAsync()
    {
        var defaults = settings.Get<DownloadSettings>("download");
        SelectedFormat = defaults.Format;
        NoWatermark = defaults.NoWatermark;
        FolderText = folders.Resolve();
        SelectedGlobal ??= GlobalPlatforms[0];
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var rows = await library.HistoryAsync();
        var checkedPaths = History.Where(r => r.IsChecked).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedPath = Selected?.Path;
        History.Clear();
        foreach (var row in rows) History.Add(new HistoryRow(row) { IsChecked = checkedPaths.Contains(row.Path) });
        Selected = History.FirstOrDefault(r => string.Equals(r.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private Task StartAsync() => Guard(async () =>
    {
        var urls = UrlsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<string> selectedUrls = urls;
        if (HasCatalogFilter)
        {
            ValidateDates();
            var found = new List<CatalogEntry>();
            foreach (var url in urls)
            {
                var items = await catalog.ListAsync(url, VideoCatalogService.MaxEntries, CancellationToken.None, detailed: true);
                found.AddRange(items);
            }
            selectedUrls = VideoCatalogService.Filter(found, DateFrom, DateTo, SortBy)
                .Select(e => e.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (selectedUrls.Count == 0) throw new ArgumentException("Không có video phù hợp bộ lọc trong các mục đã lấy. Video thiếu ngày đăng không thể khớp khoảng ngày.");
        }
        using var owner = JobOwner.Begin(JobOwner.Screen("S09"));
        var ids = await batch.EnqueueAsync(new BatchDownloadRequest(selectedUrls, SelectedFormat, NoWatermark, FolderText));
        Status = $"Đã đưa {ids.Count} liên kết vào hàng đợi tải (theo dõi ở khung Tác vụ của màn này).";
        dialogs.Info("Đã đưa vào hàng đợi", Status);
    });

    [RelayCommand]
    private Task ImportFileAsync() => Guard(async () =>
    {
        var path = dialogs.OpenFile("Nhập danh sách link", ExcelFilter);
        if (path is null) return;
        var imported = await importer.ReadAsync(path);
        UrlsText = string.Join('\n', imported.Urls);
        Status = $"Đọc được {imported.Urls.Count} liên kết từ {imported.FileName}.";
    });

    [RelayCommand]
    private Task SaveTemplateAsync() => Guard(async () =>
    {
        var path = dialogs.SaveFile("Lưu Excel mẫu", "Excel (*.xlsx)|*.xlsx", "Link_Download_Template.xlsx");
        if (path is null) return;
        await importer.WriteTemplateAsync(path);
        Status = "Đã lưu Excel mẫu.";
    });

    [RelayCommand]
    private Task InfoAsync() => Guard(async () =>
    {
        ValidateDates();
        var url = UrlsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new ArgumentException("Nhập một liên kết để xem thông tin.");
        Status = "Đang lấy thông tin…";
        try
        {
            var items = await catalog.ListAsync(url, VideoCatalogService.MaxEntries, CancellationToken.None, detailed: HasCatalogFilter);
            if (items.Count > 1 || HasCatalogFilter)
            {
                _catalogUrl = url;
                _catalogSearchPlatform = null;
                _catalogSearchKeyword = null;
                Show(items, $"Danh sách ({items.Count} mục)");
                InfoText = $"Đã lấy {items.Count} mục đầu (tối đa {VideoCatalogService.MaxEntries}); bộ lọc chỉ áp dụng cho các mục đã lấy. Mục thiếu ngày đăng không khớp khoảng ngày.";
                Status = null;
                return;
            }
        }
        catch (DownloadException) when (!HasCatalogFilter)
        {
            // Không liệt kê được: thử đọc như một video đơn (có đường dự phòng riêng cho Bilibili).
        }

        Entries.Clear();
        EntriesTitle = "";
        _catalogEntries = [];
        _catalogTitle = "";
        _catalogUrl = null;
        _catalogSearchPlatform = null;
        _catalogSearchKeyword = null;
        var info = await downloader.GetInfoAsync(url, CancellationToken.None);
        InfoText = $"{info.Title}\nNền tảng: {info.Platform} · Tác giả: {info.Author ?? "?"} · Thời lượng: {info.Duration?.ToString(@"hh\:mm\:ss") ?? "?"}";
        Status = null;
    });

    [RelayCommand]
    private Task SearchAsync() => Guard(async () =>
    {
        ValidateDates();
        if (_platform is null || !CanSearch) throw new NotSupportedException("Nền tảng đang chọn chưa hỗ trợ tìm kiếm theo từ khoá.");
        Status = "Đang tìm…";
        var keyword = SearchText.Trim();
        var items = await catalog.SearchAsync(_platform.Id, keyword, 20, CancellationToken.None, detailed: HasCatalogFilter);
        _catalogUrl = null;
        _catalogSearchPlatform = _platform.Id;
        _catalogSearchKeyword = keyword;
        Show(items, $"Kết quả tìm \"{SearchText.Trim()}\" trên {_platform.Name} ({items.Count})");
        InfoText = "Bộ lọc chỉ áp dụng cho 20 kết quả tìm kiếm đầu. Mục thiếu ngày đăng không khớp khoảng ngày.";
        Status = null;
    });

    [RelayCommand]
    private void Paste()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            var text = System.Windows.Clipboard.GetText().Trim();
            if (text.Length > 0) UrlsText = UrlsText.Trim().Length == 0 ? text : UrlsText.TrimEnd() + "\n" + text;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "Không đọc được clipboard (đang bị ứng dụng khác giữ); thử lại.";
        }
    }

    [RelayCommand]
    private void SelectAllEntries() { foreach (var row in Entries) row.IsChecked = true; }

    [RelayCommand]
    private void ClearEntries() { foreach (var row in Entries) row.IsChecked = false; }

    [RelayCommand]
    private Task ApplyFilterAsync() => Guard(RefreshCatalogAsync);

    private async Task RefreshCatalogAsync()
    {
        ValidateDates();
        if (_catalogUrl is null && _catalogSearchPlatform is null)
            throw new ArgumentException("Bấm Xem thông tin hoặc Tìm để lấy danh sách trước khi áp dụng bộ lọc.");
        Status = "Đang lấy thông tin để lọc…";
        IReadOnlyList<CatalogEntry> items = _catalogUrl is { } url
            ? await catalog.ListAsync(url, VideoCatalogService.MaxEntries, CancellationToken.None, detailed: HasCatalogFilter)
            : await catalog.SearchAsync(_catalogSearchPlatform!, _catalogSearchKeyword!, 20, CancellationToken.None, detailed: HasCatalogFilter);
        Show(items, _catalogTitle);
        Status = null;
    }

    [RelayCommand]
    private Task DownloadEntriesAsync() => Guard(async () =>
    {
        ValidateDates();
        if (FilterChanged) throw new ArgumentException("Bộ lọc đã đổi. Bấm Áp dụng bộ lọc để xem lại danh sách trước khi tải.");
        var urls = Entries.Where(r => r.IsChecked).Select(r => r.Entry.Url).ToList();
        if (urls.Count == 0) throw new ArgumentException("Chọn ít nhất một mục để tải.");
        using var owner = JobOwner.Begin(JobOwner.Screen("S09"));
        var ids = await batch.EnqueueAsync(new BatchDownloadRequest(urls, SelectedFormat, NoWatermark, FolderText));
        Status = $"Đã đưa {ids.Count} mục vào hàng đợi tải (theo dõi ở khung Tác vụ của màn này).";
        dialogs.Info("Đã đưa vào hàng đợi", Status);
    });

    private void Show(IReadOnlyList<CatalogEntry> items, string title)
    {
        _catalogEntries = items;
        _catalogTitle = title;
        _appliedFrom = DateFrom;
        _appliedTo = DateTo;
        _appliedSort = SortBy;
        ApplyCatalogFilter();
    }

    private void ValidateDates()
    {
        if (DateFrom is { } from && DateTo is { } to && from.Date > to.Date)
            throw new ArgumentException("Ngày bắt đầu phải trước hoặc bằng ngày kết thúc.");
    }

    private void ApplyCatalogFilter()
    {
        if (_catalogTitle.Length == 0 || DateFrom is { } from && DateTo is { } to && from.Date > to.Date) return;
        var checkedUrls = Entries.Where(r => r.IsChecked).Select(r => r.Entry.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = VideoCatalogService.Filter(_catalogEntries, DateFrom, DateTo, SortBy);
        _thumbs?.Cancel();
        Entries.Clear();
        foreach (var item in items) Entries.Add(new CatalogRow(item) { IsChecked = checkedUrls.Contains(item.Url) });
        EntriesTitle = $"{_catalogTitle} · hiển thị {items.Count}/{_catalogEntries.Count}";
        _thumbs = new CancellationTokenSource();
        _ = LoadThumbsAsync([.. Entries], _thumbs.Token);
    }

    // Thumbnail lấy khi danh sách hiện, tối đa 4 yêu cầu song song, lỗi thì để trống.
    private async Task LoadThumbsAsync(IReadOnlyList<CatalogRow> rows, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        var client = http.Get("download");
        await Task.WhenAll(rows.Where(r => r.Entry.ThumbnailUrl is not null).Take(60).Select(async row =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var bytes = await client.GetByteArrayAsync(row.Entry.ThumbnailUrl!, ct);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 160;
                    image.StreamSource = new MemoryStream(bytes);
                    image.EndInit();
                    image.Freeze();
                    row.Thumb = image;
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or NotSupportedException or IOException or InvalidOperationException)
            {
                // thiếu thumbnail không ảnh hưởng việc tải
            }
        }));
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = dialogs.PickFolder("Chọn thư mục tải");
        if (folder is null) return;
        var s = settings.Get<DownloadSettings>("download");
        s.DownloadDir = folder;
        await settings.SetAsync("download", s);
        FolderText = folder;
    }

    [RelayCommand]
    private Task OpenFolderAsync() => Guard(() =>
    {
        Directory.CreateDirectory(FolderText);
        shell.OpenFolder(FolderText);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenSelectedAsync() => Guard(() =>
    {
        if (Selected is { } row) shell.OpenFile(row.Path);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private void SelectAllHistory()
    {
        foreach (var row in History) row.IsChecked = true;
    }

    [RelayCommand]
    private void ClearHistorySelection()
    {
        foreach (var row in History) row.IsChecked = false;
    }

    private HistoryRow[] SelectedHistory()
    {
        var rows = History.Where(r => r.IsChecked).ToArray();
        return rows.Length > 0 ? rows : Selected is { } row ? [row] : [];
    }

    [RelayCommand]
    private Task OpenHistoryAsync(HistoryRow? row) => Guard(() =>
    {
        if (row is not null) shell.OpenFile(row.Path);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task CopyHistoryPathAsync(HistoryRow? row) => CopyPathsAsync(row is null ? [] : [row]);

    [RelayCommand]
    private Task CopySelectedPathsAsync() => CopyPathsAsync(SelectedHistory());

    private Task CopyPathsAsync(HistoryRow[] rows) => Guard(() =>
    {
        if (rows.Length > 0)
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => r.Path)));
            dialogs.Info("Sao chép đường dẫn", $"Đã sao chép đường dẫn của {rows.Length} file.");
        }
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task DeleteHistoryAsync(HistoryRow? row) => DeleteRowsAsync(row is null ? [] : [row]);

    [RelayCommand]
    private Task DeleteSelectedAsync() => DeleteRowsAsync(SelectedHistory());

    private Task DeleteRowsAsync(HistoryRow[] rows) => Guard(async () =>
    {
        if (rows.Length == 0) return;
        var names = string.Join(Environment.NewLine, rows.Take(10).Select(r => r.Name));
        if (rows.Length > 10) names += $"\n… và {rows.Length - 10} file khác";
        if (!await dialogs.ConfirmAsync("Xoá file", $"Xoá {rows.Length} file khỏi ổ đĩa? Không thể hoàn tác.\n\n{names}")) return;
        var errors = new List<string>();
        var deleted = 0;
        foreach (var row in rows)
        {
            try
            {
                await library.DeleteAsync(row.Path);
                History.Remove(row);
                if (Selected == row) Selected = null;
                deleted++;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{row.Name}: {ex.Message}");
            }
        }
        Status = $"Đã xoá {deleted}/{rows.Length} file khỏi kho.";
        if (deleted > 0) dialogs.Info("Xoá file", Status);
        if (errors.Count > 0) dialogs.Error(string.Join(Environment.NewLine, errors));
    });

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DownloadException or IOException or InvalidDataException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            Status = null;
            dialogs.Error(ex.Message);
        }
    }
}
