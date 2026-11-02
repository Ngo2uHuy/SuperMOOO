using System.Text.RegularExpressions;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;

namespace SuperMOOO.Workflows.Whiteboard;

public sealed record WhiteboardScriptRequest(string Topic, string Language, int SceneCount, string Tone, string Aspect, string? ProviderId = null, string? Model = null);

/// <summary>Phong cách ảnh (ghép vào prompt ảnh) và tone giọng kể; khoá khớp danh sách ở S18.</summary>
public static class WhiteboardStyles
{
    private static readonly Dictionary<string, string> Art = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whiteboard"] = "flat 2D vector illustration, whiteboard doodle style, clean bold black outlines, pure white background, limited soft color palette, no text, no watermark, centered composition",
        ["stickman"] = "minimal stick figure doodle, thick black marker lines, pure white background, few accent colors, no text, simple and clear",
        ["cartoon"] = "cute 2D cartoon vector art, thick black outlines, flat pastel colors, pure white background, no text, expressive characters",
        ["infographic"] = "business infographic flat vector icons, black line art with orange and teal accents, pure white background, no text, clean layout"
    };

    private static readonly Dictionary<string, string> ToneText = new(StringComparer.OrdinalIgnoreCase)
    {
        ["friendly"] = "thân thiện, gần gũi, dễ hiểu",
        ["professional"] = "chuyên nghiệp, rõ ràng, đáng tin",
        ["humor"] = "hài hước, nhẹ nhàng",
        ["dramatic"] = "kịch tính, cuốn hút, gây tò mò",
        ["inspiring"] = "kể chuyện truyền cảm hứng",
        ["psychology"] = "phân tích tâm lý sâu sắc",
        ["mystery"] = "bí ẩn, gây tò mò",
        ["educational"] = "giáo dục, dễ hiểu"
    };

    public static string ArtPrompt(string? style) => Art.GetValueOrDefault(style ?? "") ?? Art["whiteboard"];

    public static string Tone(string? tone) => ToneText.GetValueOrDefault(tone ?? "") ?? tone ?? ToneText["friendly"];
}

/// <summary>Viết kịch bản video vẽ tay và gợi ý chủ đề bằng LLM đang chọn (Cài đặt → Mô hình AI). Port từ prompts.ts của VideoC.</summary>
[Service]
public sealed partial class WhiteboardScriptService(LlmProviderRegistry llm)
{
    [GeneratedRegex("```(?:json)?", RegexOptions.IgnoreCase)]
    private static partial Regex Fence();

    public async Task<WhiteboardScript> GenerateAsync(WhiteboardScriptRequest r, CancellationToken ct)
    {
        var lang = LanguageCatalog.TextLanguage(r.Language);
        var orientation = r.Aspect == "9:16"
            ? "khung dọc 9:16 (bố cục xếp chồng theo chiều dọc)"
            : "khung ngang 16:9 (bố cục trải theo chiều ngang)";
        var system = "Bạn là biên kịch giỏi cho video YouTube/TikTok dạng hoạt hình vẽ tay (whiteboard) giải thích nội dung.\n" +
            "Chỉ trả về MỘT đối tượng JSON hợp lệ, không markdown, không giải thích, đúng cấu trúc:\n" +
            "{\"title\": string, \"thumbnailText\": string, \"scenes\": [{\"narration\": string, \"imagePrompt\": string}], \"seo\": {\"title\": string, \"description\": string, \"tags\": string[], \"hashtags\": string[]}}\n\n" +
            "Quy tắc:\n" +
            $"- Đúng {r.SceneCount} cảnh.\n" +
            $"- \"narration\": viết bằng {lang}, văn nói, giọng \"{WhiteboardStyles.Tone(r.Tone)}\". Mỗi cảnh 2-4 câu (khoảng 30-50 từ). Cảnh 1 mở đầu bằng câu hook thật mạnh. Cảnh cuối kết bằng lời kêu gọi hành động (thích, đăng ký, bình luận). Không chỉ dẫn sân khấu, không emoji, không markdown, không đánh số cảnh.\n" +
            $"- \"imagePrompt\": viết bằng tiếng Anh. Mô tả MỘT hình ẩn dụ đơn giản, cụ thể cho lời kể của cảnh, có nhân vật hoặc đồ vật dễ nhận ra. Tuyệt đối không có chữ, số, chú thích hay logo trong ảnh. Bố cục cho {orientation}.\n" +
            $"- \"title\": tiêu đề video hấp dẫn bằng {lang}.\n" +
            $"- \"thumbnailText\": tối đa 6 từ bằng {lang}, giật tít mạnh cho thumbnail.\n" +
            $"- \"seo.title\": tiêu đề chuẩn SEO bằng {lang}.\n" +
            $"- \"seo.description\": 150-300 từ bằng {lang}, nhiều từ khoá, không mốc thời gian.\n" +
            $"- \"seo.tags\": 15-25 từ khoá tìm kiếm bằng {lang}.\n" +
            "- \"seo.hashtags\": 3-5 hashtag, mỗi cái bắt đầu bằng '#'.";

        var raw = await CompleteAsync(system, $"Chủ đề: {r.Topic}", 0.8, 8192, ct, r.ProviderId, r.Model);
        JsonNode? node;
        try { node = ExtractJson(raw); }
        catch (JsonException)
        {
            var repaired = await CompleteAsync("Bạn sửa JSON hỏng. Chỉ trả về đối tượng JSON hợp lệ đã sửa, không gì khác.",
                $"Đoạn sau đáng lẽ là JSON kịch bản video nhưng không hợp lệ. Hãy sửa và giữ nguyên nội dung:\n\n{raw}", 0.2, 8192, ct, r.ProviderId, r.Model);
            try { node = ExtractJson(repaired); }
            catch (JsonException e) { throw new InvalidDataException("Không đọc được kịch bản: AI trả về JSON không hợp lệ.", e); }
        }

        return Normalize(node as JsonObject, r);
    }

    public async Task<IReadOnlyList<string>> SuggestTopicsAsync(string niche, int count, string language, CancellationToken ct, string? providerId = null, string? model = null)
    {
        var lang = LanguageCatalog.TextLanguage(language);
        var field = string.IsNullOrWhiteSpace(niche) ? "tâm lý học, tài chính cá nhân, phát triển bản thân" : niche.Trim();
        var system = "Bạn là chuyên gia chiến lược video ngắn viral. Chỉ trả về JSON {\"topics\": string[]}. " +
                     $"Đưa ra đúng {count} tiêu đề chủ đề video khác nhau, gây tò mò, tiềm năng viral cao, mỗi tiêu đề tối đa 15 từ, viết bằng {lang}. Không đánh số.";
        var raw = await CompleteAsync(system, $"Ngách: {field}", 0.9, 2048, ct, providerId, model);
        JsonNode? node;
        try { node = ExtractJson(raw); }
        catch (JsonException e) { throw new InvalidDataException("Không đọc được danh sách chủ đề: AI trả về JSON không hợp lệ.", e); }

        var list = node as JsonArray ?? (node as JsonObject)?["topics"] as JsonArray;
        var topics = (list ?? []).Select(Str).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(count).ToList();
        if (topics.Count == 0) throw new InvalidDataException("AI không trả về chủ đề nào.");
        return topics;
    }

    private async Task<string> CompleteAsync(string system, string user, double temperature, int maxTokens, CancellationToken ct, string? providerId, string? model) =>
        (await (string.IsNullOrWhiteSpace(providerId) ? llm.Selected : llm.Get(providerId)).CompleteAsync(new LlmRequest([new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)],
            Model: string.IsNullOrWhiteSpace(model) ? null : model.Trim(), MaxTokens: maxTokens, Temperature: temperature, JsonOutput: true), ct)).Text;

    private static JsonNode? ExtractJson(string raw)
    {
        var s = Fence().Replace(raw, "").Trim();
        var start = s.IndexOfAny(['{', '[']);
        if (start >= 0)
        {
            var end = s.LastIndexOf(s[start] == '{' ? '}' : ']');
            if (end > start) s = s[start..(end + 1)];
        }

        return JsonNode.Parse(s);
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static List<string> StrList(JsonNode? n) => n switch
    {
        JsonArray a => [.. a.Select(Str).Where(s => s.Length > 0)],
        JsonValue v when v.TryGetValue<string>(out var s) => [.. s.Split([',', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
        _ => []
    };

    private static WhiteboardScript Normalize(JsonObject? obj, WhiteboardScriptRequest r)
    {
        var scenes = ((obj?["scenes"] as JsonArray) ?? []).OfType<JsonObject>()
            .Select(s => new WhiteboardScene(Str(s["narration"]), Str(s["imagePrompt"])))
            .Where(s => s.Narration.Length > 0).Take(r.SceneCount)
            .Select(s => s.ImagePrompt.Length > 0 ? s : s with { ImagePrompt = $"simple illustration about: {r.Topic}" }).ToList();
        if (scenes.Count == 0) throw new InvalidDataException("Kịch bản AI trả về không có cảnh nào.");

        var title = Str(obj?["title"]) is { Length: > 0 } t ? t : r.Topic;
        var seo = obj?["seo"] as JsonObject;
        var hashtags = StrList(seo?["hashtags"]).Select(h => h.StartsWith('#') ? h : "#" + Regex.Replace(h, @"\s+", "")).Take(5).ToList();
        return new WhiteboardScript(title, Str(obj?["thumbnailText"]) is { Length: > 0 } tt ? tt : title, scenes, new WhiteboardSeo(
            Str(seo?["title"]) is { Length: > 0 } st ? st : title,
            Str(seo?["description"]) is { Length: > 0 } d ? d : string.Join(' ', scenes.Select(s => s.Narration)),
            StrList(seo?["tags"]).Take(25).ToList(), hashtags));
    }
}
