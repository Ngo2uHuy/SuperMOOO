using System.Globalization;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.Publishing.Analytics;

[Service]
public sealed class AnalyticsOptions
{
    /// <summary>Bài mới đồng bộ dày, bài cũ thưa (mặc định 2h → 12h → 2 ngày → 1 tuần).</summary>
    public SyncCadenceOptions Cadence { get; set; } = SyncCadenceOptions.Default;
    /// <summary>Số bài tối đa trong một job (YouTube videos.list nhận 50 id/lần).</summary>
    public int MaxPostsPerJob { get; set; } = 50;
    public int JobMaxAttempts { get; set; } = 3;
    /// <summary>Số job đồng bộ tối đa xếp mỗi lượt planner: lane Network dùng chung với tải/crawl nên không xếp ồ ạt.</summary>
    public int MaxJobsPerRun { get; set; } = 20;
    /// <summary>Số lớn = chạy sau; thống kê nằm sau mọi việc khác trong lane.</summary>
    public int JobPriority { get; set; } = 9;
    public TimeSpan ChannelInterval { get; set; } = TimeSpan.FromDays(1);
    public int DefaultDailyBudget { get; set; } = 200;
    /// <summary>Units YouTube Data API giữ lại cho upload: thống kê chỉ được dùng phần quota vượt mức này.</summary>
    public int YouTubeQuotaReserve { get; set; } = 3200;
    public int DefaultRetentionDays { get; set; } = 30;
    public TimeSpan CompactEvery { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Bài chưa đủ tuổi thì chưa dùng cho baseline/giờ đăng tốt: view còn đang tăng nhanh, so sánh sẽ lệch.</summary>
    public TimeSpan MatureAge { get; set; } = TimeSpan.FromDays(3);
    public int HealthHistoryDays { get; set; } = 30;
    /// <summary>Số bài gần nhất gộp thành "hiện tại" khi so view với baseline.</summary>
    public int ViewWindow { get; set; } = 3;
    /// <summary>Tỉ lệ lỗi đăng chỉ bị coi là bất thường khi có ít nhất chừng này bài kết thúc trong ngày và tỉ lệ từ ngưỡng này trở lên.</summary>
    public int MinOutcomesToday { get; set; } = 3;
    public double MinFailureRate { get; set; } = 0.3;
    public AnomalyOptions Anomaly { get; set; } = new();
    public BestTimeOptions BestTime { get; set; } = new();
}

public static class AnalyticsSettingsStore
{
    public static async Task<int> RetentionDaysAsync(IPublishSettingsStore settings, AnalyticsOptions options, CancellationToken ct) =>
        int.TryParse(await settings.GetAsync(SettingKeys.AnalyticsRetentionDays, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d > 0
            ? d : options.DefaultRetentionDays;

    public static async Task<int> BudgetAsync(IPublishSettingsStore settings, AnalyticsOptions options, SocialPlatform platform, CancellationToken ct) =>
        int.TryParse(await settings.GetAsync(BudgetKey(platform), ct), NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b >= 0
            ? b : options.DefaultDailyBudget;

    public static string BudgetKey(SocialPlatform platform) => SettingKeys.AnalyticsBudgetPrefix + platform.ToString().ToLowerInvariant();

    public static bool AutoPause(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Khoá sổ quota nội bộ của ngân sách đồng bộ (cùng bảng quota_usage, tách khỏi project OAuth của YouTube).</summary>
    public static string LedgerKey(SocialPlatform platform) => "analytics:" + platform.ToString().ToLowerInvariant();

    public static DateTimeOffset DayStart(DateTimeOffset now) => new(now.UtcDateTime.Date, TimeSpan.Zero);

    public static DateTimeOffset DayEnd(DateTimeOffset now) => DayStart(now).AddDays(1);
}

/// <summary>Payload job <see cref="PublishJobTypes.AnalyticsSync"/>; khoá đồng thời: mỗi tài khoản một job thống kê một lúc.</summary>
public sealed record AnalyticsJobPayload(
    string ConnectionId, string Platform, string DestinationId, List<string> PublicationIds, bool IncludeChannel, string ConcurrencyKey);
