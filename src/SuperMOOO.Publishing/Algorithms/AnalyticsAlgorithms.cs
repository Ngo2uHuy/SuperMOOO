namespace SuperMOOO.Publishing.Algorithms;

/// <summary>Bài có tuổi &lt; <paramref name="MaxAge"/> thì đồng bộ mỗi <paramref name="Interval"/>.</summary>
public sealed record CadenceTier(TimeSpan MaxAge, TimeSpan Interval);

public sealed record SyncCadenceOptions(IReadOnlyList<CadenceTier> Tiers, TimeSpan FallbackInterval)
{
    public static SyncCadenceOptions Default { get; } = new(
        [
            new(TimeSpan.FromHours(48), TimeSpan.FromHours(2)),
            new(TimeSpan.FromDays(7), TimeSpan.FromHours(12)),
            new(TimeSpan.FromDays(30), TimeSpan.FromDays(2)),
        ],
        TimeSpan.FromDays(7));
}

public sealed record SyncItem(string PublicationId, DateTimeOffset PublishedUtc, DateTimeOffset? LastSyncedUtc);

public static class SyncCadence
{
    public static TimeSpan IntervalFor(TimeSpan age, SyncCadenceOptions options)
    {
        foreach (var tier in options.Tiers.OrderBy(t => t.MaxAge))
            if (age < tier.MaxAge) return tier.Interval;
        return options.FallbackInterval;
    }

    /// <summary>Thời điểm đến hạn đồng bộ kế tiếp; bài chưa từng đồng bộ đến hạn ngay từ lúc đăng.</summary>
    public static DateTimeOffset DueAt(SyncItem item, DateTimeOffset nowUtc, SyncCadenceOptions options)
    {
        if (item.LastSyncedUtc is not { } last) return item.PublishedUtc;
        return last + IntervalFor(nowUtc - item.PublishedUtc, options);
    }

    /// <summary>
    /// Chọn tối đa <paramref name="budget"/> bài đã đến hạn. Ưu tiên theo mức quá hạn so với chu kỳ của chính bài đó
    /// (quá hạn lâu nhất lên trước; bài mới có chu kỳ ngắn nên tự lên trước), hòa thì bài mới hơn trước.
    /// </summary>
    public static IReadOnlyList<SyncItem> SelectDue(IEnumerable<SyncItem> items, DateTimeOffset nowUtc, int budget, SyncCadenceOptions options)
    {
        if (budget <= 0) return [];
        var queue = new PriorityQueue<SyncItem, (double NegRatio, long NegPublished, string Id)>(PriorityComparer.Instance);

        foreach (var item in items)
        {
            var due = DueAt(item, nowUtc, options);
            if (due > nowUtc) continue;
            var interval = IntervalFor(nowUtc - item.PublishedUtc, options);
            var ratio = (nowUtc - due) / interval;
            queue.Enqueue(item, (-ratio, -item.PublishedUtc.UtcTicks, item.PublicationId));
        }

        var result = new List<SyncItem>();
        while (result.Count < budget && queue.TryDequeue(out var next, out _)) result.Add(next);
        return result;
    }

    private sealed class PriorityComparer : IComparer<(double NegRatio, long NegPublished, string Id)>
    {
        public static readonly PriorityComparer Instance = new();

        public int Compare((double NegRatio, long NegPublished, string Id) x, (double NegRatio, long NegPublished, string Id) y)
        {
            var c = x.NegRatio.CompareTo(y.NegRatio);
            if (c != 0) return c;
            c = x.NegPublished.CompareTo(y.NegPublished);
            return c != 0 ? c : string.CompareOrdinal(x.Id, y.Id);
        }
    }
}

/// <param name="Metric">Chỉ số thô (view/like...); null thì mẫu bị bỏ, không coi là 0.</param>
/// <param name="Followers">Số follower lúc đăng; null hoặc &lt;= 0 thì mẫu bị bỏ vì không chuẩn hóa được.</param>
public sealed record PerformanceSample(DateTimeOffset PublishedUtc, double? Metric, long? Followers);

public enum BestTimeMethod { Median, Ewma }

public sealed record BestTimeOptions(int MinSamplesPerBucket = 5, BestTimeMethod Method = BestTimeMethod.Median, double EwmaAlpha = 0.3);

public sealed record TimeBucketScore(DayOfWeek Day, int Hour, double Score, int Samples);

public static class BestTime
{
    /// <summary>Các bucket (thứ, giờ local) đủ mẫu, điểm cao nhất trước. Điểm = chỉ số trên 1000 follower.</summary>
    public static IReadOnlyList<TimeBucketScore> Rank(IEnumerable<PerformanceSample> samples, TimeZoneInfo tz, BestTimeOptions options)
    {
        var buckets = samples
            .Where(s => s.Metric is { } m && m >= 0 && s.Followers is > 0)
            .Select(s =>
            {
                var local = TimeZoneInfo.ConvertTime(s.PublishedUtc, tz);
                return (s.PublishedUtc, local.DayOfWeek, local.Hour, Value: s.Metric!.Value / s.Followers!.Value * 1000.0);
            })
            .GroupBy(x => (x.DayOfWeek, x.Hour));

        var result = new List<TimeBucketScore>();
        foreach (var g in buckets)
        {
            var values = g.OrderBy(x => x.PublishedUtc).Select(x => x.Value).ToList();
            if (values.Count < options.MinSamplesPerBucket) continue;
            var score = options.Method == BestTimeMethod.Median ? Median(values) : Ewma(values, options.EwmaAlpha);
            result.Add(new TimeBucketScore(g.Key.DayOfWeek, g.Key.Hour, score, values.Count));
        }
        return result.OrderByDescending(r => r.Score).ThenBy(r => r.Day).ThenBy(r => r.Hour).ToList();
    }

    /// <summary>Bucket tốt nhất, hoặc null khi không bucket nào đủ mẫu (không đề xuất thay vì bịa).</summary>
    public static TimeBucketScore? Best(IEnumerable<PerformanceSample> samples, TimeZoneInfo tz, BestTimeOptions options) =>
        Rank(samples, tz, options).FirstOrDefault();

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Ewma(List<double> chronological, double alpha)
    {
        var ewma = chronological[0];
        for (var i = 1; i < chronological.Count; i++) ewma = alpha * chronological[i] + (1 - alpha) * ewma;
        return ewma;
    }
}

public enum AnomalyDirection
{
    /// <summary>Giá trị tăng bất thường (vd tỉ lệ lỗi).</summary>
    Increase,

    /// <summary>Giá trị sụt bất thường (vd view).</summary>
    Decrease,
}

/// <param name="MinHistory">Dưới số điểm lịch sử này thì không kết luận gì.</param>
/// <param name="RelativeStdFloor">Sàn độ lệch chuẩn theo tỉ lệ |mean|, tránh z-score vô hạn khi lịch sử gần như phẳng.</param>
public sealed record AnomalyOptions(double Alpha = 0.2, int MinHistory = 10, double ZThreshold = 3.0, double RelativeStdFloor = 0.05, double AbsoluteStdFloor = 1e-9);

public sealed record AnomalyResult(bool IsAnomaly, bool HasEnoughHistory, double ZScore, double Mean, double StdDev, int HistoryCount);

public static class AnomalyDetector
{
    /// <summary>So <paramref name="current"/> với baseline EWMA mean/variance dựng từ <paramref name="history"/> (cũ → mới).</summary>
    public static AnomalyResult Evaluate(IReadOnlyList<double> history, double current, AnomalyDirection direction, AnomalyOptions options)
    {
        if (history.Count < options.MinHistory) return new(false, false, 0, 0, 0, history.Count);

        var mean = history[0];
        var variance = 0.0;
        for (var i = 1; i < history.Count; i++)
        {
            var diff = history[i] - mean;
            var incr = options.Alpha * diff;
            mean += incr;
            variance = (1 - options.Alpha) * (variance + diff * incr);
        }

        var std = Math.Max(Math.Sqrt(variance), Math.Max(Math.Abs(mean) * options.RelativeStdFloor, options.AbsoluteStdFloor));
        var z = (current - mean) / std;
        var anomaly = direction == AnomalyDirection.Increase ? z >= options.ZThreshold : z <= -options.ZThreshold;
        return new(anomaly, true, z, mean, std, history.Count);
    }
}

