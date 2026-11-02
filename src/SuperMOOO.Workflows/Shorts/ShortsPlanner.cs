using System.Text.Encodings.Web;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;
using SuperMOOO.Workflows.Subtitles;

namespace SuperMOOO.Workflows.Shorts;

public sealed record ShortClip(double Start, double End, string Title, string Hook);

public sealed record ShortsPlan(IReadOnlyList<ShortClip> Clips, string Method, string? Warning);

public sealed record ShortsPlanOptions(int Count, double MinSeconds, double MaxSeconds, bool HookMode, bool UseLlm, string Language,
    string? ProviderId = null, string? Model = null);

/// <summary>Chọn đoạn hay từ lời thoại: LLM trả JSON (mốc giây + tiêu đề + hook), chưa cấu hình LLM thì chọn theo mật độ lời.</summary>
[Service]
public sealed class ShortsPlanner(LlmProviderRegistry llm)
{
    public const double HookMaxSeconds = 10;
    private const double HookMinSeconds = 3;
    private const int MaxPromptChars = 120_000;

    private static readonly JsonSerializerOptions PromptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<ShortsPlan> PlanAsync(IReadOnlyList<SubtitleEntry> entries, double duration, ShortsPlanOptions o, CancellationToken ct)
    {
        if (entries.Count == 0) throw new InvalidOperationException("Không có lời thoại để chọn đoạn.");
        var (min, max) = o.HookMode ? (HookMinSeconds, HookMaxSeconds) : (o.MinSeconds, o.MaxSeconds);
        if (max < min) throw new ArgumentException("Độ dài tối đa phải lớn hơn hoặc bằng tối thiểu.");

        if (o.UseLlm)
        {
            try
            {
                var clips = await AskAsync(entries, duration, o, min, max, ct);
                if (clips.Count > 0) return new ShortsPlan(clips, "ai", null);
            }
            catch (ProviderException ex) when (ex.Kind == ProviderErrorKind.KeyMissing)
            {
                // Chưa nhập khoá: chưa gửi gì đi nên không tính phí, rơi về heuristic.
                return new ShortsPlan(Heuristic(entries, o.Count, min, max), "heuristic",
                    "Chưa cấu hình LLM (Cài đặt > Model AI): đã chọn đoạn theo mật độ lời thoại, không dùng AI; tiêu đề lấy từ lời thoại.");
            }

            throw new InvalidDataException("AI không chọn được đoạn nào hợp lệ; thử lại hoặc đổi model.");
        }

        return new ShortsPlan(Heuristic(entries, o.Count, min, max), "heuristic",
            "Đã chọn đoạn theo mật độ lời thoại (không dùng AI); tiêu đề lấy từ lời thoại.");
    }

    private async Task<List<ShortClip>> AskAsync(IReadOnlyList<SubtitleEntry> entries, double duration, ShortsPlanOptions o, double min, double max, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(entries.Select(e => new { start = Math.Round(e.Start, 1), end = Math.Round(e.End, 1), text = e.Text }), PromptJson);
        var task = o.HookMode
            ? $"chọn {o.Count} đoạn MỞ ĐẦU gây chú ý nhất (câu gây tò mò, gây sốc hoặc nêu kết quả/điểm nhấn), mỗi đoạn từ {min:0} đến {max:0} giây, dùng làm hook đầu video"
            : $"chọn {o.Count} đoạn hay, trọn ý, dễ viral nhất để làm video ngắn dọc, mỗi đoạn từ {min:0} đến {max:0} giây";
        var prompt = $"Bạn là biên tập viên video ngắn (TikTok/Shorts/Reels). Từ lời thoại có mốc thời gian (giây) bên dưới, {task}. " +
                     "Mốc bắt đầu/kết thúc phải trùng mốc của các câu thoại, các đoạn không chồng nhau, không vượt tổng thời lượng " + duration.ToString("0.#", CultureInfo.InvariantCulture) + " giây. " +
                     $"Với mỗi đoạn viết tiêu đề ngắn (tối đa 60 ký tự) và một câu hook gây chú ý (tối đa 80 ký tự) bằng {LanguageCatalog.TextLanguage(o.Language)}. " +
                     "Chỉ dựa vào nội dung thoại, không bịa. " +
                     "Trả đúng một JSON object {\"clips\":[{\"start\":12.5,\"end\":45.0,\"title\":\"...\",\"hook\":\"...\"}]}, không giải thích.\n" + payload;
        if (prompt.Length > MaxPromptChars) throw new InvalidOperationException("Lời thoại quá dài cho một lượt AI; dùng video ngắn hơn hoặc tắt \"Dùng AI chọn đoạn\".");

        var raw = (await (o.ProviderId is null ? llm.Selected : llm.Get(o.ProviderId)).CompleteAsync(
            new LlmRequest([new ChatMessage(ChatRole.User, prompt)], Model: o.Model, MaxTokens: 4096, Temperature: 0.5, JsonOutput: true), ct)).Text;
        return Parse(raw, entries, duration, o.Count, min, max);
    }

    internal static List<ShortClip> Parse(string raw, IReadOnlyList<SubtitleEntry> entries, double duration, int count, double min, double max)
    {
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal)) json = json[(json.IndexOf('\n') + 1)..].TrimEnd('`', '\n', '\r', ' ');
        var clips = new List<ShortClip>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("clips");
            foreach (var row in rows.EnumerateArray())
            {
                var start = row.GetProperty("start").GetDouble();
                var end = row.GetProperty("end").GetDouble();
                if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start) continue;

                // Bám mốc câu thoại gần nhất để không cắt giữa chữ.
                start = entries.OrderBy(e => Math.Abs(e.Start - start)).First().Start;
                var limit = Math.Min(duration, start + max);
                end = Math.Min(end, limit);
                end = entries.Where(e => e.End <= limit + 0.01 && e.End > start && e.End <= end + 0.5).Select(e => e.End).DefaultIfEmpty(end).Max();
                end = Math.Min(end, limit);
                if (end - start < min * 0.8 || clips.Any(c => start < c.End && end > c.Start)) continue;

                clips.Add(new ShortClip(start, end, Clean(row, "title", 80), Clean(row, "hook", 120)));
                if (clips.Count == count) break;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("AI trả danh sách đoạn không hợp lệ; thử lại hoặc đổi model.");
        }

        return clips;
    }

    private static string Clean(JsonElement row, string name, int limit)
    {
        var text = row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        return text.Length > limit ? text[..limit].TrimEnd() : text;
    }

    /// <summary>Cửa sổ trượt qua các câu thoại, điểm = ký tự/giây (lời dày = ít khoảng lặng); lấy cao nhất, không chồng nhau.</summary>
    internal static List<ShortClip> Heuristic(IReadOnlyList<SubtitleEntry> entries, int count, double min, double max)
    {
        var sorted = entries.OrderBy(e => e.Start).ToList();
        var candidates = new List<(double Start, double End, double Score, string Text)>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i].Start;
            var chars = 0;
            var end = start;
            for (var j = i; j < sorted.Count && sorted[j].End - start <= max; j++)
            {
                chars += sorted[j].Display.Length;
                end = sorted[j].End;
            }

            if (end - start >= min) candidates.Add((start, end, chars / (end - start), sorted[i].Display));
        }

        if (candidates.Count == 0)
        {
            // Không cửa sổ nào đủ dài (thoại thưa): lấy đoạn đầu vừa độ dài tối đa.
            var from = sorted[0].Start;
            return [new ShortClip(from, Math.Min(sorted[^1].End, from + max), Headline(sorted[0].Display), "")];
        }

        var picked = new List<ShortClip>();
        foreach (var c in candidates.OrderByDescending(c => c.Score))
        {
            if (picked.Any(p => c.Start < p.End && c.End > p.Start)) continue;
            picked.Add(new ShortClip(c.Start, c.End, Headline(c.Text), ""));
            if (picked.Count == count) break;
        }

        return [.. picked.OrderBy(p => p.Start)];
    }

    private static string Headline(string text)
    {
        var line = text.Replace('\n', ' ').Trim();
        var cut = line.IndexOfAny(['.', '!', '?', '。', '！', '？']);
        if (cut > 8) line = line[..cut];
        return line.Length > 60 ? line[..60].TrimEnd() : line;
    }
}
