using System.Text.RegularExpressions;

namespace SuperMOOO.Media.Subtitles;

public static class TimeFormat
{
    private static long Ms(TimeSpan t) => Math.Max(0, (long)Math.Round(t.TotalMilliseconds));

    public static string Srt(TimeSpan t) => Clock(Ms(t), ',');

    public static string Vtt(TimeSpan t) => Clock(Ms(t), '.');

    /// <summary>ASS dùng h:mm:ss.cc (phần trăm giây).</summary>
    public static string Ass(TimeSpan t)
    {
        var cs = Math.Max(0, (long)Math.Round(t.TotalSeconds * 100));
        return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    private static string Clock(long ms, char sep) =>
        $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}{sep}{ms % 1000:000}";
}

public static partial class SrtCodec
{
    [GeneratedRegex(@"^\s*(\d+):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{2}):(\d{2})[,.](\d{1,3})")]
    private static partial Regex TimeLine();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    public static SubtitleDocument Parse(string text)
    {
        var blocks = Regex.Split(text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Trim(), @"\n\s*\n");
        var cues = new List<SubtitleCue>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var at = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (at < 0) continue;
            var m = TimeLine().Match(lines[at]);
            if (!m.Success) throw new FormatException($"Không đọc được mốc thời gian SRT: {lines[at].Trim()}");
            var start = At(m, 1);
            var end = At(m, 5);
            if (end <= start) throw new FormatException($"Mốc kết thúc phải sau mốc bắt đầu: {lines[at].Trim()}");
            var body = Tags().Replace(string.Join('\n', lines.Skip(at + 1).Select(l => l.Trim())), "").Trim();
            if (body.Length == 0) continue;
            cues.Add(new SubtitleCue(cues.Count + 1, start, end, body));
        }

        return new SubtitleDocument(cues.OrderBy(c => c.Start).Select((c, i) => c with { Index = i + 1 }).ToList());
    }

    public static async Task<SubtitleDocument> ParseFileAsync(string path, CancellationToken ct = default) =>
        Parse(await File.ReadAllTextAsync(path, Encoding.UTF8, ct));

    public static string Format(SubtitleDocument document) => new SrtFormat().Render(document);

    // ".29" là 0,29 s = 290 ms, không phải 29 ms: phần lẻ đọc như số thập phân.
    private static TimeSpan At(Match m, int g) =>
        TimeSpan.FromMilliseconds(int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) * 3600000.0
            + int.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture) * 60000.0
            + int.Parse(m.Groups[g + 2].Value, CultureInfo.InvariantCulture) * 1000.0
            + double.Parse("0." + m.Groups[g + 3].Value, CultureInfo.InvariantCulture) * 1000.0);
}

public sealed class SrtFormat : ISubtitleFormat
{
    public string Extension => ".srt";
    public bool Bom => true;

    public string Render(SubtitleDocument document)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var cue in document.Cues.Where(c => !string.IsNullOrWhiteSpace(c.Text)))
            sb.Append(i++).Append("\r\n").Append(TimeFormat.Srt(cue.Start)).Append(" --> ").Append(TimeFormat.Srt(cue.End))
                .Append("\r\n").Append(cue.Text.Trim().Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
        return sb.ToString();
    }
}

public sealed class VttFormat : ISubtitleFormat
{
    public string Extension => ".vtt";
    public bool Bom => false;

    public string Render(SubtitleDocument document)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        var i = 1;
        foreach (var cue in document.Cues.Where(c => !string.IsNullOrWhiteSpace(c.Text)))
            sb.Append(i++).Append('\n').Append(TimeFormat.Vtt(cue.Start)).Append(" --> ").Append(TimeFormat.Vtt(cue.End))
                .Append('\n').Append(cue.Text.Trim()).Append("\n\n");
        return sb.ToString();
    }
}
