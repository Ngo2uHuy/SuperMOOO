using SuperMOOO.Core.Publishing;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Library;

namespace SuperMOOO.App.Features.S21;

public sealed record SourceOption(string? Id, string Name);

public sealed record StatusOption(ContentStatus? Status, string Name);

public sealed record ModeOption(ImportMode Mode, string Name);

public sealed record KindOption(SourceKind Kind, string Name);

/// <summary>Một dòng của kho. Thumbnail chỉ được lấy khi dòng thật sự hiện (DataGrid ảo hoá chỉ tạo binding cho dòng nhìn thấy).</summary>
public sealed class ContentRow(FinishedContent item, string? attachedImage, string sourceName, ContentThumbnails thumbnails) : ObservableObject
{
    private bool _requested;
    private ImageSource? _thumbnail;

    public FinishedContent Item { get; } = item;
    public string Id => Item.Id;
    public string Title => Item.Title;
    public string Source => sourceName;
    public string Info => (Item.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss") : "")
        + (Item.Width is { } w && Item.Height is { } h ? $"  {w}x{h}" : "");
    public string Flags => string.Join(" · ", new[] { Item.IsAiGenerated ? "AI" : null, Item.IsReupload ? "Reup" : null, Item.Status == ContentStatus.Archived ? "Lưu trữ" : null }.OfType<string>());
    public string Imported => Item.ImportedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    public ImageSource? Thumbnail
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    private async Task LoadThumbnailAsync()
    {
        _thumbnail = await thumbnails.LoadAsync(Item, attachedImage);
        OnPropertyChanged(nameof(Thumbnail));
    }
}

public sealed record WatchRow(ContentSource Source)
{
    public string Id => Source.Id;
    public string Name => Source.Name;
    public string Path => Source.RootPath;
    public string Mode => Source.ImportMode switch
    {
        ImportMode.Manifest => "manifest.json", ImportMode.VideoCMeta => "meta.json VideoC", _ => "File rời"
    } + (Source.Recursive ? ", cả thư mục con" : "");
    public string State => !Source.Enabled ? "Tắt" : Source.WatchEnabled ? "Đang theo dõi" : "Chỉ quét tay";
}

[Service(ServiceLifetime.Transient)]
public sealed partial class ContentLibraryViewModel(IContentRepository contents, ISourceRepository sources, LibraryImportService importer,
    ContentIngestService ingest, LibraryRiskService risk, ContentThumbnails thumbnails, ContentHandoff handoff, IDialogService dialogs, ShellService shell,
    IServiceProvider services, IClock clock) : ObservableObject
{
    private const int PageSize = 50;

    private Dictionary<string, string> _sourceNames = [];
    private bool _loading;

    public ObservableCollection<ContentRow> Rows { get; } = [];
    public ObservableCollection<SourceOption> SourceOptions { get; } = [];
    public ObservableCollection<WatchRow> Watches { get; } = [];
    public ObservableCollection<string> Posts { get; } = [];
    public ObservableCollection<string> RiskLines { get; } = [];

    public IReadOnlyList<StatusOption> StatusOptions { get; } =
        [new(null, "Mọi trạng thái"), new(ContentStatus.Ready, "Sẵn sàng"), new(ContentStatus.Archived, "Lưu trữ")];

    public IReadOnlyList<ModeOption> ModeOptions { get; } =
    [
        new(ImportMode.LooseFiles, "File video rời (dùng manifest.json cạnh file nếu có)"),
        new(ImportMode.Manifest, "Thư mục có manifest.json v1"),
        new(ImportMode.VideoCMeta, "Thư mục VideoC có meta.json")
    ];

    public IReadOnlyList<KindOption> KindOptions { get; } =
        [new(SourceKind.Folder, "Thư mục thường"), new(SourceKind.VideoC, "VideoC (nội dung AI)"), new(SourceKind.AIVideoStudio, "Xuất từ SuperMOOO (nội dung AI)")];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial SourceOption? SourceFilter { get; set; }

    [ObservableProperty]
    public partial StatusOption? StatusFilter { get; set; }

    [ObservableProperty]
    public partial int Page { get; set; } = 1;

    [ObservableProperty]
    public partial int TotalPages { get; set; } = 1;

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial ContentRow? Selected { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // --- Sửa metadata ---
    [ObservableProperty]
    public partial string EditTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EditCaption { get; set; } = "";

    [ObservableProperty]
    public partial string EditTags { get; set; } = "";

    [ObservableProperty]
    public partial string EditLanguage { get; set; } = "";

    [ObservableProperty]
    public partial bool EditAi { get; set; }

    [ObservableProperty]
    public partial bool EditReup { get; set; }

    [ObservableProperty]
    public partial bool EditArchived { get; set; }

    // --- Thư mục theo dõi ---
    [ObservableProperty]
    public partial WatchRow? SelectedWatch { get; set; }

    [ObservableProperty]
    public partial string WatchPath { get; set; } = "";

    [ObservableProperty]
    public partial string WatchName { get; set; } = "";

    [ObservableProperty]
    public partial ModeOption? WatchMode { get; set; }

    [ObservableProperty]
    public partial KindOption? WatchKind { get; set; }

    [ObservableProperty]
    public partial bool WatchRecursive { get; set; } = true;

    partial void OnSearchChanged(string value) => ReloadFromFirstPage();

    partial void OnSourceFilterChanged(SourceOption? value) => ReloadFromFirstPage();

    partial void OnStatusFilterChanged(StatusOption? value) => ReloadFromFirstPage();

    partial void OnSelectedChanged(ContentRow? value) => _ = ShowDetailAsync(value);

    private void ReloadFromFirstPage()
    {
        if (_loading) return;
        Page = 1;
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var all = await sources.ListAsync(default);
            _sourceNames = all.ToDictionary(s => s.Id, s => s.Name);
            var keepSource = SourceFilter?.Id;
            SourceOptions.Sync([new SourceOption(null, "Mọi nguồn"), .. all.Select(s => new SourceOption(s.Id, s.Name))], o => o.Id ?? "", o => o.Name);
            SourceFilter = SourceOptions.FirstOrDefault(o => o.Id == keepSource) ?? SourceOptions[0];
            StatusFilter ??= StatusOptions[0];
            WatchMode ??= ModeOptions[0];
            WatchKind ??= KindOptions[0];
            Watches.Sync([.. all.Where(s => !LibrarySources.IsSystem(s.Id)).Select(s => new WatchRow(s))], w => w.Id, w => (w.State, w.Mode, w.Path));

            var (items, total) = await contents.QueryAsync(string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(), SourceFilter?.Id, Page, PageSize, default, StatusFilter?.Status);
            TotalPages = Math.Max(1, (total + PageSize - 1) / PageSize);
            if (Page > TotalPages)
            {
                Page = TotalPages;
                (items, total) = await contents.QueryAsync(string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(), SourceFilter?.Id, Page, PageSize, default, StatusFilter?.Status);
            }
            var keep = Selected?.Id;
            Rows.Sync([.. items.Select(i => new ContentRow(i.Item, i.ThumbnailPath, SourceName(i.Item.SourceId), thumbnails))], r => r.Id,
                r => (r.Title, r.Flags, r.Info));
            Selected = Rows.FirstOrDefault(r => r.Id == keep);
            Summary = $"{total} video · trang {Page}/{TotalPages}";
        }
        finally { _loading = false; }
    }

    private string SourceName(string? id) => id is not null && _sourceNames.TryGetValue(id, out var name) ? name : "Không rõ";

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (Page <= 1) return;
        Page--;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (Page >= TotalPages) return;
        Page++;
        await LoadAsync();
    }

    // --- Nhập ---
    [RelayCommand]
    private async Task AddFileAsync()
    {
        if (dialogs.OpenFile("Chọn video", "Video|*.mp4;*.mov;*.mkv;*.webm;*.m4v") is not { } path) return;
        await RunImportAsync(ct => importer.ImportFilesAsync([path], ct));
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (dialogs.PickFolder("Chọn thư mục chứa video") is not { } folder) return;
        var recursive = await dialogs.ConfirmAsync("Thêm thư mục", "Lấy cả video trong thư mục con?", "Cả thư mục con", "Chỉ thư mục này");
        await RunImportAsync(ct => importer.ImportFolderAsync(folder, recursive, ct));
    }

    [RelayCommand]
    private async Task ImportTableAsync()
    {
        if (dialogs.OpenFile("Chọn file CSV/Excel", "CSV/Excel|*.csv;*.xlsx") is not { } path) return;
        await RunImportAsync(async ct =>
        {
            var errors = new List<string>();
            var requests = ContentCsv.Parse(ContentCsv.Read(path), errors);
            int added = 0, duplicate = 0, failed = errors.Count;
            foreach (var request in requests)
            {
                try
                {
                    var result = await ingest.IngestAsync(request, ct);
                    if (!result.Duplicate) { added++; continue; }
                    // Bảng là nguồn do người dùng tự khai: video đã có thì cập nhật metadata theo bảng.
                    duplicate++;
                    await contents.UpdateMetadataAsync(result.ContentId, new ContentMetadataUpdate(request.Title, request.Caption, request.Language,
                        request.Tags is { Count: > 0 } ? JsonSerializer.Serialize(request.Tags) : null, request.IsAiGenerated, request.IsReupload, null), ct);
                }
                catch (Exception e) when (e is IngestException or IOException)
                {
                    failed++;
                    errors.Add($"{Path.GetFileName(request.VideoPath)}: {e.Message}");
                }
            }
            return new ImportSummary(added, duplicate, 0, failed, errors);
        });
    }

    private async Task RunImportAsync(Func<CancellationToken, Task<ImportSummary>> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Đang nhập vào kho…";
        try
        {
            var summary = await work(default);
            Status = summary.Describe();
            if (summary.Errors.Count > 0) dialogs.Error(string.Join("\n", summary.Errors.Take(8)));
            else dialogs.Info("Kho thành phẩm", summary.Describe());
            await LoadAsync();
        }
        catch (Exception e) when (e is IngestException or IOException or UnauthorizedAccessException)
        {
            Status = null;
            dialogs.Error(e.Message);
        }
        finally { IsBusy = false; }
    }

    // --- Mục đang chọn ---
    private async Task ShowDetailAsync(ContentRow? row)
    {
        Posts.Clear();
        RiskLines.Clear();
        if (row is null) return;
        var item = row.Item;
        EditTitle = item.Title;
        EditCaption = item.Caption ?? "";
        EditLanguage = item.Language ?? "";
        EditAi = item.IsAiGenerated;
        EditReup = item.IsReupload;
        EditArchived = item.Status == ContentStatus.Archived;
        EditTags = TagsText(item.TagsJson);
        var posts = await contents.ListPostsAsync(item.Id, default);
        if (Selected?.Id != item.Id) return;
        foreach (var p in posts)
            Posts.Add($"{Labels.Term(p.Platform)} · {p.DestinationTitle} · {Labels.Term(p.Status)}"
                + ((p.PublishedUtc ?? p.ScheduledUtc) is { } at ? " · " + at.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : ""));
        if (posts.Count == 0) Posts.Add("Chưa đăng ở đâu.");
    }

    private static string TagsText(string json)
    {
        try { return string.Join(", ", JsonSerializer.Deserialize<List<string>>(json) ?? []); }
        catch (JsonException) { return ""; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Selected is not { } row) return;
        if (string.IsNullOrWhiteSpace(EditTitle)) { dialogs.Error("Tiêu đề không được để trống."); return; }
        var tags = EditTags.Split([',', ';', '#', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await contents.UpdateMetadataAsync(row.Id, new ContentMetadataUpdate(EditTitle.Trim(), EditCaption, EditLanguage.Trim(), JsonSerializer.Serialize(tags),
            EditAi, EditReup, EditArchived ? ContentStatus.Archived : ContentStatus.Ready), default);
        dialogs.Info("Kho thành phẩm", "Đã lưu thông tin video.");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task CheckRiskAsync()
    {
        if (Selected is not { } row || IsBusy) return;
        IsBusy = true;
        Status = "Đang kiểm tra trùng video/caption…";
        try
        {
            var lines = await risk.CheckAsync(row.Id, default);
            RiskLines.Clear();
            foreach (var l in lines) RiskLines.Add((l.Level == RiskLevel.Warning ? "⚠ " : "") + l.Message);
            Status = null;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Compose()
    {
        if (Selected is not { } row) return;
        if (row.Item.Status == ContentStatus.Archived) { dialogs.Error("Video đã lưu trữ: bỏ lưu trữ trước khi soạn bài."); return; }
        handoff.Offer(row.Id);
        var screens = services.GetRequiredService<ScreenRegistry>();
        services.GetRequiredService<MainViewModel>().Select(screens.Find("S14")!);
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (Selected is { } row && File.Exists(row.Item.OriginalPath)) shell.OpenFile(row.Item.OriginalPath);
        else dialogs.Error("File video không còn ở đường dẫn cũ.");
    }

    [RelayCommand]
    private void RevealFile()
    {
        if (Selected is { } row && File.Exists(row.Item.OriginalPath)) shell.RevealFile(row.Item.OriginalPath);
        else dialogs.Error("File video không còn ở đường dẫn cũ.");
    }

    // --- Thư mục theo dõi ---
    [RelayCommand]
    private void BrowseWatch()
    {
        if (dialogs.PickFolder("Chọn thư mục theo dõi") is not { } folder) return;
        WatchPath = folder;
        if (string.IsNullOrWhiteSpace(WatchName)) WatchName = Path.GetFileName(folder.TrimEnd('\\', '/'));
    }

    [RelayCommand]
    private async Task AddWatchAsync()
    {
        if (string.IsNullOrWhiteSpace(WatchPath) || !Directory.Exists(WatchPath)) { dialogs.Error("Chọn một thư mục có thật."); return; }
        var root = PathRules.Normalize(WatchPath);
        if ((await sources.ListAsync(default)).Any(s => PathRules.Key(s.RootPath) == PathRules.Key(root))) { dialogs.Error("Thư mục này đã được theo dõi."); return; }
        var now = clock.UtcNow;
        await sources.UpsertAsync(new ContentSource
        {
            Id = Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(WatchName) ? Path.GetFileName(root) : WatchName.Trim(), Kind = (WatchKind ?? KindOptions[0]).Kind,
            RootPath = root, ImportMode = (WatchMode ?? ModeOptions[0]).Mode, Recursive = WatchRecursive, WatchEnabled = true, Enabled = true, CreatedUtc = now, UpdatedUtc = now
        }, default);
        WatchPath = "";
        WatchName = "";
        dialogs.Info("Kho thành phẩm", "Đã thêm thư mục theo dõi; app quét mỗi phút.");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleWatchAsync()
    {
        if (SelectedWatch is not { } w) return;
        var s = w.Source;
        s.WatchEnabled = !s.WatchEnabled;
        s.Enabled = true;
        s.UpdatedUtc = clock.UtcNow;
        await sources.UpsertAsync(s, default);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SweepWatchAsync()
    {
        if (SelectedWatch is not { } w) return;
        await RunImportAsync(ct => importer.SweepAsync(w.Source, ct));
    }

    [RelayCommand]
    private async Task RemoveWatchAsync()
    {
        if (SelectedWatch is not { } w) return;
        if (!await dialogs.ConfirmAsync("Xoá thư mục theo dõi", $"Ngừng theo dõi \"{w.Name}\"? Video đã nhập vẫn ở trong kho (và file không bị xoá).", "Xoá")) return;
        await sources.RemoveAsync(w.Id, default);
        await LoadAsync();
    }
}
