using SuperMOOO.AI.Http;
using SuperMOOO.AI.Keys;

namespace SuperMOOO.AI.Llm;

/// <summary>Claude qua Messages API (không tương thích OpenAI): key ở header x-api-key, system tách khỏi messages.</summary>
[Service(As = [typeof(ILlmProvider)])]
public sealed class AnthropicLlmProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings) : ILlmProvider
{
    private const string ApiVersion = "2023-06-01";
    private static readonly TimeSpan CompleteTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly LlmProviderSpec Spec = LlmProviderSpec.Anthropic;

    public string Id => Spec.Id;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var model = ResolveModel(request.Model);
        var body = BuildBody(request, model);
        var text = await keys.ExecuteAsync(Id, (key, c) => AiHttp.SendTextAsync(http, Id,
            Request(HttpMethod.Post, Spec.BaseUrl + "/messages", body, key), CompleteTimeout, c), ct);
        return ParseResponse(Id, text, model);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var text = await keys.ExecuteAsync(Id, (key, c) => AiHttp.SendTextAsync(http, Id,
            Request(HttpMethod.Get, Spec.BaseUrl + "/models?limit=100", null, key), CheckTimeout, c), ct);
        var data = AiHttp.ParseJson(Id, text)["data"] as JsonArray
            ?? throw new ProviderException(Id, ProviderErrorKind.BadResponse, $"{Spec.Name} không trả danh sách model hợp lệ.");
        return data.Select(d => d?["id"]?.GetValue<string>()).OfType<string>().ToList();
    }

    public async Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            await keys.ExecuteAsync(Id, (key, c) => AiHttp.SendTextAsync(http, Id,
                Request(HttpMethod.Get, Spec.BaseUrl + "/models?limit=1", null, key), CheckTimeout, c), ct);
            return new ProviderCheckResult(true, null, $"Key {Spec.Name} hợp lệ.");
        }
        catch (ProviderException e)
        {
            return new ProviderCheckResult(false, e.Kind, e.Message);
        }
    }

    private string ResolveModel(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        var s = settings.Get<AiModelsSettings>("ai.models");
        return s.TextProvider == Id && !string.IsNullOrWhiteSpace(s.TextModel) ? s.TextModel : Spec.DefaultModel;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, JsonNode? body, string key)
    {
        var request = AiHttp.Json(method, url, body);
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        return request;
    }

    internal static JsonObject BuildBody(LlmRequest request, string model)
    {
        // API chỉ nhận user/assistant xen kẽ trong messages; system gộp vào trường riêng, hai lượt cùng vai liền nhau gộp lại.
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var turns = new List<(string Role, string Content)>();
        foreach (var m in request.Messages.Where(m => m.Role != ChatRole.System))
        {
            var role = m.Role.ToString().ToLowerInvariant();
            if (turns.Count > 0 && turns[^1].Role == role) turns[^1] = (role, turns[^1].Content + "\n\n" + m.Content);
            else turns.Add((role, m.Content));
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["messages"] = new JsonArray(turns.Select(t => (JsonNode)new JsonObject { ["role"] = t.Role, ["content"] = t.Content }).ToArray())
        };
        if (system.Length > 0) body["system"] = request.JsonOutput ? system + "\n\nChỉ trả về một JSON object hợp lệ, không thêm chữ nào khác." : system;
        else if (request.JsonOutput) body["system"] = "Chỉ trả về một JSON object hợp lệ, không thêm chữ nào khác.";
        return body;
    }

    internal static LlmResponse ParseResponse(string id, string text, string model)
    {
        var root = AiHttp.ParseJson(id, text);
        var content = string.Concat((root["content"] as JsonArray ?? [])
            .Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]?.GetValue<string>())).Trim();
        if (root["stop_reason"]?.GetValue<string>() == "max_tokens")
            throw new ProviderException(id, ProviderErrorKind.BadResponse,
                $"{id} trả nội dung chưa hoàn tất (hết max_tokens); chia yêu cầu thành các phần ngắn hơn.");
        if (content.Length == 0)
            throw new ProviderException(id, ProviderErrorKind.BadResponse, $"{id} trả nội dung rỗng.");

        var usage = root["usage"];
        return new LlmResponse(content, root["model"]?.GetValue<string>() ?? model,
            usage?["input_tokens"]?.GetValue<int>(), usage?["output_tokens"]?.GetValue<int>());
    }
}
