using System.Text.RegularExpressions;

namespace SuperMOOO.Media.Whiteboard;

/// <summary>Chia lời đọc thành cue phụ đề tối đa 2 dòng (port render/cues.ts + shared/layout.ts wrapLines).</summary>
public static partial class WhiteboardCues
{
    [GeneratedRegex("""[.!?…。！？]["')\]]*$""")]
    private static partial Regex Strong();

    [GeneratedRegex("""[,;:，；：、—–-]["')\]]*$""")]
    private static partial Regex Weak();

    [GeneratedRegex("""[.!?…。！？,;:，；：]*[^.!?…。！？,;:，；：]+[.!?…。！？,;:，；：]*["')\]]*\s*""")]
    private static partial Regex Sentence();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlnum();

    /// <summary>Gói từ tham lam theo số ký tự; không cắt giữa từ.</summary>
    public static List<string> WrapLines(string text, int maxChars)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var w in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur.Length == 0) cur = w;
            else if (cur.Length + 1 + w.Length <= maxChars) cur += ' ' + w;
            else
            {
                lines.Add(cur);
                cur = w;
            }
        }

        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    private static bool Fits(string s, double perLine) => WrapLines(s, (int)perLine).Count <= 2;

    private static List<string> SplitText(string text, int max)
    {
        var perLine = max / 2.0;
        var parts = new List<string>();
        foreach (Match m in Sentence().Matches(Spaces().Replace(text, " ").Trim()))
        {
            var cur = "";
            foreach (var w in m.Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (cur.Length > 0 && !Fits(cur + ' ' + w, perLine))
                {
                    parts.Add(cur);
                    cur = w;
                }
                else cur = cur.Length > 0 ? cur + ' ' + w : w;
            }

            if (cur.Length > 0) parts.Add(cur);
        }

        // gộp mảnh quá ngắn vào mảnh sau để phụ đề không nhấp nháy
        var merged = new List<string>();
        foreach (var p in parts)
        {
            if (merged.Count > 0 && merged[^1].Length < max / 3.0 && Fits(merged[^1] + ' ' + p, perLine)) merged[^1] += ' ' + p;
            else merged.Add(p);
        }

        return merged;
    }

    private static string Norm(string s) => NonAlnum().Replace(s.Normalize(NormalizationForm.FormC).ToLowerInvariant(), "");

    // Mốc từ của TTS (Edge) không có dấu câu: ghép ngược vào token của lời đọc để cue giữ dấu và ngắt theo dấu.
    private static List<WordTiming> AttachPunctuation(string narration, IReadOnlyList<WordTiming> words)
    {
        var tokens = narration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var p = 0;
        var result = new List<WordTiming>(words.Count);
        foreach (var w in words)
        {
            var k = Norm(w.Text);
            var matched = false;
            for (var j = p; k.Length > 0 && j < Math.Min(tokens.Length, p + 4); j++)
            {
                if (Norm(tokens[j]) != k) continue;
                p = j + 1;
                result.Add(w with { Text = tokens[j] });
                matched = true;
                break;
            }

            if (!matched) result.Add(w);
        }

        return result;
    }

    /// <summary>Như Build nhưng đo bề rộng dòng thật: dòng rộng nhất vượt maxWidth thì giảm số ký tự mỗi dòng rồi chia lại (port layoutSubtitle của VideoC; ngân sách ký tự cho 16:9 làm khung dọc tràn).</summary>
    public static IReadOnlyList<SubtitleCue> Build(string narration, double durationSec, int maxCharsPerLine, Func<string, double> measure, double maxWidth,
        IReadOnlyList<WordTiming>? words = null)
    {
        var perLine = maxCharsPerLine;
        var cues = Build(narration, durationSec, perLine, words);
        double Widest(int n) => cues.Count == 0 ? 0 : cues.Max(c => WrapLines(c.Text, n).Max(measure));
        var textW = Widest(perLine);
        while (textW > maxWidth && perLine > 8)
        {
            perLine = Math.Max(8, Math.Min(perLine - 1, (int)Math.Floor(perLine * maxWidth / textW)));
            cues = Build(narration, durationSec, perLine, words);
            textW = Widest(perLine);
        }

        return cues;
    }

    public static IReadOnlyList<SubtitleCue> Build(string narration, double durationSec, int maxCharsPerLine, IReadOnlyList<WordTiming>? words = null)
    {
        var max = Math.Max(8, maxCharsPerLine * 2);
        var cues = new List<SubtitleCue>();
        if (words is { Count: > 0 })
        {
            var attached = AttachPunctuation(narration, words);
            var groups = new List<List<WordTiming>>();
            var cur = new List<WordTiming>();
            static int Len(List<WordTiming> g) => g.Sum(w => w.Text.Length) + Math.Max(0, g.Count - 1);
            static bool Punct(string t) => Strong().IsMatch(t) || Weak().IsMatch(t);
            foreach (var w in attached)
            {
                if (cur.Count > 0 && !Fits(string.Join(' ', cur.Append(w).Select(x => x.Text)), max / 2.0))
                {
                    // ưu tiên ngắt sau dấu câu cuối nếu nó nằm ở nửa sau nhóm
                    var cut = -1;
                    for (var i = cur.Count - 1; i >= 0; i--)
                        if (Punct(cur[i].Text)) { cut = i; break; }

                    if (cut >= 0 && cut < cur.Count - 1 && Len(cur.GetRange(0, cut + 1)) >= max / 2.0)
                    {
                        groups.Add(cur.GetRange(0, cut + 1));
                        cur = cur.GetRange(cut + 1, cur.Count - cut - 1);
                    }
                    else
                    {
                        groups.Add(cur);
                        cur = [];
                    }
                }

                cur.Add(w);
                if (Strong().IsMatch(w.Text) && Len(cur) >= max / 3.0)
                {
                    groups.Add(cur);
                    cur = [];
                }
            }

            if (cur.Count > 0) groups.Add(cur);
            // kéo từ lên để đuôi câu kiểu "nay!" không nháy một mình sau cue đầy
            for (var i = 1; i < groups.Count; i++)
            {
                var prev = groups[i - 1];
                var g = groups[i];
                while (Len(g) < max / 4.0 && prev.Count > 1 && !Punct(prev[^1].Text))
                {
                    g.Insert(0, prev[^1]);
                    prev.RemoveAt(prev.Count - 1);
                }
            }

            for (var i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var start = Math.Min(g[0].Start.TotalSeconds, durationSec);
                var end = i + 1 < groups.Count ? groups[i + 1][0].Start.TotalSeconds : g[^1].End.TotalSeconds + 0.3;
                end = Math.Min(Math.Max(end, start + 0.1), durationSec);
                cues.Add(new SubtitleCue(i + 1, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), string.Join(' ', g.Select(x => x.Text))));
            }

            return cues;
        }

        var texts = SplitText(narration, max);
        var total = texts.Sum(p => p.Length);
        if (total == 0) total = 1;
        var t = 0.0;
        foreach (var text in texts)
        {
            var d = (double)text.Length / total * durationSec;
            cues.Add(new SubtitleCue(cues.Count + 1, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(t + d), text));
            t += d;
        }

        return cues;
    }
}
