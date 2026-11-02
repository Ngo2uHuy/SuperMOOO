using System.Globalization;

namespace SuperMOOO.Core.Models;

/// <summary>Khung giờ đăng theo giờ địa phương, dạng "HH:mm"; End ≤ Start nghĩa là khung qua nửa đêm.</summary>
public sealed record PostingWindow(string Start, string End)
{
    public static bool TryParse(string text, out TimeSpan value) =>
        TimeSpan.TryParseExact(text.Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out value) && value < TimeSpan.FromDays(1);
}

/// <summary>
/// Hồ sơ thị trường (S23): quốc gia, ngôn ngữ, múi giờ, khung giờ đăng và mẫu chữ dùng lại cho mọi luồng đăng sang thị trường đó.
/// Phụ đề/lồng tiếng theo ngôn ngữ sẽ thêm vào đây sau.
/// </summary>
public sealed class MarketProfile
{
    public const int MaxDailyLimit = 1000;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Mã quốc gia ISO 2 chữ cái (vd US); rỗng = không ràng buộc IP khi đăng.</summary>
    public string Country { get; set; } = "";
    /// <summary>Mã ngôn ngữ đích của tiêu đề/mô tả (vd en, ja, zh-TW).</summary>
    public string Language { get; set; } = "";
    /// <summary>Id múi giờ Windows (TimeZoneInfo); rỗng = giờ máy.</summary>
    public string TimeZoneId { get; set; } = "";
    public List<PostingWindow> Windows { get; set; } = [];
    /// <summary>Các ngày được đăng; rỗng = mọi ngày.</summary>
    public List<DayOfWeek> Days { get; set; } = [];
    public int DailyLimit { get; set; } = 20;
    public bool TranslateText { get; set; }
    public string Hashtags { get; set; } = "";
    public string TitleTemplate { get; set; } = "{title}";
    public string CaptionTemplate { get; set; } = "{title}\n{hashtags}";
    public string Privacy { get; set; } = "private";

    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    /// <summary>Kiểm tra và chuẩn hoá; ném <see cref="ArgumentException"/> kèm lý do tiếng Việt.</summary>
    public void Normalize()
    {
        Name = Name.Trim();
        if (Name.Length is 0 or > 100) throw new ArgumentException("Tên hồ sơ thị trường phải có 1–100 ký tự.");
        Country = Country.Trim().ToUpperInvariant();
        if (Country.Length is not (0 or 2) || !Country.All(char.IsAsciiLetter)) throw new ArgumentException("Quốc gia là mã 2 chữ cái (vd US, JP).");
        Language = Language.Trim();
        if (Language.Length > 20) throw new ArgumentException("Mã ngôn ngữ không hợp lệ.");
        if (TranslateText && Language.Length == 0) throw new ArgumentException("Bật dịch tiêu đề/mô tả thì phải chọn ngôn ngữ của hồ sơ.");
        TimeZoneId = TimeZoneId.Trim();
        if (TimeZoneId.Length > 0)
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("Múi giờ không có trên máy này."); }
        }
        Windows = NormalizeWindows(Windows);
        Days = [.. Days.Distinct().Order()];
        if (DailyLimit is < 1 or > MaxDailyLimit) throw new ArgumentException($"Giới hạn bài/ngày phải từ 1 đến {MaxDailyLimit}.");
        Hashtags = NormalizeHashtags(Hashtags);
        if (Privacy is not ("private" or "unlisted" or "public")) throw new ArgumentException("Quyền riêng tư phải là private, unlisted hoặc public.");
    }

    public static List<PostingWindow> NormalizeWindows(IEnumerable<PostingWindow> windows)
    {
        var list = new List<PostingWindow>();
        foreach (var w in windows)
        {
            if (!PostingWindow.TryParse(w.Start, out var start) || !PostingWindow.TryParse(w.End, out var end))
                throw new ArgumentException($"Khung giờ \"{w.Start}–{w.End}\" phải dạng HH:mm–HH:mm (vd 18:00–22:00).");
            if (start == end) throw new ArgumentException($"Khung giờ \"{w.Start}–{w.End}\" có giờ bắt đầu trùng giờ kết thúc.");
            list.Add(new PostingWindow(start.ToString(@"hh\:mm"), end.ToString(@"hh\:mm")));
        }
        return [.. list.Distinct().OrderBy(w => w.Start, StringComparer.Ordinal)];
    }

    /// <summary>"18:00-22:00, 07:00-09:00" → danh sách khung (rỗng = cả ngày).</summary>
    public static List<PostingWindow> ParseWindows(string text) =>
        NormalizeWindows(text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(['-', '–'], 2, StringSplitOptions.TrimEntries) is [var s, var e] ? new PostingWindow(s, e)
                : throw new ArgumentException($"Khung giờ \"{part}\" phải dạng HH:mm-HH:mm.")));

    public static string FormatWindows(IEnumerable<PostingWindow> windows) => string.Join(", ", windows.Select(w => $"{w.Start}-{w.End}"));

    /// <summary>Chuẩn hoá về "#a #b" (bỏ trùng, thêm # nếu thiếu).</summary>
    public static string NormalizeHashtags(string text) =>
        string.Join(' ', text.Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => "#" + t.TrimStart('#')).Where(t => t.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Quốc gia IP bắt buộc khi đăng cho tài khoản thuộc hồ sơ thị trường.</summary>
public sealed record MarketRequirement(string ProfileName, string Country);

/// <summary>Section "publish.markets": hồ sơ thị trường và ánh xạ tài khoản (connection id) → hồ sơ.</summary>
public sealed class MarketSettings
{
    public const string Section = "publish.markets";

    public List<MarketProfile> Profiles { get; set; } = [];
    public Dictionary<string, string> Accounts { get; set; } = [];

    public MarketProfile? ProfileOf(string connectionId) =>
        Accounts.TryGetValue(connectionId, out var id) ? Profiles.FirstOrDefault(p => p.Id == id) : null;

    public MarketRequirement? RequirementFor(string connectionId) =>
        ProfileOf(connectionId) is { Country.Length: 2 } p ? new MarketRequirement(p.Name, p.Country) : null;
}

/// <summary>Khung giờ đăng đã giải theo múi giờ của thị trường; mọi thời điểm vào/ra là UTC.</summary>
public sealed class PostingSchedule
{
    private const int LookAheadDays = 9;
    private readonly (TimeSpan Start, TimeSpan End)[] _windows;
    private readonly HashSet<DayOfWeek> _days;

    public PostingSchedule(TimeZoneInfo zone, IEnumerable<PostingWindow> windows, IEnumerable<DayOfWeek> days)
    {
        Zone = zone;
        _windows = [.. windows.Select(w => PostingWindow.TryParse(w.Start, out var s) && PostingWindow.TryParse(w.End, out var e) ? (s, e)
            : throw new ArgumentException($"Khung giờ \"{w.Start}–{w.End}\" không hợp lệ."))];
        if (_windows.Length == 0) _windows = [(TimeSpan.Zero, TimeSpan.Zero)];
        _days = [.. days];
    }

    public TimeZoneInfo Zone { get; }

    public DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).DateTime;

    public DateOnly LocalDay(DateTimeOffset utc) => DateOnly.FromDateTime(Local(utc));

    /// <summary>Đầu ngày địa phương kế tiếp (UTC).</summary>
    public DateTimeOffset NextDayStart(DateTimeOffset utc) => ToUtc(Local(utc).Date.AddDays(1));

    /// <summary>Thời điểm sớm nhất ≥ <paramref name="utc"/> nằm trong một khung của một ngày được phép.</summary>
    public DateTimeOffset Fit(DateTimeOffset utc)
    {
        var local = Local(utc);
        DateTime? best = null;
        // Bắt đầu từ hôm qua để khung qua nửa đêm của hôm trước còn được tính.
        for (var d = -1; d <= LookAheadDays && best is null; d++)
        {
            var date = local.Date.AddDays(d);
            if (_days.Count > 0 && !_days.Contains(date.DayOfWeek)) continue;
            foreach (var (s, e) in _windows)
            {
                var start = date + s;
                var end = e > s ? date + e : date.AddDays(1) + e;
                var at = local > start ? local : start;
                if (at < end && (best is null || at < best)) best = at;
            }
        }
        return best is { } b ? (b == local ? utc : ToUtc(b)) : utc;
    }

    private DateTimeOffset ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Giờ rơi vào khoảng nhảy giờ mùa hè không tồn tại: dời sang giờ hợp lệ kế tiếp.
        while (Zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone), TimeSpan.Zero);
    }
}
