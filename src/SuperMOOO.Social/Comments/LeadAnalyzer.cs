using System.Text.RegularExpressions;

namespace SuperMOOO.Social.Comments;

public sealed record LeadInfo(bool IsLead, bool HasPhone, bool HasEmail, string? LeadType, string Extracted);

public sealed record KeywordCount(string Word, int Count);

public sealed record CommentAnalytics(IReadOnlyList<CommentRecord> Comments, int Positive, int Negative, int Neutral, int LeadCount,
    IReadOnlyList<KeywordCount> Keywords);

/// <summary>Phân tích cảm xúc tiếng Việt theo từ điển, bóc SĐT/email và nhu cầu mua (port từ lead_service.py).</summary>
public static partial class LeadAnalyzer
{
    private static readonly HashSet<string> Positive = new(StringComparer.Ordinal)
    {
        "tuyệt vời", "tuyệt", "chất lượng", "đẹp", "xịn", "thích", "ưng", "yêu", "ngon", "tốt", "uy tín", "xuất sắc", "đỉnh", "mượt",
        "cute", "hời", "siêu phẩm", "rất tốt", "hài lòng", "chất", "xịn xò", "giá rẻ", "đáng mua", "ủng hộ", "chăm sóc tốt", "giao nhanh",
        "đóng gói cẩn thận", "dễ thương", "10 điểm", "10/10", "love", "good", "best", "perfect", "fast", "nice", "awesome", "chuẩn",
        "chính hãng", "chất lượng cao", "ưng ý", "ưng bụng", "cháy hàng", "đỉnh nóc kịch trần", "keo lì", "hết nước chấm", "mê",
        "chúc shop đắt hàng", "tiện lợi", "rẻ mà xịn", "quá đã", "cực đẹp", "chất vải đẹp", "chất xịn", "đáng đồng tiền", "đã nhận được hàng",
        "5 sao", "shop uy tín", "đẹp lắm", "sẽ ủng hộ tiếp", "dùng rất thích", "thơm ngon", "dễ dùng", "đúng mẫu", "đúng mô tả",
        "giao hàng thần tốc", "hàng chuẩn", "shop nhiệt tình", "⭐⭐⭐⭐⭐", "❤️", "😍", "👍", "🔥", "💯"
    };

    private static readonly HashSet<string> Negative = new(StringComparer.Ordinal)
    {
        "xấu", "tệ", "dở", "lừa đảo", "rác", "kém", "hỏng", "đắt", "thất vọng", "hoàn tiền", "giật", "lag", "lừa", "không nên mua", "chậm",
        "phí tiền", "vứt", "đau đầu", "tệ hại", "tệ lắm", "không như quảng cáo", "lừa người", "chửi", "bùng", "hàng giả", "bị lỗi", "lỗi",
        "không chạy được", "hàng nhái", "fake", "bad", "worst", "scam", "trash", "thất đức", "đểu", "hàng đểu", "hàng rởm", "bom hàng",
        "treo đầu dê bán thịt chó", "giao sai", "mất tiền oan", "đừng mua", "tẩy chay", "chậm như rùa", "bục chỉ", "chất lượng tệ",
        "thái độ kém", "1 sao", "2 sao", "hư hại", "vỡ nát", "giao thiếu", "không trả lời", "quá tệ", "hàng rác", "phí tiền của", "bực mình",
        "⭐", "😡", "🤬", "👎", "💩", "🤮"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "và", "là", "có", "không", "được", "cho", "của", "các", "những", "thì", "mà", "ở", "với", "này", "đó", "ra", "vào", "để", "như",
        "theo", "đã", "đang", "sẽ", "cũng", "hoặc", "khi", "nếu", "vì", "nên", "bởi", "lại", "nữa", "qua", "từ", "lên", "xuống", "vẫn",
        "chỉ", "tại", "anh", "em", "chị", "bạn", "tôi", "mình", "shop", "admin", "ad", "gì", "ai", "sao", "nha", "ạ"
    };

    private static readonly string[] BuyKeywords =
    [
        "báo giá", "inbox", "tư vấn", "giá bao nhiêu", "bao nhiêu", "chốt", "đặt hàng", "ship", "cần mua", "liên hệ", "check ib", "ib",
        "giá sao", "zalo", "đặt 1 cái", "đặt 2 cái", "mua ở đâu", "còn hàng không", "còn không", "gửi link", "order", "xin giá", "mua ntn",
        "ship cod", "phí ship", "có size", "màu đen còn ko", "rep ib", "cho xin sđt", "check tin nhắn", "mua sỉ", "sỉ giá sao"
    ];

    private static readonly Regex SentimentPattern = new(
        string.Join('|', Positive.Union(Negative).OrderByDescending(w => w.Length).ThenBy(w => w, StringComparer.Ordinal).Select(KeywordPattern)),
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BuyRegex = new(string.Join('|', BuyKeywords.Select(KeywordPattern)), RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\b(?:không|chẳng|chưa|not|never)\s+(?:(?:hề|rất|thật|quá)\s+)?$")] private static partial Regex NegationPattern();
    [GeneratedRegex(@"(?<![\w+])(?:\+?84|0)(?:[\s.\-()]?\d){8,10}\b")] private static partial Regex PhonePattern();
    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}")] private static partial Regex EmailPattern();
    [GeneratedRegex(@"[^\w\s]")] private static partial Regex Punctuation();

    private static string KeywordPattern(string word)
    {
        var before = char.IsLetterOrDigit(word[0]) ? @"(?<!\w)" : "";
        var after = char.IsLetterOrDigit(word[^1]) ? @"(?!\w)" : "";
        return before + Regex.Escape(word) + after;
    }

    public static string AnalyzeSentiment(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "neutral";
        var lower = text.ToLowerInvariant();
        int pos = 0, neg = 0;
        foreach (Match m in SentimentPattern.Matches(lower))
        {
            var positive = Positive.Contains(m.Value);
            // Phủ định liền trước ("không tốt") đảo cực.
            if (NegationPattern().IsMatch(lower[..m.Index])) positive = !positive;
            if (positive) pos++;
            else neg++;
        }

        return pos > neg ? "positive" : neg > pos ? "negative" : "neutral";
    }

    public static LeadInfo ExtractLeadInfo(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new LeadInfo(false, false, false, null, "");

        var phones = new List<string>();
        foreach (Match m in PhonePattern().Matches(text))
        {
            var digits = Regex.Replace(m.Value, @"[^\d+]", "");
            if (digits.StartsWith("+84", StringComparison.Ordinal)) digits = "0" + digits[3..];
            else if (digits.StartsWith("84", StringComparison.Ordinal) && digits.Length >= 11) digits = "0" + digits[2..];
            if (digits.Length is 10 or 11 && digits[0] == '0' && !phones.Contains(digits)) phones.Add(digits);
        }

        var emails = EmailPattern().Matches(text).Select(m => m.Value).ToList();
        var buyIntent = BuyRegex.IsMatch(text.ToLowerInvariant());

        var parts = new List<string>();
        if (phones.Count > 0) parts.Add($"SĐT: {string.Join(", ", phones)}");
        if (emails.Count > 0) parts.Add($"Email: {string.Join(", ", emails)}");
        if (buyIntent) parts.Add("Hỏi mua / Tư vấn");

        return new LeadInfo(phones.Count > 0 || emails.Count > 0 || buyIntent, phones.Count > 0, emails.Count > 0,
            parts.Count > 0 ? string.Join(" | ", parts) : null, string.Join(", ", phones.Concat(emails)));
    }

    public static IReadOnlyList<KeywordCount> TopKeywords(IEnumerable<string> texts, int top = 18)
    {
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in texts)
            foreach (var w in Punctuation().Replace(text.ToLowerInvariant(), " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (w.Length > 1 && !StopWords.Contains(w) && !w.All(char.IsDigit))
                    freq[w] = freq.GetValueOrDefault(w) + 1;

        return freq.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(top).Select(kv => new KeywordCount(kv.Key, kv.Value)).ToList();
    }

    public static CommentAnalytics Analyze(IEnumerable<RawComment> raws)
    {
        var comments = new List<CommentRecord>();
        foreach (var raw in raws)
        {
            var lead = ExtractLeadInfo(raw.Text);
            comments.Add(new CommentRecord(raw.Author, raw.ProfileUrl, raw.Text, raw.Likes, raw.Created, AnalyzeSentiment(raw.Text),
                lead.HasPhone, lead.HasEmail, lead.IsLead, lead.LeadType));
        }

        return new CommentAnalytics(comments, comments.Count(c => c.Sentiment == "positive"), comments.Count(c => c.Sentiment == "negative"),
            comments.Count(c => c.Sentiment == "neutral"), comments.Count(c => c.IsLead), TopKeywords(comments.Select(c => c.Text)));
    }

    public static string Summary(IReadOnlyList<CommentRecord> comments, string? title = null)
    {
        if (comments.Count == 0) return "Không có dữ liệu bình luận để thống kê.";
        var total = comments.Count;
        var pos = comments.Count(c => c.Sentiment == "positive");
        var neg = comments.Count(c => c.Sentiment == "negative");
        double Pct(int n) => Math.Round(n * 100.0 / total, 1);
        return $"Thống kê bình luận & leads ({(string.IsNullOrWhiteSpace(title) ? "nội dung" : title)}): "
               + $"tổng {total:N0}; tích cực {pos:N0} ({Pct(pos)}%); tiêu cực {neg:N0} ({Pct(neg)}%); "
               + $"khách tiềm năng {comments.Count(c => c.IsLead):N0}; để lại SĐT {comments.Count(c => c.HasPhone):N0}; để lại email {comments.Count(c => c.HasEmail):N0}.";
    }
}
