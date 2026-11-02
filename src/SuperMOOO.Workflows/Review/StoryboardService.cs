using System.Text.Encodings.Web;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;
using SuperMOOO.Workflows.Subtitles;

namespace SuperMOOO.Workflows.Review;

/// <summary>Một cảnh của bản review/tóm tắt. Caption = lời thoại gốc, Narration = lời bình, Dialog = thoại đọc bằng giọng thứ hai (tuỳ chọn).</summary>
public sealed record ReviewScene(double Start, double End, string Caption = "", string Narration = "", string Dialog = "");

/// <summary>Đoạn thoại (đã gộp, ≤ 15 s) làm đơn vị chọn cảnh.</summary>
public sealed record StoryWindow(double Start, double End, string Text);

public sealed record StoryboardOptions(string Mode = "summary", double TargetSeconds = 60, bool Ai = false, string Style = "Kịch tính", string Language = "vi",
    string? ProviderId = null, string? Model = null);

public sealed record StoryboardResult(IReadOnlyList<ReviewScene> Scenes, string Method, double Duration, int SourceWindows);

/// <summary>Port `timeline_service.storyboard`: chọn cảnh bám sát mốc thoại thật; AI chỉ chọn và viết lời bình, không đổi mốc.</summary>
[Service]
public sealed class StoryboardService(LlmProviderRegistry llm)
{
    public static readonly IReadOnlyList<string> Modes = ["summary", "review", "highlights"];

    private const double MaxWindowSeconds = 15;
    private const double MinNarratedSlice = 1.5;
    private const int MaxWindows = 3000;
    private const int MaxScenes = 200;
    private const int MaxPromptChars = 150_000;

    private static readonly JsonSerializerOptions PromptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Gộp thoại sát nhau thành đoạn ≤ 15 s; thoại dài hơn bị cắt nhỏ để không sinh một cảnh khổng lồ.</summary>
    public static IReadOnlyList<StoryWindow> Windows(IEnumerable<SubtitleEntry> segments, double sourceDuration)
    {
        var windows = new List<(double Start, double End, string Text)>();
        foreach (var s in segments.OrderBy(s => s.Start))
        {
            if (!double.IsFinite(s.Start) || !double.IsFinite(s.End) || s.Start < 0 || s.End <= s.Start || s.End > sourceDuration + 0.02)
                throw new InvalidDataException("Mốc thoại vượt nguồn hoặc không hợp lệ.");
            var text = s.Display.Trim();
            if (text.Length == 0) continue;

            if (windows.Count > 0 && s.End - windows[^1].Start <= MaxWindowSeconds && s.Start - windows[^1].End < 2)
            {
                var last = windows[^1];
                windows[^1] = (last.Start, Math.Max(last.End, s.End), last.Text + " " + text);
                continue;
            }

            for (var cursor = s.Start; cursor < s.End - 0.08; cursor += MaxWindowSeconds)
                windows.Add((cursor, Math.Min(s.End, cursor + MaxWindowSeconds), text.Length > 4000 ? text[..4000] : text));
        }

        return [.. windows.Select(w => new StoryWindow(w.Start, w.End, w.Text))];
    }

    /// <summary>Nguồn chưa có thoại: dùng các phần chia đều (SegmentSourceService) làm cảnh, không có lời bình.</summary>
    public static IReadOnlyList<StoryWindow> Windows(IEnumerable<RenderClip> clips) => [.. clips.Select(c => new StoryWindow(c.Start, c.End, ""))];

    public async Task<StoryboardResult> BuildAsync(IReadOnlyList<StoryWindow> windows, StoryboardOptions o, CancellationToken ct)
    {
        if (!Modes.Contains(o.Mode) || o.TargetSeconds is < 5 or > 1800) throw new ArgumentException("Chọn kiểu dựng và thời lượng từ 5 đến 1.800 giây.");
        if (windows.Count == 0) throw new InvalidOperationException("Nhập SRT hoặc nhận diện thoại trước khi dựng tóm tắt.");
        if (windows.Count > MaxWindows) throw new InvalidOperationException("Nội dung thoại quá nhiều đoạn; chọn một phần phim để xử lý.");

        var picks = o.Ai ? await AskAiAsync(windows, o, ct) : Distribute(windows, o.TargetSeconds);
        var remaining = o.TargetSeconds;
        var scenes = new List<ReviewScene>();
        foreach (var (index, narration) in picks)
        {
            var w = windows[index];
            var end = Math.Min(w.End, w.Start + remaining);
            if (end - w.Start < 0.08) break;
            // Cảnh cuối bị cắt còn quá ngắn thì lời bình của nó không thể vừa (NarrationStep từ chối > 1,8 lần): bỏ cảnh đó thay vì làm hỏng cả job.
            if (narration.Length > 0 && end < w.End && end - w.Start < Math.Min(MinNarratedSlice, w.End - w.Start)) break;
            // Cảnh bị cắt theo ngân sách thời lượng: chữ của cảnh (và lời bình lấy từ chữ) phải cắt theo, nếu không TTS dài hơn cảnh nhiều lần.
            var caption = end < w.End ? TrimToFraction(w.Text, (end - w.Start) / (w.End - w.Start)) : w.Text;
            scenes.Add(new ReviewScene(w.Start, end, caption, narration));
            remaining -= end - w.Start;
        }

        return new StoryboardResult(scenes, o.Ai ? "ai" : "transcript_excerpts", Math.Round(o.TargetSeconds - remaining, 3), windows.Count);
    }

    internal static string TrimToFraction(string text, double fraction)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keep = Math.Clamp((int)Math.Ceiling(words.Length * fraction), 1, Math.Max(1, words.Length));
        return string.Join(' ', words.Take(keep));
    }

    /// <summary>Không AI: các đoạn thoại thật rải đều theo phim, không gắn nhãn AI.</summary>
    private static List<(int Index, string Narration)> Distribute(IReadOnlyList<StoryWindow> windows, double target)
    {
        var average = windows.Average(w => w.End - w.Start);
        var count = Math.Min(Math.Min(windows.Count, MaxScenes), Math.Max(1, (int)Math.Ceiling(target / average)));
        return [.. Enumerable.Range(0, count).Select(i => (windows.Count == 1 ? 0 : (int)Math.Round(i * (windows.Count - 1.0) / Math.Max(1, count - 1)), ""))];
    }

    private async Task<List<(int Index, string Narration)>> AskAiAsync(IReadOnlyList<StoryWindow> windows, StoryboardOptions o, CancellationToken ct)
    {
        var task = o.Mode switch
        {
            "summary" => "tóm tắt diễn biến theo thứ tự",
            "review" => "viết review có nhận xét",
            _ => "lấy các điểm nổi bật"
        };
        var payload = JsonSerializer.Serialize(windows.Select((w, i) => new { id = i, start = w.Start, end = w.End, text = w.Text }), PromptJson);
        var prompt = $"Dựa DUY NHẤT vào các đoạn thoại có ID bên dưới, chọn cảnh để {task}. Thời lượng tổng tối đa {o.TargetSeconds:0} giây. " +
                     $"Phong cách {o.Style}. Viết lời bình bằng {LanguageCatalog.TextLanguage(o.Language)}. " +
                     "Trả đúng một JSON object {\"scenes\":[{\"id\":0,\"narration\":\"lời bình ngắn\"}]}. " +
                     $"Không bịa sự kiện ngoài thoại, không thay ID, tối đa {MaxScenes} cảnh.\n{payload}";
        if (prompt.Length > MaxPromptChars) throw new InvalidOperationException("Nội dung quá dài cho một lượt AI; chọn phần phim ngắn hơn.");

        var provider = o.ProviderId is null ? llm.Selected : llm.Get(o.ProviderId);
        var raw = (await provider.CompleteAsync(new LlmRequest([new ChatMessage(ChatRole.User, prompt)], Model: o.Model,
            MaxTokens: 8192, Temperature: 0.5, JsonOutput: true), ct)).Text;
        var picks = Parse(raw, windows.Count);
        if (o.Mode == "summary") picks.Sort((a, b) => a.Index.CompareTo(b.Index));
        return picks;
    }

    internal static List<(int Index, string Narration)> Parse(string raw, int windowCount)
    {
        const string Invalid = "AI trả kịch bản không hợp lệ; chưa áp dụng thay đổi.";
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal)) json = json[(json.IndexOf('\n') + 1)..].TrimEnd('`', '\n', '\r', ' ');
            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("scenes");
            if (rows.GetArrayLength() is < 1 or > MaxScenes) throw new InvalidDataException(Invalid);

            var seen = new HashSet<int>();
            var picks = new List<(int, string)>();
            foreach (var row in rows.EnumerateArray())
            {
                var id = row.GetProperty("id").GetInt32();
                var narration = row.GetProperty("narration").GetString();
                if (id < 0 || id >= windowCount || !seen.Add(id) || string.IsNullOrWhiteSpace(narration) || narration.Length > 2000)
                    throw new InvalidDataException(Invalid);
                picks.Add((id, narration.Trim()));
            }

            return picks;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(Invalid, e);
        }
    }
}
