using System.Text;

namespace SuperMOOO.Publishing.Algorithms;

// --- Chia lượt giữa các chiến dịch cùng kênh ---

public sealed record FairShareCandidate(string CampaignId, double Weight, DateTimeOffset? EarliestExpiryUtc);

/// <summary>Deficit round-robin: mỗi slot tốn 1 đơn vị; chiến dịch có trọng số lớn tích deficit nhanh hơn theo đúng tỉ lệ.</summary>
public sealed class FairShare
{
    private const double Epsilon = 1e-9;
    private readonly Dictionary<string, double> _deficit = new(StringComparer.Ordinal);

    public FairShare() { }

    /// <summary>Khôi phục deficit đã lưu (vd từ DB) để restart không reset tỉ lệ chia lượt.</summary>
    public FairShare(IEnumerable<KeyValuePair<string, double>> deficits)
    {
        foreach (var (id, d) in deficits) _deficit[id] = d;
    }

    public double DeficitOf(string campaignId) => _deficit.GetValueOrDefault(campaignId);

    public IReadOnlyDictionary<string, double> Snapshot() => new Dictionary<string, double>(_deficit, StringComparer.Ordinal);

    /// <summary>Chọn chiến dịch cho slot kế tiếp; null nếu không có ứng viên hợp lệ (weight &gt; 0).</summary>
    public string? Pick(IReadOnlyList<FairShareCandidate> candidates)
    {
        var active = candidates.Where(c => c.Weight > 0).ToList();
        // Chiến dịch không còn nội dung không được tích deficit để "đòi bù" về sau.
        foreach (var key in _deficit.Keys.Where(k => active.All(c => c.CampaignId != k)).ToList()) _deficit.Remove(key);
        if (active.Count == 0) return null;

        foreach (var c in active) _deficit.TryAdd(c.CampaignId, 0);

        if (!active.Any(c => _deficit[c.CampaignId] >= 1 - Epsilon))
        {
            // Nạp đúng lượng nhỏ nhất để có người đủ 1, giữ nguyên tỉ lệ trọng số.
            var f = active.Min(c => (1 - _deficit[c.CampaignId]) / c.Weight);
            foreach (var c in active) _deficit[c.CampaignId] += f * c.Weight;
        }

        var eligible = active.Where(c => _deficit[c.CampaignId] >= 1 - Epsilon).ToList();
        var maxDeficit = eligible.Max(c => _deficit[c.CampaignId]);
        var winner = eligible
            .Where(c => maxDeficit - _deficit[c.CampaignId] <= Epsilon)
            .OrderBy(c => c.EarliestExpiryUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(c => c.CampaignId, StringComparer.Ordinal)
            .First();
        _deficit[winner.CampaignId] -= 1;
        return winner.CampaignId;
    }
}

// --- Trễ lịch ---

public enum LateAction { PublishNow, Reschedule, Skip }

/// <param name="Grace">Trễ không quá mức này vẫn coi là đúng giờ (jitter, tick của bộ điều phối).</param>
/// <param name="MaxLateness">CatchUp: cửa sổ trễ tối đa còn được đăng bù.</param>
/// <param name="MaxCatchUp">CatchUp: trần số bài bù mỗi lần xét.</param>
public sealed record LatePolicyOptions(LateMode Mode, TimeSpan Grace, TimeSpan MaxLateness, int MaxCatchUp);

public sealed record DuePost(string PublicationId, DateTimeOffset ScheduledUtc);

public sealed record LateDecision(string PublicationId, LateAction Action, DateTimeOffset? NewTimeUtc, string Reason);

public static class LatePolicy
{
    /// <param name="nextSlotAfter">Slot kế sau một thời điểm; null nếu hết lịch.</param>
    public static IReadOnlyList<LateDecision> Decide(
        IReadOnlyList<DuePost> posts,
        DateTimeOffset nowUtc,
        LatePolicyOptions options,
        Func<DateTimeOffset, DateTimeOffset?> nextSlotAfter)
    {
        var decisions = new List<LateDecision>();
        var caughtUp = 0;
        var cursor = nowUtc;

        // Bài trễ lâu nhất xử lý trước để đăng bù/dời không bị bài mới hơn chen ngang.
        foreach (var p in posts.OrderBy(p => p.ScheduledUtc).ThenBy(p => p.PublicationId, StringComparer.Ordinal))
        {
            var lateness = nowUtc - p.ScheduledUtc;
            if (lateness <= options.Grace)
            {
                decisions.Add(new(p.PublicationId, LateAction.PublishNow, null, "Đúng giờ."));
                continue;
            }

            switch (options.Mode)
            {
                case LateMode.Skip:
                    decisions.Add(new(p.PublicationId, LateAction.Skip, null, "Trễ lịch, chính sách bỏ qua."));
                    break;
                case LateMode.Shift:
                    if (nextSlotAfter(cursor) is { } next)
                    {
                        cursor = next;
                        decisions.Add(new(p.PublicationId, LateAction.Reschedule, next, "Trễ lịch, dời sang slot kế tiếp."));
                    }
                    else decisions.Add(new(p.PublicationId, LateAction.Skip, null, "Trễ lịch và không còn slot để dời."));
                    break;
                default:
                    if (lateness > options.MaxLateness)
                        decisions.Add(new(p.PublicationId, LateAction.Skip, null, "Trễ quá cửa sổ đăng bù."));
                    else if (caughtUp >= options.MaxCatchUp)
                        decisions.Add(new(p.PublicationId, LateAction.Skip, null, "Vượt trần số bài đăng bù."));
                    else
                    {
                        caughtUp++;
                        decisions.Add(new(p.PublicationId, LateAction.PublishNow, null, "Đăng bù."));
                    }
                    break;
            }
        }
        return decisions;
    }
}

// --- Slot giờ đăng ---

public sealed record WeeklySlot(DayOfWeek Day, TimeOnly Time);

/// <summary>Khung giờ cấm theo giờ local của kênh; Start &gt; End nghĩa là vắt qua nửa đêm.</summary>
public sealed record ForbiddenWindow(TimeOnly Start, TimeOnly End)
{
    public bool Contains(TimeOnly t) => Start <= End ? t >= Start && t < End : t >= Start || t < End;
}

public sealed record SchedulingConstraints(int MaxPerDay, TimeSpan MinGap, IReadOnlyList<ForbiddenWindow> Forbidden, int JitterMinutes = 0);

public sealed record ChannelSchedule(TimeZoneInfo TimeZone, IReadOnlyList<WeeklySlot> Slots, SchedulingConstraints Constraints);

public static class SlotScheduler
{
    /// <summary>Các slot (UTC, chưa jitter) từ <paramref name="fromUtc"/>, tăng dần. Giờ không tồn tại (DST tiến) bị bỏ; giờ lặp (DST lùi) lấy lần đầu.</summary>
    public static IReadOnlyList<DateTimeOffset> GenerateSlots(ChannelSchedule schedule, DateTimeOffset fromUtc, int horizonDays)
    {
        var tz = schedule.TimeZone;
        var startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fromUtc, tz).DateTime);
        var result = new List<DateTimeOffset>();
        for (var i = 0; i <= horizonDays; i++)
        {
            var date = startDate.AddDays(i);
            foreach (var slot in schedule.Slots.Where(s => s.Day == date.DayOfWeek))
            {
                var utc = LocalToUtc(tz, date.ToDateTime(slot.Time));
                if (utc is { } u && u >= fromUtc) result.Add(u);
            }
        }
        result.Sort();
        return result;
    }

    /// <summary>Jitter ổn định theo id (FNV-1a, không dùng string.GetHashCode vì đổi theo tiến trình) trong [-max, +max] phút.</summary>
    public static int JitterMinutes(string id, int maxMinutes)
    {
        if (maxMinutes <= 0) return 0;
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(id)) h = (h ^ b) * 16777619u;
        return (int)(h % (uint)(2 * maxMinutes + 1)) - maxMinutes;
    }

    /// <summary>Thời điểm cụ thể có thỏa ràng buộc (khung cấm, giới hạn/ngày, khoảng cách) với các bài đã có không; dùng khi đổi lịch tay.</summary>
    public static bool CanPlace(ChannelSchedule schedule, IEnumerable<DateTimeOffset> occupiedUtc, DateTimeOffset candidateUtc) =>
        Satisfies(schedule, occupiedUtc.Select(t => t.ToUniversalTime()).ToList(), candidateUtc.ToUniversalTime());

    private static DateOnly LocalDate(TimeZoneInfo tz, DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, tz).DateTime);

    private static bool Satisfies(ChannelSchedule schedule, List<DateTimeOffset> occupied, DateTimeOffset candidate)
    {
        var tz = schedule.TimeZone;
        var c = schedule.Constraints;
        var local = TimeZoneInfo.ConvertTime(candidate, tz).DateTime;
        var time = TimeOnly.FromDateTime(local);
        if (c.Forbidden.Any(f => f.Contains(time))) return false;

        var day = DateOnly.FromDateTime(local);
        if (occupied.Count(o => LocalDate(tz, o) == day) >= c.MaxPerDay) return false;
        return !occupied.Any(o => (o - candidate).Duration() < c.MinGap);
    }

    private static DateTimeOffset? LocalToUtc(TimeZoneInfo tz, DateTime local)
    {
        if (tz.IsInvalidTime(local)) return null;
        var offset = tz.IsAmbiguousTime(local) ? tz.GetAmbiguousTimeOffsets(local).Max() : tz.GetUtcOffset(local);
        return new DateTimeOffset(DateTime.SpecifyKind(local - offset, DateTimeKind.Utc));
    }
}

// --- Điểm đặt hàng sản xuất ---

/// <param name="PostsPerDay">Tốc độ tiêu thụ nội dung của chiến dịch.</param>
/// <param name="BufferDays">Đặt hàng khi tồn &lt; PostsPerDay × BufferDays.</param>
/// <param name="TargetDays">Mức tồn mục tiêu sau khi đặt; được nâng lên ít nhất bằng BufferDays.</param>
/// <param name="MaxOrder">Trần số lượng mỗi yêu cầu.</param>
public sealed record ReorderOptions(double PostsPerDay, double BufferDays, double TargetDays, int MaxOrder);

public sealed record ReorderDecision(int Quantity, string Reason)
{
    public bool ShouldOrder => Quantity > 0;
}

public static class ReorderPoint
{
    public static ReorderDecision Evaluate(int stock, bool hasOpenRequest, ReorderOptions o)
    {
        if (hasOpenRequest) return new(0, "Đã có yêu cầu đang mở.");
        if (o.PostsPerDay <= 0 || o.MaxOrder <= 0) return new(0, "Chiến dịch không tiêu thụ nội dung hoặc trần đặt bằng 0.");

        var reorderLevel = o.PostsPerDay * o.BufferDays;
        if (stock >= reorderLevel) return new(0, "Tồn kho còn đủ.");

        var target = Math.Ceiling(o.PostsPerDay * Math.Max(o.TargetDays, o.BufferDays));
        var needed = (int)Math.Min(target - stock, o.MaxOrder);
        return needed > 0 ? new(needed, "Tồn kho dưới điểm đặt hàng.") : new(0, "Tồn kho đã đạt mục tiêu.");
    }
}

// --- Chọn nội dung ---

public sealed record ContentCandidate(
    string ContentId, double Priority, DateTimeOffset AddedUtc, DateTimeOffset? ExpiresUtc, bool Evergreen, VideoSignature? Signature);

public sealed record ChannelPostRecord(string ContentId, DateTimeOffset PostedUtc, VideoSignature? Signature);

/// <param name="LruWindow">Nội dung đã đăng trên kênh trong cửa sổ này bị loại.</param>
/// <param name="EvergreenMinInterval">Evergreen chỉ đăng lại sau chu kỳ này kể từ lần đăng gần nhất.</param>
/// <param name="FreshnessHalfLife">Điểm độ mới giảm một nửa sau mỗi chu kỳ này.</param>
/// <param name="UrgencyHorizon">Hạn sử dụng còn trong khoảng này thì bắt đầu tăng điểm gấp (tuyến tính tới 1 khi hết hạn).</param>
public sealed record ContentSelectorOptions(
    TimeSpan LruWindow, TimeSpan EvergreenMinInterval, TimeSpan FreshnessHalfLife, TimeSpan UrgencyHorizon,
    double PriorityWeight = 1, double FreshnessWeight = 1, double UrgencyWeight = 1);

public sealed record ScoredContent(string ContentId, double Score);

public static class ContentSelector
{
    /// <summary>Chọn tối đa <paramref name="count"/> nội dung điểm cao nhất, đã loại hết, hết hạn, trùng LRU, evergreen chưa đủ chu kỳ và gần trùng.</summary>
    public static IReadOnlyList<ScoredContent> Select(
        IReadOnlyList<ContentCandidate> candidates,
        IReadOnlyList<ChannelPostRecord> channelHistory,
        DateTimeOffset nowUtc,
        ContentSelectorOptions options,
        Func<VideoSignature, VideoSignature, bool> areNearDuplicate,
        int count)
    {
        var recent = channelHistory.Where(h => nowUtc - h.PostedUtc < options.LruWindow).ToList();
        var accepted = new List<(ContentCandidate Item, double Score)>();

        var ranked = candidates
            .Where(c => IsEligible(c, channelHistory, recent, nowUtc, options))
            .Select(c => (Item: c, Score: Score(c, nowUtc, options)))
            .Where(x => !IsNearDuplicateOfRecent(x.Item, recent, areNearDuplicate))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.ContentId, StringComparer.Ordinal);

        foreach (var x in ranked)
        {
            if (accepted.Count >= count) break;
            // Hai ứng viên gần trùng nhau không được cùng lọt vào một lượt chọn.
            if (x.Item.Signature is { } sig && accepted.Any(a => a.Item.Signature is { } s && areNearDuplicate(sig, s))) continue;
            accepted.Add(x);
        }
        return accepted.Select(a => new ScoredContent(a.Item.ContentId, a.Score)).ToList();
    }

    public static double Score(ContentCandidate c, DateTimeOffset nowUtc, ContentSelectorOptions o)
    {
        var age = nowUtc - c.AddedUtc;
        var freshness = age <= TimeSpan.Zero ? 1 : Math.Pow(0.5, age / o.FreshnessHalfLife);
        var urgency = 0.0;
        if (c.ExpiresUtc is { } exp && o.UrgencyHorizon > TimeSpan.Zero)
            urgency = Math.Clamp(1 - (exp - nowUtc) / o.UrgencyHorizon, 0, 1);
        return o.PriorityWeight * c.Priority + o.FreshnessWeight * freshness + o.UrgencyWeight * urgency;
    }

    private static bool IsEligible(
        ContentCandidate c, IReadOnlyList<ChannelPostRecord> history, List<ChannelPostRecord> recent, DateTimeOffset now, ContentSelectorOptions o)
    {
        if (c.ExpiresUtc is { } exp && exp <= now) return false;
        if (recent.Any(h => h.ContentId == c.ContentId)) return false;
        if (c.Evergreen)
        {
            var last = history.Where(h => h.ContentId == c.ContentId).Select(h => (DateTimeOffset?)h.PostedUtc).Max();
            if (last is { } l && now - l < o.EvergreenMinInterval) return false;
        }
        return true;
    }

    private static bool IsNearDuplicateOfRecent(
        ContentCandidate c, List<ChannelPostRecord> recent, Func<VideoSignature, VideoSignature, bool> near) =>
        c.Signature is { } sig && recent.Any(h => h.Signature is { } s && near(sig, s));
}
