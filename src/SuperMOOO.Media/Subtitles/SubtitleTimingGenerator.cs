using System.Text.RegularExpressions;

namespace SuperMOOO.Media.Subtitles;

public static partial class SubtitleTimingGenerator
{
    private const int LongChars = 60;
    private const int LineChars = 45;

    [GeneratedRegex(@"(?<=[.?!;。！？])\s+|\n+")]
    private static partial Regex Boundary();

    public static IReadOnlyList<string> Split(string text)
    {
        var result = new List<string>();
        foreach (var part in Boundary().Split(text ?? ""))
        {
            var clean = part.Trim();
            if (clean.Length == 0) continue;
            if (clean.Length <= LongChars) { result.Add(clean); continue; }

            var line = new StringBuilder();
            foreach (var word in clean.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + word.Length + 1 > LineChars)
                {
                    result.Add(line.ToString());
                    line.Clear();
                }

                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }

            if (line.Length > 0) result.Add(line.ToString());
        }

        return result;
    }
}
