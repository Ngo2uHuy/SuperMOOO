using System.Text;

namespace SuperMOOO.Core.Publishing;

public enum CampaignStatus { Active, Paused, Stopped }

/// <summary>ManualApproval: mọi bài vào AwaitingApproval (không có job upload). AutoWithinLimits: tự lên lịch nếu không có cảnh báo rủi ro.</summary>
public enum AutomationLevel { ManualApproval, AutoWithinLimits }

public enum AspectFilter { Any, Landscape, Portrait, Square }

public enum LateMode { Skip, Shift, CatchUp }

public sealed class Campaign
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public CampaignStatus Status { get; set; } = CampaignStatus.Paused;
    /// <summary>IANA hoặc Windows id; slot của chiến dịch hiểu theo giờ local của múi này (có DST).</summary>
    public string TimeZoneId { get; set; } = "UTC";
    public double Weight { get; set; } = 1;
    public AutomationLevel Automation { get; set; } = AutomationLevel.ManualApproval;
    /// <summary>Chỉ gợi ý: planner không tạo bài, người dùng xem trước lịch và tự đăng. Cột riêng vì CHECK của cột automation chỉ có 2 giá trị.</summary>
    public bool SuggestOnly { get; set; }
    /// <summary>Hạn sử dụng nội dung: quá số ngày kể từ lúc nhập thì không chọn nữa (null = không hạn).</summary>
    public int? ContentMaxAgeDays { get; set; }
    /// <summary>Ngân sách: tổng số bài chiến dịch được tạo; chạm mức → Paused.</summary>
    public int? PostBudget { get; set; }
    public int MaxConsecutiveFailures { get; set; } = 3;
    public string TitleTemplate { get; set; } = "{title}";
    public string DescriptionTemplate { get; set; } = "{caption}";
    public PublishPrivacy Privacy { get; set; } = PublishPrivacy.Private;
    /// <summary>Evergreen: nội dung đã đăng được đăng lại sau tối thiểu <see cref="EvergreenMinDays"/> ngày.</summary>
    public bool Evergreen { get; set; }
    public int EvergreenMinDays { get; set; } = 30;
    /// <summary>Lựa chọn mặc định theo nền tảng cho bài của chiến dịch (JSON), vd TikTok Inbox/Direct Post.</summary>
    public string? PlatformSettingsJson { get; set; }
    public string? StatusReason { get; set; }
    public DateTimeOffset StatusChangedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public TimeZoneInfo ResolveTimeZone() => TimeZones.Find(TimeZoneId) ?? TimeZoneInfo.Utc;
}

/// <summary>Nguồn/nhãn/ngôn ngữ/thời lượng/tỉ lệ → các kênh đích. Điều kiện null/rỗng = không lọc.</summary>
public sealed class CampaignRule
{
    public string Id { get; init; } = "";
    public string CampaignId { get; init; } = "";
    public int Position { get; set; }
    public string? SourceId { get; set; }
    /// <summary>Nội dung phải có ít nhất một nhãn trong danh sách (không phân biệt hoa/thường).</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];
    public string? Language { get; set; }
    public int? MinDurationSec { get; set; }
    public int? MaxDurationSec { get; set; }
    public AspectFilter Aspect { get; set; }
    public IReadOnlyList<string> DestinationIds { get; set; } = [];

    public bool Matches(FinishedContent c, IReadOnlyList<string> contentTags)
    {
        if (SourceId is not null && c.SourceId != SourceId) return false;
        if (!string.IsNullOrWhiteSpace(Language) && !string.Equals(c.Language?.Trim(), Language.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (Tags.Count > 0 && !Tags.Any(t => contentTags.Contains(t, StringComparer.OrdinalIgnoreCase))) return false;
        var seconds = c.DurationMs / 1000.0;
        // Thời lượng chưa biết thì không khẳng định được là thỏa: loại khi có điều kiện thời lượng.
        if (MinDurationSec is { } min && (seconds is null || seconds < min)) return false;
        if (MaxDurationSec is { } max && (seconds is null || seconds > max)) return false;
        if (Aspect != AspectFilter.Any)
        {
            if (c.Width is not { } w || c.Height is not { } h || w <= 0 || h <= 0) return false;
            var ratio = w / (double)h;
            var actual = ratio > 1.05 ? AspectFilter.Landscape : ratio < 0.95 ? AspectFilter.Portrait : AspectFilter.Square;
            if (actual != Aspect) return false;
        }
        return true;
    }
}

/// <param name="DestinationId">null = áp dụng cho mọi kênh của chiến dịch.</param>
public sealed record CampaignSlot(DayOfWeek Day, TimeOnly Time, string? DestinationId = null);

/// <summary>Khung giờ cấm theo giờ local của chiến dịch; Start &gt; End là vắt qua nửa đêm.</summary>
public sealed record TimeWindow(TimeOnly Start, TimeOnly End);

public sealed class SchedulePolicy
{
    public string CampaignId { get; init; } = "";
    public IReadOnlyList<CampaignSlot> Slots { get; set; } = [];
    /// <summary>Số bài tối đa của một kênh trong một ngày local (tính mọi bài trên kênh).</summary>
    public int MaxPerDay { get; set; } = 3;
    /// <summary>Khoảng cách tối thiểu tới mọi bài khác trên cùng kênh (kể cả bài ngoài chiến dịch).</summary>
    public int MinGapMinutes { get; set; } = 120;
    public IReadOnlyList<TimeWindow> Forbidden { get; set; } = [];
    public int JitterMinutes { get; set; }
    public LateMode LateMode { get; set; } = LateMode.Skip;
    /// <summary>Trễ không quá mức này vẫn đăng như bình thường.</summary>
    public int LateGraceMinutes { get; set; } = 15;
    public int LateMaxCatchUp { get; set; } = 1;
    public int LateMaxLatenessMinutes { get; set; } = 360;
    /// <summary>Planner xếp trước tối đa bấy nhiêu ngày.</summary>
    public int HorizonDays { get; set; } = 7;

    public IReadOnlyList<CampaignSlot> SlotsFor(string destinationId) =>
        Slots.Where(s => s.DestinationId is null || s.DestinationId == destinationId).ToList();
}

public static class TimeZones
{
    public static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var trimmed = id.Trim();
        if (TryFind(trimmed) is { } tz) return tz;
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(trimmed, out var win) ? TryFind(win)
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(trimmed, out var iana) ? TryFind(iana) : null;
    }

    private static TimeZoneInfo? TryFind(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }
}

/// <summary>Mẫu caption: {title} {caption} {tags} {hashtags} {source} {language}.</summary>
public static class CaptionTemplate
{
    public static readonly string[] Variables = ["{title}", "{caption}", "{tags}", "{hashtags}", "{source}", "{language}"];

    public static string Render(string template, FinishedContent item, IReadOnlyList<string> tags, string? sourceName)
    {
        var hashtags = tags.Select(t => "#" + new string(t.Where(char.IsLetterOrDigit).ToArray())).Where(t => t.Length > 1);
        var sb = new StringBuilder(template);
        sb.Replace("{title}", item.Title)
            .Replace("{caption}", item.Caption ?? "")
            .Replace("{tags}", string.Join(", ", tags))
            .Replace("{hashtags}", string.Join(' ', hashtags))
            .Replace("{source}", sourceName ?? "")
            .Replace("{language}", item.Language ?? "");
        return sb.ToString().Trim();
    }
}
