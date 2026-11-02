using System.Text.RegularExpressions;

namespace SuperMOOO.AI.Translation;

/// <summary>Chia văn bản theo câu để mỗi phần không vượt giới hạn ký tự của endpoint.</summary>
public static partial class TextChunker
{
    [GeneratedRegex(@"(?<=[.!?。！？])\s+|\n+")]
    private static partial Regex SentenceBreak();

    public static IReadOnlyList<string> Split(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = "";
        foreach (var raw in SentenceBreak().Split(text.Trim()))
        {
            var sentence = raw.Trim();
            while (sentence.Length > maxChars)
            {
                if (current.Length > 0) { chunks.Add(current); current = ""; }
                var cut = sentence.LastIndexOf(' ', maxChars);
                cut = cut > maxChars / 2 ? cut : maxChars;
                chunks.Add(sentence[..cut].Trim());
                sentence = sentence[cut..].Trim();
            }

            if (sentence.Length == 0) continue;
            if (current.Length > 0 && current.Length + 1 + sentence.Length > maxChars) { chunks.Add(current); current = ""; }
            current = current.Length == 0 ? sentence : current + " " + sentence;
        }

        if (current.Length > 0) chunks.Add(current);
        return chunks;
    }
}
