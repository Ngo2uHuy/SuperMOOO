using System.IO;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Infrastructure.Tabular;

namespace SuperMOOO.App.Controls;

public sealed class ResultRow(IReadOnlyList<string> cells)
{
    public IReadOnlyList<string> Cells => cells;

    // DataGrid bind theo chỉ số cột: [0], [1], …
    public string this[int index] => index < cells.Count ? cells[index] : "";
}

[Service(ServiceLifetime.Transient)]
public sealed partial class ResultGridViewModel(IJobQueue jobs, IDialogService dialogs) : ObservableObject
{
    private static readonly string[] UrlColumns = ["url", "link", "video_url", "source_url", "permalink"];
    private static readonly string[] TitleColumns = ["title", "name", "caption"];
    private static readonly string[] PreviewKinds = ["video", "short", "shorts", "reel", "note", "live", "clip"];
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private TableData _table = TableData.Empty;

    /// <summary>"Dùng dòng đã chọn": màn chứa lưới đăng ký để nạp các dòng vào form của mình.</summary>
    public event EventHandler<IReadOnlyList<JsonObject>>? RowsUsed;

    public IReadOnlyList<string> Columns => _table.Columns;
    public ObservableCollection<ResultRow> Visible { get; } = [];
    public IReadOnlyList<ResultRow> Selected { get; set; } = [];

    /// <summary>Owner gắn cho job tải xếp từ lưới (vd. <c>JobOwner.Screen("S10")</c>) để hiện ở khung tác vụ của màn chứa lưới.</summary>
    public string? JobsOwner { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    [ObservableProperty]
    public partial string? Status { get; set; }

    public event EventHandler? TableChanged;

    public void Load(TableData table)
    {
        _table = table;
        FilterText = "";
        ApplyFilter();
        OnPropertyChanged(nameof(Columns));
        TableChanged?.Invoke(this, EventArgs.Empty);
    }

    public void LoadJson(JsonNode? node) => Load(TableData.FromJson(node));

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public Task ExportAsync(string path, CancellationToken ct = default)
    {
        var table = new TableData(_table.Columns, Visible.Select(r => r.Cells).ToList());
        if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.Create(path);
            XlsxLite.Write(stream, table);
            return Task.CompletedTask;
        }

        return File.WriteAllTextAsync(path, CsvCodec.Write(table), new UTF8Encoding(true), ct);   // BOM để Excel nhận UTF-8
    }

    public Task SaveJsonAsync(string path, CancellationToken ct = default) =>
        File.WriteAllTextAsync(path, new JsonArray(AsObjects(Visible).Cast<JsonNode>().ToArray()).ToJsonString(Pretty), ct);

    public IReadOnlyList<JsonObject> SelectedAsObjects() => AsObjects(Selected);

    /// <summary>
    /// URL trang video của dòng nếu xem trước được. Chỉ lấy cột url/link… (không đoán cột http đầu tiên, vì đó có thể là link hồ sơ
    /// của bình luận); bảng có cột kind thì chỉ dòng video, còn kênh/bình luận/sản phẩm trả null.
    /// </summary>
    public string? PreviewUrl(ResultRow row)
    {
        if (FindColumn(UrlColumns) is not { } urlIndex) return null;
        var url = row[urlIndex];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        return FindColumn(["kind"]) is { } kind && !PreviewKinds.Contains(row[kind], StringComparer.OrdinalIgnoreCase) ? null : url;
    }

    public string TitleOf(ResultRow row) => FindColumn(TitleColumns) is { } t && row[t].Length > 0 ? row[t] : PreviewUrl(row) ?? "";

    /// <summary>"Đưa vào tải" trong cửa sổ xem trước: chỉ dòng đang xem; true khi đã xếp job.</summary>
    public Task<bool> SendRowToDownloadAsync(ResultRow row) => EnqueueWithStatusAsync([row]);

    /// <summary>Xếp mỗi dòng đã chọn có URL http(s) thành một job tải; trả số job đã xếp.</summary>
    public Task<int> EnqueueDownloadsAsync(CancellationToken ct = default) => EnqueueDownloadsAsync(Selected, ct);

    public async Task<int> EnqueueDownloadsAsync(IEnumerable<ResultRow> rows, CancellationToken ct = default)
    {
        var urlIndex = FindColumn(UrlColumns) ?? FirstUrlColumn();
        if (urlIndex is null) throw new InvalidOperationException("Bảng không có cột liên kết (url/link).");
        var titleIndex = FindColumn(TitleColumns);

        using var owner = JobsOwner is { } o ? JobOwner.Begin(o) : null;
        var count = 0;
        foreach (var row in rows)
        {
            var url = row[urlIndex.Value];
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var title = titleIndex is { } t && row[t].Length > 0 ? row[t] : url;
            await jobs.EnqueueAsync(new JobRequest(JobTypes.Download, title, new JsonObject { ["url"] = url }, JobLane.Network), ct);
            count++;
        }

        return count;
    }

    [RelayCommand]
    private async Task ExportTableAsync()
    {
        var path = dialogs.SaveFile("Xuất bảng", "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", "ket-qua.xlsx");
        if (path is null) return;
        await RunAsync(() => ExportAsync(path), $"Đã xuất {Visible.Count} dòng.");
    }

    [RelayCommand]
    private async Task SaveResultAsync()
    {
        var path = dialogs.SaveFile("Lưu kết quả", "JSON (*.json)|*.json", "ket-qua.json");
        if (path is null) return;
        await RunAsync(() => SaveJsonAsync(path), "Đã lưu kết quả.");
    }

    [RelayCommand]
    private void UseSelected() => RowsUsed?.Invoke(this, SelectedAsObjects());

    [RelayCommand]
    private async Task SendToDownloadAsync() => await EnqueueWithStatusAsync(Selected);

    private async Task<bool> EnqueueWithStatusAsync(IEnumerable<ResultRow> rows)
    {
        try
        {
            var count = await EnqueueDownloadsAsync(rows);
            Status = JobsOwner is null ? $"Đã đưa {count} mục vào hàng đợi tải." : $"Đã đưa {count} mục vào hàng đợi tải (theo dõi ở khung Tác vụ của màn này).";
            return count > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            dialogs.Error(ex.Message);
            return false;
        }
    }

    private async Task RunAsync(Func<Task> action, string done)
    {
        try
        {
            await action();
            Status = done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialogs.Error(ex.Message);
        }
    }

    private void ApplyFilter()
    {
        var text = FilterText.Trim();
        Visible.Clear();
        foreach (var cells in _table.Rows)
            if (text.Length == 0 || cells.Any(c => c.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
                Visible.Add(new ResultRow(cells));
    }

    private List<JsonObject> AsObjects(IEnumerable<ResultRow> rows) => rows
        .Select(r => new JsonObject(_table.Columns.Select((c, i) => KeyValuePair.Create(c, (JsonNode?)JsonValue.Create(r[i])))))
        .ToList();

    private int? FindColumn(string[] names)
    {
        var index = _table.Columns.ToList().FindIndex(c => names.Contains(c, StringComparer.OrdinalIgnoreCase));
        return index < 0 ? null : index;
    }

    private int? FirstUrlColumn()
    {
        var first = _table.Rows.FirstOrDefault();
        var index = first?.ToList().FindIndex(c => c.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? -1;
        return index < 0 ? null : index;
    }
}
