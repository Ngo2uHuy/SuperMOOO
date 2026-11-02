namespace SuperMOOO.Core.Models;

public enum LiveEventKind { Chat, Gift, Like, Follow, Share, Member, ViewerCount, Subscribe }

/// <summary>Coins là số xu của một quà (combo nhân RepeatCount). Simulated: sự kiện "Bắn thử" của S24, chỉ diễn hiệu ứng, không vào sổ công đức/thống kê.</summary>
public sealed record LiveEvent(LiveEventKind Kind, string UserId, string Nickname,
    string AvatarUrl = "", string Text = "", string GiftId = "", string GiftName = "",
    int Coins = 0, int RepeatCount = 1, bool RepeatEnd = true, int LikeCount = 0,
    int ViewerCount = 0, string MessageId = "", DateTimeOffset At = default,
    string GiftImage = "", bool Simulated = false);

public sealed class LiveGameSettings
{
    public int Port { get; set; } = 18121;
    public string TikFinityUrl { get; set; } = "ws://localhost:21213/";
    public string ThemeId { get; set; } = "temple";
    public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    public int SentencesPerMinute { get; set; } = 6;
    public double Volume { get; set; } = 0.8;
    public bool EnableSpeech { get; set; } = true;
    public bool EnableAi { get; set; }
    public bool DiagnosticLogging { get; set; }
    public int Fps { get; set; } = 30;
    public string[] BannedWords { get; set; } = [];
    public List<LiveGameGiftRule> GiftRules { get; set; } =
        [new(1, "incense", "Dâng hương"), new(5, "bow", "Lễ Phật"), new(10, "lotus", "Hoa sen"), new(100, "festival", "Đại lễ")];
    /// <summary>Đang "đã kết nối" mà không nhận gói TikFinity nào quá số phút này thì đóng và nối lại.</summary>
    public int IdleReconnectMinutes { get; set; } = 10;
    /// <summary>Giới hạn chung số lệnh bình luận (luật chat) được chạy mỗi phút cho cả phòng.</summary>
    public int CommandsPerMinute { get; set; } = 20;
    public List<LiveGameRule> Rules { get; set; } =
    [
        new() { Id = "chat-doi-ao", Source = "chat", Trigger = "!doi, !đổi, đổi áo", Action = "outfit", Label = "Đổi áo", CooldownSeconds = 30 },
        new() { Id = "chat-thinh-chuong", Source = "chat", Trigger = "thỉnh chuông, !chuong", Match = "contains", Action = "bell", Label = "Thỉnh chuông", CooldownSeconds = 60 }
    ];

    public void Validate()
    {
        if (Port is < 1024 or > 65535 || Port is 8765 or 18089 or 18090 or 18091 or 18120)
            throw new ArgumentException("Cổng phải từ 1024–65535 và không trùng cổng TTS, S20 hoặc OAuth.");
        if (!Uri.TryCreate(TikFinityUrl, UriKind.Absolute, out var uri) || uri.Scheme != "ws" || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Địa chỉ TikFinity phải là WebSocket local, ví dụ ws://localhost:21213/.");
        if (ThemeId != "temple") throw new ArgumentException("Chủ đề chưa được hỗ trợ.");
        if (SentencesPerMinute is < 1 or > 30 || !double.IsFinite(Volume) || Volume is < 0 or > 1 || Fps is not (30 or 60))
            throw new ArgumentException("Giới hạn giọng đọc 1–30 câu/phút, âm lượng 0–1, tốc độ 30 hoặc 60 fps.");
        if (GiftRules is null || GiftRules.Count is < 1 or > 12 || GiftRules.Any(r => r.MinCoins < 1 || r.MinCoins > 1000000 ||
                r.Action is not ("incense" or "bow" or "lotus" or "festival")) || GiftRules.Select(r => r.MinCoins).Distinct().Count() != GiftRules.Count)
            throw new ArgumentException("Mức quà phải dương, không trùng nhau và dùng hiệu ứng hợp lệ.");
        if (BannedWords is null || BannedWords.Length > 500 || BannedWords.Any(w => w.Length > 100))
            throw new ArgumentException("Tối đa 500 từ cấm, mỗi mục tối đa 100 ký tự.");
        if (IdleReconnectMinutes is < 1 or > 120 || CommandsPerMinute is < 1 or > 120)
            throw new ArgumentException("Thời gian nối lại khi im lặng 1–120 phút, lệnh bình luận 1–120 lần/phút.");
        Rules = CleanRules(Rules);
    }

    public const int MaxRules = 100;

    /// <summary>Kẹp mọi trường của luật (settings có thể bị sửa tay); id trùng được cấp lại để cooldown và bắn thử không lẫn nhau.</summary>
    public static List<LiveGameRule> CleanRules(List<LiveGameRule>? rules)
    {
        if (rules is null || rules.Count > MaxRules) throw new ArgumentException($"Tối đa {MaxRules} luật quà/bình luận.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<LiveGameRule>(rules.Count);
        foreach (var rule in rules.Where(r => r is not null))
        {
            var item = rule.Sanitized();
            if (!ids.Add(item.Id)) { item.Id = Guid.NewGuid().ToString("N"); ids.Add(item.Id); }
            clean.Add(item);
        }
        return clean;
    }
}

/// <summary>Luật theo mã quà / tên quà / từ khoá bình luận. Trigger: nhiều tên hoặc từ khoá, cách nhau bằng dấu phẩy, so khớp không dấu.</summary>
public sealed class LiveGameRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    /// <summary>gift | chat</summary>
    public string Source { get; set; } = "gift";
    public string GiftId { get; set; } = "";
    public string Trigger { get; set; } = "";
    /// <summary>exact | contains</summary>
    public string Match { get; set; } = "exact";
    /// <summary>Hành động dựng sẵn (incense, bow, lotus, festival, chant, heart, spark, outfit) hoặc id hành động khai báo trong theme.json.</summary>
    public string Action { get; set; } = "incense";
    public string Label { get; set; } = "";
    /// <summary>Luật bình luận: mỗi người chỉ kích hoạt lại sau số giây này.</summary>
    public int CooldownSeconds { get; set; } = 30;

    public LiveGameRule Sanitized() => new()
    {
        Id = Clean(Id, 64, c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
        Enabled = Enabled,
        Source = Source == "chat" ? "chat" : "gift",
        GiftId = Clean(GiftId, 64, c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
        Trigger = string.Join(", ", Text(Trigger, 300).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20)),
        Match = Match == "contains" ? "contains" : "exact",
        Action = Clean(Action, 32, c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') is { Length: > 0 } action ? action : "incense",
        Label = Text(Label, 40),
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 3600)
    };

    private static string Clean(string? text, int max, Func<char, bool> allowed)
    {
        var value = new string((text ?? "").Trim().Where(allowed).ToArray());
        return value.Length > max ? value[..max] : value;
    }

    private static string Text(string? text, int max)
    {
        var value = new string((text ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return value.Length > max ? value[..max] : value;
    }
}

public sealed record LiveGameGiftRule(int MinCoins, string Action, string Label);
public sealed record LiveGameDonor(string UserId, string Nickname, long Coins);
public sealed record LiveGameSession(string Id, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, long Incense, long Prayers, long Likes, bool Simulated = false);
