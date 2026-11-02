using SuperMOOO.AI.Http;
using SuperMOOO.AI.Keys;

namespace SuperMOOO.AI.Llm;

public sealed record LlmProviderSpec(string Id, string Name, string BaseUrl, string DefaultModel, bool RequiresKey = true)
{
    public static readonly LlmProviderSpec OpenAi = new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4.1-mini");
    public static readonly LlmProviderSpec Gemini = new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.8-flash");
    public static readonly LlmProviderSpec Groq = new("groq", "Groq", "https://api.groq.com/openai/v1", "openai/gpt-oss-20b");
    public static readonly LlmProviderSpec OpenRouter = new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "openrouter/auto");
    public static readonly LlmProviderSpec DeepSeek = new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-flash");
    public static readonly LlmProviderSpec HuggingFace = new("hf", "Hugging Face", "https://router.huggingface.co/v1", "Qwen/Qwen3.5-35B-A3B");
    public static readonly LlmProviderSpec Anthropic = new("anthropic", "Anthropic (Claude)", "https://api.anthropic.com/v1", "claude-sonnet-5-5");
    // BaseUrl thật lấy từ ai.models.OpenAiCompatUrl; key tuỳ chọn (có thì gửi Bearer).
    public static readonly LlmProviderSpec OpenAiCompat = new("openai_compat", "OpenAI-compatible", "https://api.openai.com/v1", "gpt-4o-mini", RequiresKey: false);
    // BaseUrl thật lấy từ ai.models.LocalLlmUrl; model do người dùng chọn theo những gì server đang nạp.
    public static readonly LlmProviderSpec Local = new("local", "Máy chủ cục bộ", "http://127.0.0.1:11434/v1", "llama3.1", RequiresKey: false);

    // Qwen3 GGUF đóng gói chạy bằng llama-server của app (BundledLlmProvider), không phải máy chủ ngoài.
    public static readonly LlmProviderSpec Bundled = new("bundled", "Qwen3 1.7B (chạy máy)", "", "qwen3-1.7b", RequiresKey: false);

    public static IReadOnlyList<LlmProviderSpec> All { get; } = [OpenAi, Gemini, Groq, OpenRouter, DeepSeek, HuggingFace, Anthropic, OpenAiCompat, Local, Bundled];
}

/// <summary>Chat completions kiểu OpenAI cho openai/gemini/groq/openrouter/deepseek/hf; mỗi provider chỉ khác URL và vài tham số.</summary>
public class OpenAiCompatibleLlmProvider(LlmProviderSpec spec, IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : ILlmProvider
{
    private const int TransientRetries = 2;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    private static readonly TimeSpan CompleteTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private const string GeminiNativeModels = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1";

    // Model có thinking (Gemini 3.x) tiêu token cho phần suy nghĩ; trần thấp trả finish_reason=length với content rỗng.
    private const int GeminiMinTokens = 4096;

    public string Id => spec.Id;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var model = ResolveModel(request.Model);
        var body = BuildBody(spec, request, model);
        // Gemini hay trả 503 tạm thời (quá tải); không thử lại thì một lỗi thoáng qua làm hỏng cả job dài.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = await WithKeyAsync((key, c) => AiHttp.SendTextAsync(http, Id,
                    AiHttp.Json(HttpMethod.Post, BaseUrl + "/chat/completions", body, bearer: key), CompleteTimeout, c), ct);
                return ParseResponse(Id, text, model);
            }
            catch (ProviderException e) when (attempt < TransientRetries && e.Kind == ProviderErrorKind.Network)
            {
                await _delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var text = await WithKeyAsync((key, c) => AiHttp.SendTextAsync(http, Id,
            AiHttp.Json(HttpMethod.Get, BaseUrl + "/models", null, bearer: key), CheckTimeout, c), ct);
        var data = AiHttp.ParseJson(Id, text)["data"] as JsonArray
            ?? throw new ProviderException(Id, ProviderErrorKind.BadResponse, $"{spec.Name} không trả danh sách model hợp lệ.");
        return data.Select(d => d?["id"]?.GetValue<string>()).OfType<string>().Select(m => m.StartsWith("models/", StringComparison.Ordinal) ? m[7..] : m).ToList();
    }

    public async Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            // Gemini: GET /v1beta/models với x-goog-api-key (F30); provider khác: GET {base}/models.
            await WithKeyAsync((key, c) => AiHttp.SendTextAsync(http, Id, Id == LlmProviderSpec.Gemini.Id
                ? AiHttp.Json(HttpMethod.Get, GeminiNativeModels, null, googKey: key)
                : AiHttp.Json(HttpMethod.Get, BaseUrl + "/models", null, bearer: key), CheckTimeout, c), ct);
            return new ProviderCheckResult(true, null, spec.RequiresKey ? $"Key {spec.Name} hợp lệ." : $"Kết nối {BaseUrl} thành công.");
        }
        catch (ProviderException e)
        {
            return new ProviderCheckResult(false, e.Kind, e.Message);
        }
    }

    private string BaseUrl => spec.Id == LlmProviderSpec.Local.Id
        ? settings.Get<AiModelsSettings>("ai.models").LocalLlmUrl.Trim().TrimEnd('/')
        : spec.Id == LlmProviderSpec.OpenAiCompat.Id
            ? settings.Get<AiModelsSettings>("ai.models").OpenAiCompatUrl.Trim().TrimEnd('/')
            : spec.BaseUrl;

    // Server cục bộ không có key: gọi thẳng, không qua pool.
    private Task<string> WithKeyAsync(Func<string?, CancellationToken, Task<string>> call, CancellationToken ct) =>
        spec.RequiresKey ? keys.ExecuteAsync(Id, (k, c) => call(k, c), ct)
        : spec.Id == LlmProviderSpec.OpenAiCompat.Id ? keys.ExecuteOptionalAsync(Id, call, ct)
        : call(null, ct);

    private string ResolveModel(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        var s = settings.Get<AiModelsSettings>("ai.models");
        return s.TextProvider == Id && !string.IsNullOrWhiteSpace(s.TextModel) ? s.TextModel : spec.DefaultModel;
    }

    internal static JsonObject BuildBody(LlmProviderSpec spec, LlmRequest request, string model)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role.ToString().ToLowerInvariant(),
                ["content"] = m.Content
            }).ToArray()),
            ["temperature"] = request.Temperature
        };
        var maxTokens = request.MaxTokens;

        switch (spec.Id)
        {
            case "openai" when model.StartsWith("gpt-5", StringComparison.Ordinal) || model.StartsWith("gpt-6", StringComparison.Ordinal):
                // Reasoning ẩn không được ăn ngân sách đầu ra của dịch/nội dung thường.
                body["max_completion_tokens"] = maxTokens;
                body["reasoning_effort"] = model.StartsWith("gpt-6-astra", StringComparison.Ordinal) || model.StartsWith("gpt-6.1-sol", StringComparison.Ordinal) ? "low"
                    : new[] { "gpt-5.1", "gpt-5.2", "gpt-5.4", "gpt-5.6", "gpt-6" }.Any(p => model.StartsWith(p, StringComparison.Ordinal)) ? "none" : "minimal";
                body.Remove("temperature");
                maxTokens = 0;
                break;
            case "deepseek":
                body["thinking"] = new JsonObject { ["type"] = "disabled" };
                break;
            case "gemini":
                body["reasoning_effort"] = "low";
                maxTokens = Math.Max(maxTokens, GeminiMinTokens);
                break;
            case "hf" when model.StartsWith("Qwen/", StringComparison.Ordinal):
                body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
                break;
        }

        if (maxTokens > 0) body["max_tokens"] = maxTokens;
        if (request.JsonOutput) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        return body;
    }

    internal static LlmResponse ParseResponse(string id, string text, string model)
    {
        var root = AiHttp.ParseJson(id, text);
        var choice = (root["choices"] as JsonArray)?.FirstOrDefault() as JsonObject
            ?? throw new ProviderException(id, ProviderErrorKind.BadResponse, $"{id} không trả nội dung hợp lệ.");
        var content = (choice["message"]?["content"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c.Trim() : "";
        var finish = choice["finish_reason"]?.GetValue<string>();

        if (finish == "length")
            throw new ProviderException(id, ProviderErrorKind.BadResponse, content.Length == 0
                ? $"{id} hết max_tokens trước khi trả nội dung (model có thinking); tăng max_tokens hoặc chọn model không thinking."
                : $"{id} trả nội dung chưa hoàn tất (hết max_tokens); chia yêu cầu thành các phần ngắn hơn.");
        if (content.Length == 0)
            throw new ProviderException(id, ProviderErrorKind.BadResponse, $"{id} trả nội dung rỗng.");

        var usage = root["usage"];
        return new LlmResponse(content, root["model"]?.GetValue<string>() ?? model,
            usage?["prompt_tokens"]?.GetValue<int>(), usage?["completion_tokens"]?.GetValue<int>());
    }
}
