namespace SuperMOOO.Publishing.Analytics;

// --- Thống kê (GĐ5) ---

public static class AnalyticsGroupBy
{
    public const string Post = "Post";
    public const string Channel = "Channel";
    public const string Platform = "Platform";
    public const string Campaign = "Campaign";
    public const string Source = "Source";
}

/// <summary>Trạng thái của một ô thống kê. Ngoài các trạng thái của snapshot còn có NoData (chưa từng đồng bộ) và NotAggregable.</summary>
public static class MetricCellStatuses
{
    public const string Ok = "Ok";
    public const string NoData = "NoData";
    public const string NotAvailable = "NotAvailable";
    public const string MissingPermission = "MissingPermission";
    public const string TokenExpired = "TokenExpired";
    public const string NotYetAvailable = "NotYetAvailable";
    /// <summary>Metric không cộng được giữa nhiều bài (vd người xem duy nhất) nên dòng tổng hợp không có số.</summary>
    public const string NotAggregable = "NotAggregable";
}

/// <param name="GroupBy">AnalyticsGroupBy.*.</param>
/// <param name="Platform">Lọc theo nền tảng (YouTube...); null = tất cả (mỗi nền tảng một dòng riêng, không cộng chéo).</param>
/// <param name="ChartPlatform">Nền tảng của metric vẽ biểu đồ; null = metric đầu tiên có dữ liệu.</param>
public sealed record AnalyticsReportRequest(
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, string GroupBy, string? Platform = null, string? DestinationId = null,
    string? CampaignId = null, string? SourceId = null, string? PublicationId = null, string? ChartPlatform = null,
    string? ChartMetricKey = null);

/// <summary>
/// Một dòng = một nhóm × một metric của nền tảng đó. Value là số mới nhất đến hết kỳ (lấy lúc CapturedUtc từ Source);
/// PreviousValue là số mới nhất trong kỳ liền trước cùng độ dài; Delta = Value − PreviousValue khi có cả hai.
/// </summary>
/// <param name="Covered">Số bài/kênh có số liệu trên tổng số <paramref name="Total"/> trong nhóm.</param>
/// <param name="Detail">Lý do khi thiếu dữ liệu, hoặc ghi chú khi lần đồng bộ gần nhất thất bại.</param>
public sealed record AnalyticsRowDto(
    string GroupKind, string GroupKey, string Label, string Platform, string MetricKey, string MetricName, string Unit, double? Value,
    double? PreviousValue, double? Delta, string Status, DateTimeOffset? CapturedUtc, string? Source, string? Detail, int Covered, int Total);

public sealed record ChartPointDto(string Day, double? Value);

public sealed record AnalyticsChartDto(string Platform, string MetricKey, string DisplayName, string Unit, List<ChartPointDto> Points);

public sealed record MetricDefinitionDto(
    string Platform, string Key, string Scope, string DisplayName, string Unit, string Description, string SourceEndpoint,
    string? RequiredScope, bool Verified);

public sealed record AnalyticsReportDto(
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset PreviousFromUtc, DateTimeOffset GeneratedUtc, List<AnalyticsRowDto> Rows,
    AnalyticsChartDto? Chart, List<MetricDefinitionDto> ChartOptions);

public sealed record AnalyticsCsvDto(string FileName, string Content);

public sealed record GetBestTimesRequest(string? CampaignId = null, string? DestinationId = null);

public sealed record BestTimeBucketDto(string Day, int Hour, double Score, int Samples);

public static class BestTimeStatuses
{
    public const string Ok = "Ok";
    public const string InsufficientData = "InsufficientData";
}

/// <param name="Score">Mức hiệu quả: chỉ số lượt xem chính của nền tảng trên 1000 follower (trung vị theo khung giờ).</param>
public sealed record BestTimeChannelDto(
    string DestinationId, string Title, string Platform, string Status, int TotalSamples, int MinSamplesPerBucket, string TimeZoneId,
    List<BestTimeBucketDto> Buckets, string Message);

public sealed record BestTimesDto(List<BestTimeChannelDto> Channels);

public sealed record AnalyticsSettingsDto(int SnapshotRetentionDays, bool HealthAutoPauseCampaigns, Dictionary<string, int> DailyBudgets);

public sealed record ListHealthEventsRequest(int Take = 50);

public sealed record HealthEventDto(
    string Id, string ConnectionId, string? DestinationId, string? DestinationTitle, string Kind, string Detail, double? Observed, double? Baseline,
    double? ZScore, int CampaignsPaused, DateTimeOffset CreatedUtc);

public sealed record AnalyticsSyncResultDto(int JobsQueued, int PostsQueued);
