using SuperMOOO.AI.Keys;
using SuperMOOO.AI.Llm;

namespace SuperMOOO.AI.Translation;

/// <summary>Chọn bộ dịch theo Cài đặt → Model AI: "auto" dùng LLM khi có key, không thì dịch offline (nếu đã cài), cuối cùng Google miễn phí.</summary>
[Service]
public sealed class TranslatorRouter(IEnumerable<ITranslator> translators, ApiKeyPool keys, ISettingsStore settings)
{
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, TranslateOptions options, CancellationToken ct)
    {
        var translator = await ResolveAsync(ct);
        return await translator.TranslateAsync(texts, options, ct);
    }

    public async Task<ITranslator> ResolveAsync(CancellationToken ct)
    {
        var s = settings.Get<AiModelsSettings>("ai.models");
        var id = s.Translator switch
        {
            "llm" => "llm",
            "google-free" => "google-free",
            "local-mt" => "local-mt",
            _ => s.TextProvider is "local" or "openai_compat" || await keys.HasKeyAsync(s.TextProvider, ct) ? "llm"
                : translators.OfType<LocalMtTranslator>().Any(t => t.IsAvailable) ? "local-mt" : "google-free"
        };
        return translators.FirstOrDefault(t => t.Id == id)
            ?? throw new InvalidOperationException($"Bộ dịch '{id}' chưa được đăng ký.");
    }
}
