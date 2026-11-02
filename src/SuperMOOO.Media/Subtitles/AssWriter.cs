namespace SuperMOOO.Media.Subtitles;

/// <summary>
/// TimeBase "Source": mốc cue tính trên video gốc (trước cắt/đổi tốc độ) nên được quy về timeline đầu ra = (t - TrimStart) / Speed;
/// "Output": mốc đã là thời gian đầu ra. PlayRes = độ phân giải đầu ra nên cỡ chữ/lề là pixel thật, preview = render thật (F13).
/// </summary>
public sealed record AssOptions(int Width, int Height, string TimeBase = "Source", TimeSpan TrimStart = default,
    double Speed = 1, TimeSpan? OutputDuration = null);

public static class AssWriter
{
    private const string Header = "[Script Info]\nScriptType: v4.00+\nPlayResX: {0}\nPlayResY: {1}\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n[V4+ Styles]\n"
        + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n";

    public static string Write(SubtitleDocument document, SubtitleStyle style, AssOptions options)
    {
        if (options.Width <= 0 || options.Height <= 0) throw new ArgumentException("PlayRes phải dương.", nameof(options));
        var speed = options.Speed > 0 ? options.Speed : 1;
        var source = options.TimeBase.Equals("Source", StringComparison.OrdinalIgnoreCase);

        var names = new Dictionary<SubtitleStyle, string> { [style] = "Default" };
        var sb = new StringBuilder(string.Format(CultureInfo.InvariantCulture, Header, options.Width, options.Height));
        var events = new StringBuilder("\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");

        foreach (var cue in document.Cues)
        {
            var effective = cue.Style ?? style;
            if (!names.TryGetValue(effective, out var name))
                names[effective] = name = "S" + names.Count.ToString(CultureInfo.InvariantCulture);

            var start = Map(cue.Start, source, options, speed);
            var end = Map(cue.End, source, options, speed);
            if (options.OutputDuration is { } max) end = end > max ? max : end;
            if (end <= start || string.IsNullOrWhiteSpace(cue.Text)) continue;

            var (left, right) = Margins(effective, options);
            var width = options.Width - left - right;
            var text = effective.KaraokeColor is null
                ? string.Join("\\N", Escape(cue.Text).Split("\\N").Select(line => string.Join(' ', line.Split(' ').Select(t => BreakLong(t, width, effective)))))
                : Karaoke(cue, source ? speed : 1, width, effective);
            events.Append("Dialogue: 0,").Append(TimeFormat.Ass(start)).Append(',').Append(TimeFormat.Ass(end)).Append(',')
                .Append(name).Append(",,0,0,0,,").Append(text).Append('\n');
        }

        foreach (var (s, name) in names) sb.Append(StyleLine(name, s, options)).Append('\n');
        return sb.Append(events).ToString();
    }

    public static async Task WriteFileAsync(string path, SubtitleDocument document, SubtitleStyle style, AssOptions options, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, Write(document, style, options), new UTF8Encoding(false), ct);
    }

    /// <summary>"#RRGGBB" → "&amp;HAABBGGRR" (alpha 00 = đặc).</summary>
    public static string Color(string? hex, int alpha = 0, string fallback = "#FFFFFF")
    {
        var h = (hex ?? fallback).Trim().TrimStart('#');
        if (h.Length == 8) h = h[2..];      // AARRGGBB (WPF) → bỏ alpha
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) h = fallback.TrimStart('#');
        return $"&H{alpha:X2}{h[4..6]}{h[2..4]}{h[0..2]}".ToUpperInvariant().Replace("&H", "&H");
    }

    private static string StyleLine(string name, SubtitleStyle s, AssOptions o)
    {
        var box = s.BoxBackground;
        var backAlpha = (int)Math.Round((1 - Math.Clamp(s.BackOpacity, 0, 1)) * 255);
        var back = Color(s.BackColor, backAlpha, "#000000");
        var primary = s.KaraokeColor is null ? Color(s.PrimaryColor) : Color(s.KaraokeColor);
        var secondary = Color(s.PrimaryColor);
        // Hộp nền (BorderStyle 3) tô bằng OutlineColour ở libass và BackColour ở VSFilter: cho cả hai cùng màu hộp.
        var outline = box ? back : Color(s.OutlineColor, 0, "#000000");
        var align = s.PositionY is not null ? 2 : s.Alignment.ToLowerInvariant() switch { "top" => 8, "middle" or "center" => 5, _ => 2 };
        var marginV = s.PositionY is { } y
            ? (int)Math.Round(o.Height * Math.Max(0.04, 1 - y / 100))
            : s.MarginV;
        var (left, right) = Margins(s, o);
        return string.Create(CultureInfo.InvariantCulture,
            $"Style: {name},{s.FontName.Replace(',', ' ')},{s.FontSize},{primary},{secondary},{outline},{back},{(s.Bold ? -1 : 0)},0,0,0,100,100,0,0,{(box ? 3 : 1)},{(box ? 6 : s.Outline)},0,{align},{left},{right},{marginV},1");
    }

    /// <summary>Lề trái/phải (px) của dải chữ; không chỉnh dải thì lề 5% hai bên.</summary>
    private static (int Left, int Right) Margins(SubtitleStyle s, AssOptions o)
    {
        if (s.BoxLeft is not { } x || s.BoxWidth is not { } w) return ((int)Math.Round(o.Width * 0.05), (int)Math.Round(o.Width * 0.05));
        x = Math.Clamp(x, 0, 95);
        w = Math.Clamp(w, 5, 100 - x);
        return ((int)Math.Round(o.Width * x / 100), (int)Math.Round(o.Width * (100 - x - w) / 100));
    }

    private static TimeSpan Map(TimeSpan t, bool source, AssOptions o, double speed)
    {
        var v = source ? (t - o.TrimStart).TotalSeconds / speed : t.TotalSeconds;
        return TimeSpan.FromSeconds(Math.Max(0, v));
    }

    private static string Escape(string text) =>
        text.Replace("{", "").Replace("}", "").Replace("\\", "").Replace("\r\n", "\n").Replace("\r", "\n").Trim().Replace("\n", "\\N");

    // \kf = tô dần theo thời lượng từng từ (centi-giây). Không có mốc từ thì chia theo số ký tự trong cue.
    private static string Karaoke(SubtitleCue cue, double speed, double width, SubtitleStyle style)
    {
        var words = cue.Words is { Count: > 0 } w
            ? w.Select(x => (Text: x.Text.Trim(), x.Start, x.End)).Where(x => x.Text.Length > 0).ToList()
            : Spread(cue);
        if (words.Count == 0) return Escape(cue.Text);

        var sb = new StringBuilder();
        var lead = (words[0].Start - cue.Start).TotalSeconds / speed;
        if (lead > 0.01) sb.Append("{\\k").Append(Cs(lead)).Append('}');
        for (var i = 0; i < words.Count; i++)
        {
            var next = i + 1 < words.Count ? words[i + 1].Start : cue.End;
            sb.Append("{\\kf").Append(Cs(Math.Max(0.01, (next - words[i].Start).TotalSeconds / speed))).Append('}')
                .Append(BreakLong(Escape(words[i].Text), width, style)).Append(i + 1 < words.Count ? " " : "");
        }

        return sb.ToString();
    }

    private static List<(string Text, TimeSpan Start, TimeSpan End)> Spread(SubtitleCue cue)
    {
        var tokens = cue.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var total = tokens.Sum(t => t.Length);
        var result = new List<(string, TimeSpan, TimeSpan)>();
        var cursor = cue.Start;
        foreach (var t in tokens)
        {
            var len = TimeSpan.FromTicks((long)((cue.End - cue.Start).Ticks * (double)t.Length / total));
            result.Add((t, cursor, cursor + len));
            cursor += len;
        }

        return result;
    }

    /// <summary>
    /// libass chỉ tự xuống dòng ở dấu cách, nên câu tiếng Nhật/Trung/Hàn (không có dấu cách) tràn ra ngoài khung.
    /// Cụm nào ước lượng rộng hơn vùng chữ thì chèn \N chia đều các dòng, ưu tiên ngắt sau dấu câu, không để dấu câu đứng đầu dòng.
    /// Độ rộng chỉ ước lượng theo cỡ chữ (ký tự toàn khổ ≈ 1 em, còn lại ≈ 0,55 em) nên cụm ngắn kiểu Latin vẫn để libass tự ngắt.
    /// </summary>
    internal static string BreakLong(string token, double width, SubtitleStyle style)
    {
        if (token.Length < 2) return token;
        double CharWidth(char ch) => style.FontSize * (IsWide(ch) ? 1.0 : style.Bold ? 0.58 : 0.55);
        var total = token.Sum(CharWidth);
        if (total <= width) return token;

        var target = total / Math.Ceiling(total / width);
        var lines = new List<string>();
        var line = new StringBuilder();
        double acc = 0;
        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            var w = CharWidth(ch);
            if (line.Length > 0 && acc + w > target * 1.02 && !NoLineStart(ch) && !PunctuationAhead(i, acc))
            {
                // Lùi về dấu câu gần nhất nếu nó nằm ở nửa sau dòng: ngắt sau dấu câu tự nhiên hơn ngắt giữa từ.
                var current = line.ToString();
                var cut = current.Length;
                double before = 0;
                for (var k = 0; k < current.Length; k++)
                {
                    before += CharWidth(current[k]);
                    if (BreakAfter(current[k]) && before >= target * 0.5) cut = k + 1;
                }

                lines.Add(current[..cut]);
                line.Clear().Append(current[cut..]);
                acc = line.ToString().Sum(CharWidth);
            }

            line.Append(ch);
            acc += w;
            if (BreakAfter(ch) && acc >= target * 0.85 && i + 1 < token.Length && !NoLineStart(token[i + 1]))
            {
                lines.Add(line.ToString());
                line.Clear();
                acc = 0;
            }
        }

        lines.Add(line.ToString());
        return string.Join("\\N", lines.Where(l => l.Length > 0));

        // Còn dấu câu phía trước mà dòng vẫn vừa khung thì kéo dài tới dấu câu thay vì ngắt giữa từ.
        bool PunctuationAhead(int from, double width0)
        {
            var a = width0;
            for (var j = from; j < token.Length; j++)
            {
                a += CharWidth(token[j]);
                if (a > width) return false;
                if (BreakAfter(token[j])) return true;
            }

            return false;
        }
    }

    private static bool IsWide(char ch) =>
        ch is >= 'ᄀ' and <= 'ᇿ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣' or >= '豈' and <= '﫿'
            or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦';

    private static bool BreakAfter(char ch) => ch is '、' or '。' or '，' or '．' or '！' or '？' or '；' or '：' or '」' or '』' or '）' or ',' or '!' or '?';

    // Dấu câu, trường âm và kana nhỏ không được đứng đầu dòng (quy tắc ngắt dòng tiếng Nhật).
    private static bool NoLineStart(char ch) => ch is '、' or '。' or '，' or '．' or '！' or '？' or '；' or '：' or '」' or '』' or '）'
        or ',' or '.' or '!' or '?' or 'ー' or 'っ' or 'ッ' or 'ゃ' or 'ゅ' or 'ょ' or 'ャ' or 'ュ' or 'ョ';

    private static string Cs(double seconds) => Math.Max(1, (int)Math.Round(seconds * 100)).ToString(CultureInfo.InvariantCulture);
}
