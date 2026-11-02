using System.Text.RegularExpressions;

namespace SuperMOOO.AI.Tts;

public sealed record MigratedVoice(string ProviderId, string VoiceId);

/// <summary>Đổi (engine, voice) của các bản cũ sang voice id mới; giọng không còn engine thì rơi về giọng VieNeu mặc định.</summary>
public static partial class VoiceIdMigrator
{
    private static readonly string[] FemaleHints = ["phuong", "huyen", "ngan", "hoaimy", "jenny", "maichi", "trang", "hamy", "mai", "thao", "aria"];

    [GeneratedRegex(@"^[a-z]{2,3}-[A-Z]{2}-\w+Neural$")]
    private static partial Regex EdgeShortName();

    public static string? ProviderFromEngine(string? engine) => engine?.Trim().ToLowerInvariant() switch
    {
        "vieneu_cpp" or "native.vieneu" => VoiceIds.VieNeu,
        "omnivoice_cpp" or "native.omnivoice" => VoiceIds.OmniVoice,
        "native.supertonic" => VoiceIds.Supertonic,
        "native.qwen3" => VoiceIds.Qwen3,
        "native.cosyvoice3" => VoiceIds.CosyVoice3,
        "native.irodori" => VoiceIds.Irodori,
        "native.chatterbox" => VoiceIds.Chatterbox,
        "native.breeze" => VoiceIds.Breeze,
        "openai_tts" or "openai" => VoiceIds.OpenAi,
        "gemini_tts" or "gemini" => VoiceIds.Gemini,
        "elevenlabs_tts" or "elevenlabs" => VoiceIds.ElevenLabs,
        "google_tts" or "google" => VoiceIds.Google,
        "edge" or "edge_tts" => VoiceIds.Edge,
        _ => null
    };

    public static MigratedVoice Migrate(string? engine, string? voice, string role = "host")
    {
        voice = voice?.Trim() ?? "";
        if (VoiceIds.ProviderFor(voice) is { } known) return new MigratedVoice(known, voice);

        // Tên Edge của ReupVideoEdit: "vi-VN-HoaiMyNeural". Giọng Windows ("sys:<tên>") đã bỏ nên rơi về giọng native mặc định bên dưới.
        if (EdgeShortName().IsMatch(voice)) return new MigratedVoice(VoiceIds.Edge, VoiceIds.EdgePrefix + voice);

        var provider = ProviderFromEngine(engine);
        if (provider == VoiceIds.OmniVoice && voice.Length > 0) throw new ArgumentException("Chọn giọng quốc tế hoặc clone OmniVoice đã lưu; mẫu clone cần WAV và transcript.");
        if (provider is VoiceIds.OpenAi or VoiceIds.Gemini or VoiceIds.ElevenLabs or VoiceIds.Google && voice.Length > 0)
            return new MigratedVoice(provider, provider + "_" + voice);

        var female = FemaleHints.Any(h => voice.Contains(h, StringComparison.OrdinalIgnoreCase));
        return new MigratedVoice(VoiceIds.VieNeu, TtsLanguageRouter.VieNeuVoice(female || role == "guest"));
    }
}
