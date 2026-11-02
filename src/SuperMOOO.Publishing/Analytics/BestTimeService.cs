using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Scheduling;

namespace SuperMOOO.Publishing.Analytics;

/// <summary>
/// Gợi ý giờ đăng theo kênh (PLAN 3.14): trung vị lượt xem chính trên 1000 follower theo (thứ, giờ Việt Nam), chỉ tính bài đã đủ tuổi.
/// Khung giờ chưa đủ mẫu thì không đề xuất ("chưa đủ dữ liệu") thay vì bịa.
/// </summary>
[Service]
public sealed class BestTimeService(IAnalyticsRepository analytics, IClock clock, AnalyticsOptions options)
{
    private const int TopBuckets = 3;
    private static readonly TimeZoneInfo VietnamZone = TimeZoneInfo.CreateCustomTimeZone("VN", VietnamTime.Offset, "Giờ Việt Nam", "Giờ Việt Nam");
    /// <summary>Follower của một bài lấy từ snapshot kênh gần nhất trong khoảng này quanh lúc đăng; xa hơn thì bỏ bài (không chuẩn hóa được).</summary>
    private static readonly TimeSpan FollowerWindow = TimeSpan.FromDays(7);

    public async Task<BestTimesDto> GetAsync(GetBestTimesRequest r, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var all = (await analytics.ListDestinationsAsync(ct)).Where(d => !d.Removed).ToList();
        var tz = VietnamZone;
        IEnumerable<DestinationInfoRow> selected = all;
        if (r.DestinationId is not null) selected = all.Where(d => d.DestinationId == r.DestinationId);
        var channels = new List<BestTimeChannelDto>();
        foreach (var d in selected.OrderBy(d => d.Title, StringComparer.CurrentCultureIgnoreCase))
            channels.Add(await ForChannelAsync(d, tz, now, ct));
        return new BestTimesDto(channels);
    }

    private async Task<BestTimeChannelDto> ForChannelAsync(DestinationInfoRow d, TimeZoneInfo tz, DateTimeOffset now, CancellationToken ct)
    {
        var views = await analytics.ListPostViewSamplesAsync(d.DestinationId, MetricKeys.PrimaryView(d.Platform), now - options.MatureAge, ct);
        var followers = views.Count == 0
            ? []
            : (await analytics.ListPointsAsync([d.DestinationId], null, views.Min(v => v.PublishedUtc) - FollowerWindow, now, ct))
                .Where(p => p.MetricKey == MetricKeys.Followers && p.Status == MetricStatus.Ok).ToList();

        var samples = views.Select(v =>
        {
            var near = followers.OrderBy(f => (f.CapturedUtc - v.PublishedUtc).Duration()).FirstOrDefault();
            long? count = near is not null && (near.CapturedUtc - v.PublishedUtc).Duration() <= FollowerWindow ? (long)near.Value!.Value : null;
            return new PerformanceSample(v.PublishedUtc, v.Views, count);
        }).ToList();

        var usable = samples.Count(s => s.Followers is > 0);
        var min = options.BestTime.MinSamplesPerBucket;
        var ranked = BestTime.Rank(samples, tz, options.BestTime).Take(TopBuckets)
            .Select(b => new BestTimeBucketDto(b.Day.ToString(), b.Hour, Math.Round(b.Score, 2), b.Samples)).ToList();
        return ranked.Count == 0
            ? new BestTimeChannelDto(d.DestinationId, d.Title, d.Platform.ToString(), BestTimeStatuses.InsufficientData, usable, min, tz.Id, [],
                $"Chưa đủ dữ liệu: cần ít nhất {min} bài cùng khung giờ (thứ + giờ) đã có số liệu và số follower; hiện có {usable} bài dùng được.")
            : new BestTimeChannelDto(d.DestinationId, d.Title, d.Platform.ToString(), BestTimeStatuses.Ok, usable, min, tz.Id, ranked,
                $"Dựa trên {usable} bài (giờ Việt Nam); điểm = lượt xem chính trên 1000 follower, chỉ so trong cùng nền tảng.");
    }
}
