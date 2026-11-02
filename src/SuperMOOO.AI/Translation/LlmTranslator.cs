using System.Text.Json;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;

namespace SuperMOOO.AI.Translation;

/// <summary>Dịch hàng loạt qua LLM: gửi theo nhóm kèm id, bắt buộc trả đủ id đúng thứ tự (strict).</summary>
[Service(As = [typeof(ITranslator)])]
public sealed class LlmTranslator(LlmProviderRegistry llm) : ITranslator
{
    private const int BatchSize = 12;

    public string Id => "llm";
    public bool RequiresKey => true;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, TranslateOptions options, CancellationToken ct)
    {
        var target = LanguageCatalog.LocaleCode(options.TargetLanguage);
        if (target == "auto") throw new ArgumentException("Chọn ngôn ngữ đích cụ thể để dịch.");
        var system = $"Dịch phụ đề từ {LanguageCatalog.TextLanguage(options.SourceLanguage)} sang {LanguageCatalog.TextLanguage(target)}. " +
                     "Dùng chữ viết, chính tả và cách diễn đạt phù hợp vùng của mã ngôn ngữ đích. " +
                     $"Ngữ cảnh: {options.Context ?? "Tổng hợp"}. " +
                     "Trả đúng một JSON object dạng {\"translations\":[{\"id\":0,\"text\":\"bản dịch\"}]}. " +
                     "Giữ nguyên các id đầu vào, mỗi id đúng một lần. Không markdown, không giải thích.";

        var result = new string[texts.Count];
        Array.Fill(result, "");
        var pending = Enumerable.Range(0, texts.Count).Where(i => !string.IsNullOrWhiteSpace(texts[i])).ToList();
        foreach (var batch in pending.Chunk(BatchSize))
        {
            var translated = await TranslateBatchAsync(system, batch.Select((index, id) => (id, text: texts[index].Trim())).ToList(), ct);
            for (var i = 0; i < batch.Length; i++) result[batch[i]] = translated[i];
        }

        return result;
    }

    private async Task<string[]> TranslateBatchAsync(string system, List<(int id, string text)> items, CancellationToken ct)
    {
        var provider = llm.Selected;
        var payload = JsonSerializer.Serialize(items.Select(i => new { i.id, i.text }), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var messages = new List<ChatMessage> { new(ChatRole.System, system), new(ChatRole.User, payload) };

        for (var attempt = 0; ; attempt++)
        {
            var raw = (await provider.CompleteAsync(new LlmRequest(messages, MaxTokens: 4096, Temperature: 0.2, JsonOutput: true), ct)).Text;
            if (TryParse(raw, items.Count, out var parsed)) return parsed;
            if (attempt == 1)
                throw new ProviderException(provider.Id, ProviderErrorKind.BadResponse, "AI trả nhóm phụ đề không hợp lệ (thiếu/trùng id hoặc rỗng); không dùng bản nguồn thay thế.");
            messages.Add(new ChatMessage(ChatRole.Assistant, raw));
            messages.Add(new ChatMessage(ChatRole.User, "Sửa định dạng JSON. Đủ mọi id ban đầu, không đổi id và không thêm lời dẫn."));
        }
    }

    internal static bool TryParse(string raw, int expected, out string[] translated)
    {
        translated = [];
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal))
                json = json[(json.IndexOf('\n') + 1)..].TrimEnd('`', '\n', '\r', ' ');
            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.GetProperty("translations");
            if (rows.GetArrayLength() != expected) return false;

            var map = new string?[expected];
            foreach (var row in rows.EnumerateArray())
            {
                var id = row.GetProperty("id").GetInt32();
                var text = row.GetProperty("text").GetString();
                if (id < 0 || id >= expected || map[id] is not null || string.IsNullOrWhiteSpace(text)) return false;
                map[id] = text.Trim();
            }

            translated = map!;
            return true;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
