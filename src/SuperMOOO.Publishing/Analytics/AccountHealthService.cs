using System.Globalization;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Automation;

namespace SuperMOOO.Publishing.Analytics;

/// <summary>
/// Sức khỏe tài khoản (PLAN 3.13): so tỉ lệ lỗi đăng hôm nay và view/bài gần đây với baseline EWMA của chính kênh bằng <see cref="AnomalyDetector"/>.
/// Bất thường → account_health_events (một sự kiện mỗi kênh/loại/ngày) + action_required. Bật <see cref="SettingKeys.HealthAutoPauseCampaigns"/>
/// thì còn tự dừng mọi chiến dịch đang chạy trên kênh đó (<see cref="CampaignGuard"/>); mặc định chỉ cảnh báo.
/// </summary>
[Service]
public sealed class AccountHealthService(
    IAnalyticsRepository analytics,
    IActionRequiredRepository actions,
    IAuditLog audit,
    IClock clock,
    AnalyticsOptions options,
    IPublishSettingsStore settings,
    CampaignGuard guard)
{
    /// <returns>Số sự kiện mới tạo.</returns>
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var created = 0;
        foreach (var d in (await analytics.ListDestinationsAsync(ct)).Where(d => !d.Removed))
        {
            if (await CheckFailureRateAsync(d, now, ct)) created++;
            if (await CheckViewDropAsync(d, now, ct)) created++;
        }
        return created;
    }

    private async Task<bool> CheckFailureRateAsync(DestinationInfoRow d, DateTimeOffset now, CancellationToken ct)
    {
        var outcomes = await analytics.ListPublishOutcomesAsync(d.DestinationId, now - TimeSpan.FromDays(options.HealthHistoryDays), ct);
        var today = Day(now);
        var current = outcomes.FirstOrDefault(o => o.Day == today);
        if (current is null || current.Failed + current.Published < options.MinOutcomesToday) return false;
        var rate = current.Failed / (double)(current.Failed + current.Published);
        if (rate < options.MinFailureRate) return false;

        var history = outcomes.Where(o => o.Day != today && o.Failed + o.Published > 0).OrderBy(o => o.Day, StringComparer.Ordinal)
            .Select(o => o.Failed / (double)(o.Failed + o.Published)).ToList();
        var result = AnomalyDetector.Evaluate(history, rate, AnomalyDirection.Increase, options.Anomaly);
        if (!result.IsAnomaly) return false;

        var detail = $"Tỉ lệ lỗi đăng hôm nay {Percent(rate)} ({current.Failed}/{current.Failed + current.Published} bài) cao bất thường so với mức nền "
                     + $"{Percent(result.Mean)} của kênh (z = {result.ZScore.ToString("0.0", CultureInfo.InvariantCulture)}).";
        return await RaiseAsync(d, HealthEventKinds.PublishFailureRate, detail, rate, result, now, ct);
    }

    private async Task<bool> CheckViewDropAsync(DestinationInfoRow d, DateTimeOffset now, CancellationToken ct)
    {
        var key = MetricKeys.PrimaryView(d.Platform);
        var samples = await analytics.ListPostViewSamplesAsync(d.DestinationId, key, now - options.MatureAge, ct);
        if (samples.Count < options.ViewWindow + options.Anomaly.MinHistory) return false;

        var recent = samples.TakeLast(options.ViewWindow).Select(s => s.Views).Average();
        var history = samples.Take(samples.Count - options.ViewWindow).Select(s => s.Views).ToList();
        var result = AnomalyDetector.Evaluate(history, recent, AnomalyDirection.Decrease, options.Anomaly);
        if (!result.IsAnomaly) return false;

        var detail = $"View trung bình của {options.ViewWindow} bài gần nhất ({recent.ToString("N0", CultureInfo.InvariantCulture)}) sụt mạnh so với mức nền "
                     + $"{result.Mean.ToString("N0", CultureInfo.InvariantCulture)} của kênh (z = {result.ZScore.ToString("0.0", CultureInfo.InvariantCulture)}). "
                     + "Chỉ so các bài đã đăng đủ lâu trong cùng nền tảng.";
        return await RaiseAsync(d, HealthEventKinds.ViewDrop, detail, recent, result, now, ct);
    }

    private async Task<bool> RaiseAsync(DestinationInfoRow d, string kind, string detail, double observed, AnomalyResult result, DateTimeOffset now,
        CancellationToken ct)
    {
        var ev = new AccountHealthEvent
        {
            Id = Guid.NewGuid().ToString("N"), ConnectionId = d.ConnectionId, DestinationId = d.DestinationId, Kind = kind, Detail = detail,
            Observed = observed, Baseline = result.Mean, ZScore = result.ZScore, CreatedUtc = now
        };
        if (!await analytics.TryAddHealthEventAsync(ev, Day(now), ct)) return false;

        await actions.RaiseAsync(ActionKinds.AccountHealth, d.DestinationId, $"Kênh \"{d.Title}\" ({d.Platform}) có dấu hiệu bất thường", detail, ct);
        await audit.AddAsync("analytics", "health.event", d.DestinationId, kind, 1, ct);
        if (AnalyticsSettingsStore.AutoPause(await settings.GetAsync(SettingKeys.HealthAutoPauseCampaigns, ct)))
        {
            var paused = await guard.PauseForDestinationAsync(d.DestinationId, $"Kênh \"{d.Title}\" có dấu hiệu bất thường: {detail}", ct);
            if (paused > 0) await analytics.SetHealthEventCampaignsPausedAsync(ev.Id, paused, ct);
        }
        return true;
    }

    private static string Day(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Percent(double rate) => (rate * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
