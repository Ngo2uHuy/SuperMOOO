using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Models;
using SuperMOOO.Core.Persistence;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Infrastructure.Tabular;
using SuperMOOO.Publishing.Analytics;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Scheduling;

namespace SuperMOOO.App.Features.S14;

public sealed record PeriodChoice(string Label, int Days);

public sealed record PlatformChoice(string? Key, string Label);

public sealed record FilterChoice(string? Id, string Label);

public sealed record MetricChoice(string Platform, string Key, string Label);

public sealed record StatRow(string Group, string Metric, string Value, string Previous, string Delta, string Note);

public sealed record TopPostRow(string Title, string Destination, string Platform, string Published, string Views, string Likes, string Comments, string Shares);

public sealed record BestTimeRow(string Channel, string Platform, string Summary);

public sealed record HealthRow(string Created, string Channel, string Detail);

/// <summary>Tab "Hiệu quả": tổng quan theo kỳ, theo kênh, top bài, biểu đồ, giờ đăng tốt, cảnh báo bất thường, xuất CSV. Đọc số đã đồng bộ nền, không gọi API nền tảng.</summary>
public sealed partial class AnalyticsViewModel(AnalyticsService analytics, IDialogService dialogs, ICampaignRepository campaigns, ISourceRepository sources) : ObservableObject
{
    private const int PageSize = 50;

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private bool _loading;

    public IReadOnlyList<PeriodChoice> Periods { get; } = [new("7 ngày", 7), new("30 ngày", 30), new("90 ngày", 90), new("365 ngày", 365)];

    public IReadOnlyList<PlatformChoice> Platforms { get; } =
    [
        new(null, "Tất cả nền tảng"), new(nameof(SocialPlatform.YouTube), "YouTube"), new(nameof(SocialPlatform.Facebook), "Facebook"),
        new(nameof(SocialPlatform.Instagram), "Instagram"), new(nameof(SocialPlatform.TikTok), "TikTok")
    ];

    public ObservableCollection<FilterChoice> CampaignChoices { get; } = [];
    public ObservableCollection<FilterChoice> SourceChoices { get; } = [];
    public ObservableCollection<StatRow> Overview { get; } = [];
    public ObservableCollection<StatRow> Channels { get; } = [];
    public ObservableCollection<TopPostRow> TopPosts { get; } = [];
    public ObservableCollection<BestTimeRow> BestTimes { get; } = [];
    public ObservableCollection<HealthRow> Alerts { get; } = [];
    public ObservableCollection<MetricChoice> ChartOptions { get; } = [];

    public IReadOnlyList<MetricSample> ChartSamples { get; private set; } = [];

    public event EventHandler? ChartChanged;

    [ObservableProperty]
    public partial PeriodChoice Period { get; set; }

    [ObservableProperty]
    public partial PlatformChoice Platform { get; set; }

    [ObservableProperty]
    public partial FilterChoice? CampaignFilter { get; set; }

    [ObservableProperty]
    public partial FilterChoice? SourceFilter { get; set; }

    [ObservableProperty]
    public partial MetricChoice? ChartMetric { get; set; }

    [ObservableProperty]
    public partial string Budget { get; set; } = "";

    [ObservableProperty]
    public partial string PageText { get; set; } = "";

    [ObservableProperty]
    public partial int Page { get; set; } = 1;

    [ObservableProperty]
    public partial int PageCount { get; set; } = 1;

    [ObservableProperty]
    public partial int AlertCount { get; set; }

    [ObservableProperty]
    public partial string ChartTitle { get; set; } = "";

    partial void OnPeriodChanged(PeriodChoice value) => Reload();
    partial void OnPlatformChanged(PlatformChoice value) => Reload();
    partial void OnCampaignFilterChanged(FilterChoice? value) => Reload();
    partial void OnSourceFilterChanged(FilterChoice? value) => Reload();

    partial void OnChartMetricChanged(MetricChoice? value)
    {
        if (!_loading && value is not null) _ = LoadAsync(keepChartChoice: true);
    }

    public AnalyticsViewModel Init()
    {
        Period = Periods[1];
        Platform = Platforms[0];
        return this;
    }

    private void Reload()
    {
        if (_loading || Period is null || Platform is null || CampaignFilter is null || SourceFilter is null) return;
        Page = 1;
        _ = LoadAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        try
        {
            var result = await analytics.RunSyncAsync(default);
            dialogs.Info("Thống kê", result.JobsQueued == 0
                ? "Chưa có bài nào đến hạn đồng bộ (hoặc đã hết ngân sách hôm nay)."
                : $"Đã xếp {result.JobsQueued} tác vụ đồng bộ ({result.PostsQueued} bài); chạy nền ưu tiên thấp.");
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (Page >= PageCount) return;
        Page++;
        await LoadTopPostsAsync();
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (Page <= 1) return;
        Page--;
        await LoadTopPostsAsync();
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var path = dialogs.SaveFile("Xuất thống kê", "CSV (*.csv)|*.csv", $"thong-ke-{DateTime.Today:yyyyMMdd}.csv");
        if (path is null) return;
        try
        {
            var (from, to) = Range();
            var report = await analytics.GetReportAsync(new AnalyticsReportRequest(from, to, AnalyticsGroupBy.Post, Platform.Key, CampaignId: CampaignFilter?.Id, SourceId: SourceFilter?.Id), default);
            var table = ToTable(report);
            await File.WriteAllTextAsync(path, CsvCodec.Write(table), new UTF8Encoding(true));   // BOM để Excel nhận UTF-8
            dialogs.Info("Thống kê", $"Đã xuất {table.Rows.Count} dòng: {path}");
        }
        catch (Exception ex) when (ex is PublishingException or IOException or UnauthorizedAccessException)
        {
            dialogs.Error(ex.Message);
        }
    }

    public async Task LoadAsync(bool keepChartChoice = false)
    {
        if (_loading || Period is null || Platform is null) return;
        _loading = true;
        try
        {
            await LoadFiltersAsync();
            var (from, to) = Range();
            var platform = Platform.Key;
            var campaign = CampaignFilter?.Id;
            var source = SourceFilter?.Id;
            var chart = keepChartChoice ? ChartMetric : null;
            var overview = await analytics.GetReportAsync(new AnalyticsReportRequest(from, to, AnalyticsGroupBy.Platform, platform, CampaignId: campaign, SourceId: source,
                ChartPlatform: chart?.Platform, ChartMetricKey: chart?.Key), default);
            var channels = await analytics.GetReportAsync(new AnalyticsReportRequest(from, to, AnalyticsGroupBy.Channel, platform, CampaignId: campaign, SourceId: source), default);
            var best = await analytics.GetBestTimesAsync(new GetBestTimesRequest(), default);
            var events = await analytics.ListHealthEventsAsync(50, default);
            var budgets = await analytics.BudgetUsageAsync(default);
            var posts = await analytics.TopPostsAsync(from, to, platform, null, Page, PageSize, default, campaign, source);

            UiPost.Run(_ui, () =>
            {
                Replace(Overview, overview.Rows.Select(ToStat));
                Replace(Channels, channels.Rows.Select(ToStat));
                Replace(BestTimes, best.Channels.Where(c => platform is null || c.Platform == platform).Select(ToBest));
                Replace(Alerts, events.Select(e => new HealthRow(PostRow.VietnamTimeText(e.CreatedUtc), e.DestinationTitle ?? "", e.Detail)));
                AlertCount = events.Count;
                Budget = "Ngân sách đọc số liệu hôm nay: " + string.Join(" · ", budgets.Select(b => $"{Labels.Term(b.Platform)} {b.Used}/{b.Budget}"));
                ApplyPosts(posts);
                ApplyChart(overview);
            });
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Danh sách lọc chiến dịch/nguồn; giữ lựa chọn hiện tại. Đặt lại lựa chọn khi đang _loading nên không kích hoạt nạp lại.</summary>
    private async Task LoadFiltersAsync()
    {
        var campaignList = (await campaigns.ListAsync(default)).Select(c => new FilterChoice(c.Id, c.Name)).ToList();
        var sourceList = (await sources.ListAsync(default)).Select(s => new FilterChoice(s.Id, s.Name)).ToList();
        var keepCampaign = CampaignFilter?.Id;
        var keepSource = SourceFilter?.Id;
        CampaignChoices.Sync([new FilterChoice(null, "Mọi chiến dịch"), .. campaignList], c => c.Id ?? "", c => c.Label);
        SourceChoices.Sync([new FilterChoice(null, "Mọi nguồn"), .. sourceList], c => c.Id ?? "", c => c.Label);
        CampaignFilter = CampaignChoices.FirstOrDefault(c => c.Id == keepCampaign) ?? CampaignChoices[0];
        SourceFilter = SourceChoices.FirstOrDefault(c => c.Id == keepSource) ?? SourceChoices[0];
    }

    private async Task LoadTopPostsAsync()
    {
        try
        {
            var (from, to) = Range();
            var posts = await analytics.TopPostsAsync(from, to, Platform.Key, null, Page, PageSize, default, CampaignFilter?.Id, SourceFilter?.Id);
            UiPost.Run(_ui, () => ApplyPosts(posts));
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    private void ApplyPosts(PagedDto<TopPostDto> posts)
    {
        PageCount = Math.Max(1, (int)Math.Ceiling(posts.Total / (double)PageSize));
        Page = Math.Min(Page, PageCount);
        PageText = $"Trang {Page}/{PageCount} · {posts.Total} bài";
        Replace(TopPosts, posts.Items.Select(p => new TopPostRow(p.Title, p.DestinationTitle, Labels.Term(p.Platform), PostRow.VietnamTimeText(p.PublishedUtc),
            Num(p.Views), Num(p.Likes), Num(p.Comments), Num(p.Shares))));
    }

    /// <summary>Biểu đồ vẽ theo ngày UTC từ metric đã chọn; ngày không có số bị bỏ (không vẽ thành 0).</summary>
    private void ApplyChart(AnalyticsReportDto report)
    {
        var options = report.ChartOptions.Select(o => new MetricChoice(o.Platform, o.Key, $"{Labels.Term(o.Platform)} · {o.DisplayName}")).ToList();
        _loading = true;
        ChartOptions.Sync(options, o => o.Platform + o.Key, o => o);
        ChartMetric = report.Chart is null ? null : options.FirstOrDefault(o => o.Platform == report.Chart.Platform && o.Key == report.Chart.MetricKey);
        _loading = false;
        ChartTitle = report.Chart is null ? "Chưa có số liệu để vẽ biểu đồ." : $"{Labels.Term(report.Chart.Platform)} · {report.Chart.DisplayName} (tổng theo ngày)";
        ChartSamples = report.Chart is null ? [] :
        [
            .. report.Chart.Points.Where(p => p.Value is not null).Select(p => new MetricSample(report.Chart.Platform, report.Chart.MetricKey, null, null,
                report.Chart.MetricKey, p.Value!.Value, DateTimeOffset.ParseExact(p.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)))
        ];
        ChartChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private (DateTimeOffset From, DateTimeOffset To) Range()
    {
        var to = DateTimeOffset.UtcNow;
        return (to.AddDays(-Period.Days), to);
    }

    private static StatRow ToStat(AnalyticsRowDto r)
    {
        var value = r.Status == MetricCellStatuses.Ok ? Num(r.Value) : "";
        var note = r.Status == MetricCellStatuses.Ok ? r.Detail ?? "" : StatusText(r.Status) + (r.Detail is null ? "" : ": " + r.Detail);
        var delta = r.Delta is { } d ? (d > 0 ? "+" : "") + Num(d) : "";
        return new StatRow(r.Label, r.MetricName, value, Num(r.PreviousValue), delta, note);
    }

    private static string StatusText(string status) => status switch
    {
        MetricCellStatuses.NoData => "Chưa có dữ liệu",
        MetricCellStatuses.NotAvailable => "Không có số liệu",
        MetricCellStatuses.MissingPermission => "Thiếu quyền",
        MetricCellStatuses.TokenExpired => "Cần kết nối lại",
        MetricCellStatuses.NotYetAvailable => "Chưa có số (bài còn mới)",
        MetricCellStatuses.NotAggregable => "Không cộng được",
        _ => status
    };

    private static string DayText(string day) => day switch
    {
        nameof(DayOfWeek.Monday) => "Thứ 2", nameof(DayOfWeek.Tuesday) => "Thứ 3", nameof(DayOfWeek.Wednesday) => "Thứ 4",
        nameof(DayOfWeek.Thursday) => "Thứ 5", nameof(DayOfWeek.Friday) => "Thứ 6", nameof(DayOfWeek.Saturday) => "Thứ 7", _ => "Chủ nhật"
    };

    private static BestTimeRow ToBest(BestTimeChannelDto c) => new(c.Title, Labels.Term(c.Platform),
        c.Status == BestTimeStatuses.Ok
            ? string.Join("; ", c.Buckets.Select(b => $"{DayText(b.Day)} {b.Hour:00}:00 ({b.Score:0.##}/1000 follower, {b.Samples} bài)")) + ". " + c.Message
            : c.Message);

    private static string Num(double? v) => v is { } d ? (Math.Abs(d) >= 1000 ? d.ToString("N0", CultureInfo.CurrentCulture) : d.ToString("0.##", CultureInfo.CurrentCulture)) : "";

    private static TableData ToTable(AnalyticsReportDto report)
    {
        string Time(DateTimeOffset t) => VietnamTime.ToLocal(t).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        string Raw(double? v) => v is { } d ? d.ToString("R", CultureInfo.InvariantCulture) : "";
        var rows = report.Rows.Select(r => (IReadOnlyList<string>)
        [
            r.Label, Labels.Term(r.Platform), r.MetricName, r.Unit, Raw(r.Value), Raw(r.PreviousValue), Raw(r.Delta), r.Status,
            r.CapturedUtc is { } at ? Time(at) : "", r.Source ?? "", r.Detail ?? ""
        ]).ToList();
        return new TableData(
            ["Bài · kênh", "Nền tảng", "Chỉ số", "Đơn vị", "Giá trị", "Kỳ trước", "Chênh lệch", "Trạng thái", "Lấy lúc (giờ VN)", "Nguồn", "Ghi chú"], rows);
    }
}
