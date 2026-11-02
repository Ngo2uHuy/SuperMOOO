using SuperMOOO.AI.Keys;

namespace SuperMOOO.AI.Llm;

// Mỗi provider một class mỏng để convention DI đăng ký được nhiều ILlmProvider.
[Service(As = [typeof(ILlmProvider)])]
public sealed class OpenAiLlmProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : OpenAiCompatibleLlmProvider(LlmProviderSpec.OpenAi, http, keys, settings);

[Service(As = [typeof(ILlmProvider)])]
public sealed class GeminiLlmProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : OpenAiCompatibleLlmProvider(LlmProviderSpec.Gemini, http, keys, settings);

[Service(As = [typeof(ILlmProvider)])]
public sealed class GroqLlmProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : OpenAiCompatibleLlmProvider(LlmProviderSpec.Groq, http, keys, settings);

[Service(As = [typeof(ILlmProvider)])]
public sealed class OpenRouterLlmProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : OpenAiCompatibleLlmProvider(LlmProviderSpec.OpenRouter, http, keys, settings);

[Service(As = [typeof(ILlmProvider)])]
public sealed class DeepSeekLlmProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : OpenAiCompatibleLlmProvider(LlmProviderSpec.DeepSeek, http, keys, settings);

[Service(As = [typeof(ILlmProvider)])]
public sealed class HuggingFaceLlmProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : OpenAiCompatibleLlmProvider(LlmProviderSpec.HuggingFace, http, keys, settings);

[Service(As = [typeof(ILlmProvider)])]
public sealed class LocalLlmProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : OpenAiCompatibleLlmProvider(LlmProviderSpec.Local, http, keys, settings);

[Service(As = [typeof(ILlmProvider)])]
public sealed class OpenAiCompatLlmProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : OpenAiCompatibleLlmProvider(LlmProviderSpec.OpenAiCompat, http, keys, settings);

/// <summary>Chọn provider theo Cài đặt → Model AI.</summary>
[Service]
public sealed class LlmProviderRegistry(IEnumerable<ILlmProvider> providers, ISettingsStore settings)
{
    public ILlmProvider Get(string id) => providers.FirstOrDefault(p => p.Id == id)
        ?? throw new ProviderException(id, ProviderErrorKind.BadResponse, $"Provider LLM '{id}' không tồn tại.");

    public ILlmProvider Selected => Get(settings.Get<AiModelsSettings>("ai.models").TextProvider);
}
