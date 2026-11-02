using System.Text.RegularExpressions;
using SuperMOOO.AI.Llm;
using SuperMOOO.AI.Tts;

namespace SuperMOOO.AI.Catalog;

/// <summary>Đọc/ghi lựa chọn model theo tác vụ (section ai.models) và kiểm tra tính hợp lệ trước khi lưu.</summary>
[Service]
public sealed partial class ModelProfileService(ISettingsStore settings, ModelCatalog catalog, TtsLanguageRouter router)
{
    private const string Section = "ai.models";
    private static readonly string[] CloudTts = [VoiceIds.OpenAi, VoiceIds.Gemini, VoiceIds.ElevenLabs, VoiceIds.Google];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_./:+-]{0,199}$")]
    private static partial Regex ModelId();

    public AiModelsSettings Get() => settings.Get<AiModelsSettings>(Section);

    public async Task SaveAsync(AiModelsSettings value, CancellationToken ct = default)
    {
        Validate(value);
        await settings.SetAsync(Section, value, ct);
    }

    public void Validate(AiModelsSettings s)
    {
        if (LlmProviderSpec.All.All(p => p.Id != s.TextProvider)) throw new ArgumentException("Provider nội dung không hợp lệ.");
        if (!ModelId().IsMatch(s.TextModel ?? "") || s.TextModel!.Contains("://")) throw new ArgumentException("ID model nội dung không hợp lệ.");

        if (s.AsrBackend is not ("local.whisper" or "openai" or "groq" or "gemini")) throw new ArgumentException("Backend nhận diện lời nói không hợp lệ.");
        // Gemini ASR không có preset cố định: dùng model văn bản đa phương thức bất kỳ.
        if (s.AsrBackend == "gemini" ? !ModelId().IsMatch(s.AsrModel ?? "") : catalog.Find("asr", s.AsrBackend, s.AsrModel ?? "") is null)
            throw new ArgumentException("Model nhận diện lời nói không hợp lệ.");

        if (s.Translator is not ("auto" or "llm" or "google-free" or "local-mt")) throw new ArgumentException("Bộ dịch không hợp lệ.");

        if (!VoiceIds.AllProviders.Contains(s.TtsProvider)) throw new ArgumentException("Engine TTS không hợp lệ.");
        if (string.IsNullOrWhiteSpace(s.TtsVoice) || s.TtsVoice.Length > 256) throw new ArgumentException("ID giọng đọc không hợp lệ.");
        if (CloudTts.Contains(s.TtsProvider))
        {
            if (VoiceIds.ProviderFor(s.TtsVoice) != s.TtsProvider) throw new ArgumentException("Giọng cloud không khớp engine.");
            if (!string.IsNullOrEmpty(s.TtsModel) && catalog.Find("tts", s.TtsProvider, s.TtsModel) is null) throw new ArgumentException("Model TTS không khớp provider.");
        }
    }

    /// <summary>Giọng mặc định cho ngôn ngữ: ưu tiên giọng cloud đã chọn, ngược lại giọng native hợp ngôn ngữ.</summary>
    public (string Provider, string Voice) DefaultVoice(string? language = "vi", string role = "host")
    {
        var s = Get();
        if (CloudTts.Contains(s.TtsProvider)) return (s.TtsProvider, s.TtsVoice);
        // Giọng VieNeu đã lưu chỉ hợp tiếng Việt; ngôn ngữ khác lấy giọng của model ưu tiên theo bảng (TtsLanguageRouter).
        var saved = VoiceIds.ProviderFor(s.TtsVoice) == s.TtsProvider
            && (s.TtsProvider != VoiceIds.VieNeu || LanguageCatalog.SpeechCode(language) is "vi" or "auto");
        return saved ? (s.TtsProvider, s.TtsVoice) : router.DefaultVoice(language, role == "guest");
    }
}
