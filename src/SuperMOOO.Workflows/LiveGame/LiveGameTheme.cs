using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Workflows.LiveGame;

public sealed class LiveGameTheme
{
    private const string ResourcePrefix = "LiveGame.Themes.";
    // LogicalName dùng %(RecursiveDir) nên tên resource có dấu gạch ngược của Windows; chuẩn hoá một lần để tra theo "temple/file".
    private static readonly Lazy<Dictionary<string, string>> Resources = new(() => typeof(LiveGameTheme).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        .ToDictionary(n => n[ResourcePrefix.Length..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase));

    public string Name { get; set; } = "Chùa Online · Thắp Hương Cầu An";
    public string[] PrayerKeywords { get; set; } = [];
    public string[] ChantKeywords { get; set; } = [];
    public Dictionary<string, LiveGamePrayerTopic> Prayers { get; set; } = [];
    public Dictionary<string, string> Speech { get; set; } = [];
    public Dictionary<string, string> Backgrounds { get; set; } = [];
    /// <summary>Hành động riêng của chủ đề, dùng được trong luật quà/bình luận của S24.</summary>
    public List<LiveGameThemeAction> Actions { get; set; } = [];
    /// <summary>Quà chưa có luật ở S24: khớp tên quà (không dấu) trước khi rơi về mức xu.</summary>
    public List<LiveGameGiftAlias> GiftAliases { get; set; } = [];
    /// <summary>Danh hiệu theo tổng xu của người xem trong phiên.</summary>
    public List<LiveGameTitle> Titles { get; set; } = [];
    /// <summary>Viết tắt/tiếng lóng → chữ đầy đủ, áp cho lời người xem trước khi đưa vào LLM cầu an.</summary>
    public Dictionary<string, string> Slang { get; set; } = [];
    public List<string> Playlist { get; set; } = [];

    public static Stream? OpenResource(string theme, string file) =>
        Resources.Value.TryGetValue(theme + "/" + file, out var name) ? typeof(LiveGameTheme).Assembly.GetManifestResourceStream(name) : null;

    /// <summary>Thư mục người dùng tự thay asset của gói chủ đề (file cùng tên ghi đè bản nhúng).</summary>
    public static string OverrideDir(AppPaths paths, string theme) => Path.Combine(paths.DataRoot, "themes", theme);

    public static LiveGameTheme Load(AppPaths paths, string theme, ILogger? log = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var nodeOptions = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        JsonObject root;
        using (var stream = OpenResource(theme, "theme.json") ?? throw new InvalidOperationException("Thiếu gói chủ đề Chùa."))
            root = JsonNode.Parse(stream, documentOptions: nodeOptions) as JsonObject ?? throw new InvalidOperationException("Gói chủ đề không hợp lệ.");
        var custom = new FileInfo(Path.Combine(OverrideDir(paths, theme), "theme.json"));
        if (custom.Exists)
        {
            try
            {
                if (custom.Length > 1048576) throw new InvalidDataException("theme.json ghi đè lớn hơn 1 MB.");
                using var file = custom.OpenRead();
                // Ghi đè theo khoá cấp một (giống trang web): theme.json chỉ cần ghi phần muốn đổi, khoá thiếu giữ bản nhúng.
                var extra = JsonNode.Parse(file, documentOptions: nodeOptions) as JsonObject ?? throw new InvalidDataException("theme.json ghi đè không phải object.");
                var merged = (JsonObject)root.DeepClone();
                foreach (var (key, value) in extra) merged[key] = value?.DeepClone();
                merged.Deserialize<LiveGameTheme>(options);
                root = merged;
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            { log?.LogWarning(e, "Bỏ qua theme.json ghi đè ở {Path}; dùng gói chủ đề nhúng", custom.FullName); }
        }
        var result = root.Deserialize<LiveGameTheme>(options) ?? throw new InvalidOperationException("Gói chủ đề không hợp lệ.");
        var slang = new FileInfo(Path.Combine(paths.DataRoot, "livegame", "slang.json"));
        if (slang.Exists)
        {
            try
            {
                if (slang.Length > 262144) throw new InvalidDataException("slang.json lớn hơn 256 KB.");
                using var file = slang.OpenRead();
                result.Slang ??= [];
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(file, options) ?? [])
                    if (!string.IsNullOrWhiteSpace(key)) result.Slang[key.Trim().ToLowerInvariant()] = value;
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            { log?.LogWarning(e, "Bỏ qua bảng viết tắt {Path}", slang.FullName); }
        }
        result.Normalize();
        return result;
    }

    /// <summary>theme.json ghi đè là file người dùng tự sửa: kẹp số lượng, độ dài và giá trị trước khi engine dùng.</summary>
    private void Normalize()
    {
        PrayerKeywords ??= []; ChantKeywords ??= []; Prayers ??= []; Speech ??= []; Backgrounds ??= []; Playlist = [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Actions = (Actions ?? []).Where(a => a is not null && LiveGameActions.IsId(a.Id) && !LiveGameActions.IsBuiltIn(a.Id) && ids.Add(a.Id)).Take(32).Select(a => new LiveGameThemeAction
        {
            Id = a.Id, Label = LiveGameText.Cut(a.Label, 40), Icon = LiveGameText.Cut(a.Icon, 8), Speech = LiveGameText.Cut(a.Speech, 200),
            Effect = LiveGameActions.Effects.Contains(a.Effect) ? a.Effect : "lotus", Sound = a.Sound is "bell" or "mo" or "gong" ? a.Sound : ""
        }).ToList();
        GiftAliases = (GiftAliases ?? []).Where(g => g is not null && LiveGameActions.IsId(g.Action)).Take(200).Select(g => new LiveGameGiftAlias
        {
            Aliases = (g.Aliases ?? []).Select(a => LiveGameText.Fold(LiveGameText.Cut(a, 60))).Where(a => a.Length > 0).Take(20).ToArray(),
            Action = g.Action, Label = LiveGameText.Cut(g.Label, 40)
        }).Where(g => g.Aliases.Length > 0).ToList();
        Titles = (Titles ?? []).Where(t => t is not null && t.Min is >= 1 and <= 1_000_000_000 && !string.IsNullOrWhiteSpace(t.Label))
            .OrderBy(t => t.Min).DistinctBy(t => t.Min).Take(10).Select(t => new LiveGameTitle { Min = t.Min, Label = LiveGameText.Cut(t.Label, 24) }).ToList();
        var slang = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Slang ?? [])
        {
            var word = key?.Trim().ToLowerInvariant() ?? "";
            var full = LiveGameText.Cut(value, 40);
            if (slang.Count < 500 && word.Length is > 0 and <= 20 && !word.Any(char.IsWhiteSpace) && full.Length > 0) slang[word] = full;
        }
        Slang = slang;
    }

    public LiveGameThemeAction? Action(string id) => Actions.FirstOrDefault(a => a.Id == id);

    public string Title(long coins) => Titles.LastOrDefault(t => coins >= t.Min)?.Label ?? "";

    public string Line(string key, string fallback) => Speech.TryGetValue(key, out var line) && !string.IsNullOrWhiteSpace(line) ? line : fallback;

    public string Prayer(string text)
    {
        var topic = Prayers.FirstOrDefault(p => p.Key != "default" && p.Value.Keywords.Any(k => !string.IsNullOrWhiteSpace(k) && text.Contains(k, StringComparison.OrdinalIgnoreCase))).Value;
        var lines = (topic ?? Prayers.GetValueOrDefault("default"))?.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray() ?? [];
        return lines.Length > 0 ? lines[Random.Shared.Next(lines.Length)] : "Chúc bạn và gia đình luôn bình an, mạnh khỏe.";
    }
}

public sealed class LiveGamePrayerTopic
{
    public string[] Keywords { get; set; } = [];
    public string[] Lines { get; set; } = [];
}

/// <summary>Effect: hiệu ứng dựng sẵn trên trang dùng để diễn; Sound: thay âm thanh mặc định của hiệu ứng (bell, mo, gong).</summary>
public sealed class LiveGameThemeAction
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Effect { get; set; } = "lotus";
    public string Sound { get; set; } = "";
    public string Speech { get; set; } = "";
}

public sealed class LiveGameGiftAlias
{
    public string[] Aliases { get; set; } = [];
    public string Action { get; set; } = "incense";
    public string Label { get; set; } = "";
}

public sealed class LiveGameTitle
{
    public long Min { get; set; }
    public string Label { get; set; } = "";
}

public static partial class LiveGameActions
{
    /// <summary>Hiệu ứng trang web diễn được; hành động của theme phải trỏ về một trong số này.</summary>
    public static readonly string[] Effects = ["incense", "bow", "lotus", "festival", "chant", "heart", "spark"];
    public static readonly IReadOnlyList<(string Id, string Name)> BuiltIn =
    [
        ("incense", "Dâng hương"), ("bow", "Lễ Phật"), ("lotus", "Hoa sen"), ("festival", "Đại lễ"),
        ("chant", "Niệm Phật (lạy, hào quang)"), ("heart", "Hoa sen bay"), ("spark", "Lấp lánh"), ("outfit", "Đổi áo lam ↔ vàng")
    ];
    public static bool IsBuiltIn(string id) => BuiltIn.Any(a => a.Id == id);
    public static bool IsId(string? id) => id is not null && Id().IsMatch(id);
    [GeneratedRegex("^[a-z0-9_]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Id();
}

internal static partial class LiveGameText
{
    [GeneratedRegex(@"https?://|www\.|[\p{L}\d-]+\.(com|net|vn|org|me|io)\b|(?:\+?\d[\s.()\-]*){8,}", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateOrLink();
    [GeneratedRegex(@"[^\p{L}\p{M}\s]", RegexOptions.CultureInvariant)]
    private static partial Regex NotName();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"[\p{L}\p{M}\d]+")]
    private static partial Regex Word();

    public static string Cut(string? text, int max)
    {
        var value = new string((text ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return value.Length > max ? value[..max] : value;
    }

    /// <summary>Chữ thường, bỏ dấu tiếng Việt (kể cả đ), gộp khoảng trắng: so khớp tên quà và từ khoá không phân biệt dấu.</summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(ch is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(ch));
        }
        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    public static string[] Triggers(string trigger) =>
        trigger.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Fold).Where(t => t.Length > 0).Distinct().Take(20).ToArray();

    /// <summary>Thay từng từ viết tắt ("ko", "sk"…) bằng chữ đầy đủ; chỉ thay nguyên từ để không đụng chữ khác.</summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string> slang) => slang.Count == 0 ? text :
        Word().Replace(text, m => slang.TryGetValue(m.Value.ToLowerInvariant(), out var full) ? full : m.Value);

    public static string Filter(string text, LiveGameSettings settings, int max = 160)
    {
        text = Spaces().Replace(text.Normalize(), " ").Trim();
        if (PrivateOrLink().IsMatch(text) || settings.BannedWords.Any(w => !string.IsNullOrWhiteSpace(w) && text.Contains(w.Trim(), StringComparison.OrdinalIgnoreCase))) return "";
        return text.Length > max ? text[..max] : text;
    }

    public static string Label(string text, LiveGameSettings settings, string fallback)
    {
        var label = Filter(NotName().Replace(text, ""), settings, 40);
        return label.Length == 0 ? fallback : label;
    }

    public static string Name(string text, LiveGameSettings settings)
    {
        var name = Filter(NotName().Replace(text, ""), settings, 20);
        return name.Length == 0 ? "bạn" : name;
    }
}
