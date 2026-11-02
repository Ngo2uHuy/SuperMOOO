namespace SuperMOOO.Core.Abstractions;

public enum ChatRole { System, User, Assistant }
public sealed record ChatMessage(ChatRole Role, string Content);
public sealed record LlmRequest(IReadOnlyList<ChatMessage> Messages, string? Model = null,
    int MaxTokens = 4096, double Temperature = 0.6, bool JsonOutput = false);
public sealed record LlmResponse(string Text, string Model, int? InputTokens, int? OutputTokens);
public sealed record ProviderCheckResult(bool Ok, ProviderErrorKind? Error, string Message);

public interface ILlmProvider
{
    string Id { get; }                          // "openai" | "gemini" | "groq" | "openrouter" | "deepseek" | "hf" | "anthropic" | "local"
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
    Task<ProviderCheckResult> CheckAsync(CancellationToken ct);   // lời gọi rẻ nhất để xác thực key
}

public sealed record TranslateOptions(string SourceLanguage, string TargetLanguage, string? Context = null);

public interface ITranslator
{
    string Id { get; }                          // "llm" | "google-free" | "local-mt"
    bool RequiresKey { get; }
    // Trả đúng số phần tử, đúng thứ tự; lỗi thì ném ProviderException, không trả văn bản nguồn im lặng.
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, TranslateOptions options, CancellationToken ct);
}
