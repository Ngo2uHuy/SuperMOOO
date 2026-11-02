using System.Text;
using System.Text.RegularExpressions;

namespace SuperMOOO.Workflows.Live;

public sealed record LiveSource(string Id, string Text, double Score);

/// <summary>
/// Tìm FAQ / đoạn thông tin sản phẩm bằng BM25 cục bộ, không gọi embedding. Bình luận live thường gõ không dấu và viết tắt,
/// nên mọi so khớp đều trên văn bản đã bỏ dấu và bung viết tắt. Chỉ dựng chỉ mục từ dữ liệu của một sản phẩm: không lẫn tri thức sản phẩm khác.
/// </summary>
public static partial class FaqRetriever
{
    private static readonly Dictionary<string, string> Shorthand = new()
    {
        ["ko"] = "khong", ["k"] = "khong", ["kh"] = "khong", ["hong"] = "khong", ["hok"] = "khong",
        ["bn"] = "bao nhieu", ["bnhieu"] = "bao nhieu", ["nhiu"] = "bao nhieu",
        ["dc"] = "duoc", ["dk"] = "duoc", ["sp"] = "san pham", ["ship"] = "giao hang", ["freeship"] = "mien phi giao hang",
        ["sz"] = "size", ["j"] = "gi", ["z"] = "vay", ["v"] = "vay", ["ak"] = "a", ["ah"] = "a",
        ["ib"] = "inbox", ["sdt"] = "so dien thoai", ["bh"] = "bao hanh", ["tt"] = "thanh toan"
    };

    // Từ không mang ý định: khớp trên chúng làm mọi câu hỏi trông giống nhau.
    private static readonly HashSet<string> Stopwords =
    [
        "a", "oi", "shop", "cho", "em", "minh", "anh", "chi", "ban", "vay", "the", "nhe", "nha", "voi", "la", "thi", "co", "khong", "duoc",
        "gi", "nao", "hoi", "inbox", "ad", "admin", "ha", "ne", "di", "luon", "nua", "ma", "cua", "nay", "do", "va", "sao", "nhung", "u", "nhi"
    ];

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\n+|(?<=[.!?;])\s+")]
    private static partial Regex ParagraphSplit();

    public static List<string> Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Replace("đ", "d").Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) plain.Append(c);

        var words = new List<string>();
        foreach (Match m in Word().Matches(plain.ToString()))
            words.AddRange(Shorthand.TryGetValue(m.Value, out var full) ? full.Split(' ') : [m.Value]);
        return words;
    }

    public static HashSet<string> Keywords(string text) => Normalize(text).Where(w => !Stopwords.Contains(w)).ToHashSet();

    private static List<string> RetrievalTokens(string text)
    {
        // Giữ nguyên cụm: "bao nhiêu" (giá/số lượng) không được khớp "bảo hành" chỉ vì cùng có "bao".
        var joined = string.Join(' ', Normalize(text)).Replace("bao hanh", "bao_hanh").Replace("bao nhieu", "bao_nhieu");
        return joined.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Stopwords.Contains(w)).ToList();
    }

    public static IReadOnlyList<LiveSource> Retrieve(string question, string facts, IReadOnlyList<LiveFaq> faqs, int limit = 5)
    {
        var docs = new List<(string Id, string Text)>();
        foreach (var paragraph in ParagraphSplit().Split(facts.Trim()))
        {
            // Chia đoạn dài nhưng không cắt giữa từ.
            var chunk = "";
            foreach (var word in paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (chunk.Length > 0 && chunk.Length + word.Length + 1 > 450)
                {
                    docs.Add(($"facts-{docs.Count + 1}", chunk));
                    chunk = "";
                }

                chunk = $"{chunk} {word}".Trim();
            }

            if (chunk.Length > 0) docs.Add(($"facts-{docs.Count + 1}", chunk));
        }

        docs.AddRange(faqs.Select((f, i) => ($"faq-{i + 1}", $"{f.Question}\n{f.Answer}")));

        var query = RetrievalTokens(question).ToHashSet();
        if (docs.Count == 0 || query.Count == 0) return [];

        var tokens = docs.Select(d => RetrievalTokens(d.Text)).ToList();
        var average = tokens.Average(t => t.Count);
        if (average == 0) average = 1;
        var frequency = query.ToDictionary(w => w, w => tokens.Count(t => t.Contains(w)));

        var ranked = new List<LiveSource>();
        for (var i = 0; i < docs.Count; i++)
        {
            var words = tokens[i];
            var score = 0.0;
            foreach (var word in query)
            {
                var count = words.Count(w => w == word);
                if (count == 0) continue;
                var idf = Math.Log(1 + (docs.Count - frequency[word] + 0.5) / (frequency[word] + 0.5));
                score += idf * count * 2.2 / (count + 1.2 * (0.25 + 0.75 * words.Count / average));
            }

            if (score > 0) ranked.Add(new LiveSource(docs[i].Id, docs[i].Text, Math.Round(score, 4)));
        }

        return ranked.OrderByDescending(s => s.Score).Take(limit).ToList();
    }

    /// <summary>
    /// FAQ có câu hỏi phủ nhiều từ khoá nhất của bình luận. Bình luận ngắn thường chỉ có một từ quyết định ("giá", "ship"),
    /// nên độ phủ của bình luận quan trọng hơn độ trùng với cả FAQ; trọng số IDF làm từ chung của nhiều FAQ nhẹ đi.
    /// </summary>
    public static (LiveFaq? Faq, double Score) Match(string question, IReadOnlyList<LiveFaq> faqs, double threshold = 0.5)
    {
        var asked = Keywords(question);
        var known = faqs.Select(f => Keywords(f.Question)).ToList();
        if (asked.Count == 0 || faqs.Count == 0) return (null, 0);

        var frequency = new Dictionary<string, int>();
        foreach (var words in known)
            foreach (var w in words) frequency[w] = frequency.GetValueOrDefault(w) + 1;

        // Từ không FAQ nào dùng vẫn báo hiệu bình luận lạc đề, nhưng nhẹ hơn từ đặc trưng của một FAQ.
        double Weight(string w) => frequency.TryGetValue(w, out var f) ? Math.Log(1 + (double)faqs.Count / f) : 0.5 * Math.Log(1 + faqs.Count);
        var total = asked.Sum(Weight);

        LiveFaq? best = null;
        var bestScore = 0.0;
        for (var i = 0; i < faqs.Count; i++)
        {
            if (known[i].Count == 0) continue;
            var shared = asked.Intersect(known[i]).Sum(Weight);
            // Độ phủ FAQ chỉ để phá hoà giữa các FAQ giải thích bình luận ngang nhau.
            var score = 0.8 * shared / total + 0.2 * shared / known[i].Sum(Weight);
            if (score > bestScore) (best, bestScore) = (faqs[i], score);
        }

        return (bestScore >= threshold ? best : null, Math.Round(bestScore, 3));
    }
}
