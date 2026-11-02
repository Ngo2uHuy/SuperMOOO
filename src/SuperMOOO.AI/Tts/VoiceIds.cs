namespace SuperMOOO.AI.Tts;

/// <summary>Voice id mang tiền tố provider nên định tuyến không cần tra bảng.</summary>
public static class VoiceIds
{
    public const string VieNeu = "native.vieneu";
    public const string OmniVoice = "native.omnivoice";
    public const string Supertonic = "native.supertonic";
    public const string Qwen3 = "native.qwen3";
    public const string CosyVoice3 = "native.cosyvoice3";
    public const string Irodori = "native.irodori";
    public const string Chatterbox = "native.chatterbox";
    public const string Breeze = "native.breeze";
    public const string Edge = "edge";
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";
    public const string ElevenLabs = "elevenlabs";
    public const string Google = "google";

    public const string EdgePrefix = "edge:";
    public const string CloneOmniPrefix = "cpp_omni_clone_";
    /// <summary>Cùng clone OmniVoice nhưng đọc bằng VieNeu (clone mẫu tiếng Việt có codes + embedding VieNeu).</summary>
    public const string CloneVieNeuPrefix = "cpp_vieneu_clone_";

    public static IReadOnlyList<string> AllProviders { get; } = [VieNeu, OmniVoice, Supertonic, Qwen3, CosyVoice3, Irodori, Chatterbox, Breeze, Edge, OpenAi, Gemini, ElevenLabs, Google];

    public static string? ProviderFor(string? voiceId) => voiceId switch
    {
        null or "" => null,
        _ when voiceId.StartsWith("cpp_vieneu_", StringComparison.Ordinal) => VieNeu,
        _ when voiceId.StartsWith("cpp_omni_", StringComparison.Ordinal) => OmniVoice,
        _ when voiceId.StartsWith("cpp_supertonic_", StringComparison.Ordinal) => Supertonic,
        _ when voiceId.StartsWith("cpp_qwen3_", StringComparison.Ordinal) => Qwen3,
        _ when voiceId.StartsWith("cpp_cosy_", StringComparison.Ordinal) => CosyVoice3,
        _ when voiceId.StartsWith("cpp_irodori_", StringComparison.Ordinal) => Irodori,
        _ when voiceId.StartsWith("cpp_chatterbox_", StringComparison.Ordinal) => Chatterbox,
        _ when voiceId.StartsWith("cpp_breeze_", StringComparison.Ordinal) => Breeze,
        _ when voiceId.StartsWith(EdgePrefix, StringComparison.Ordinal) => Edge,
        _ when voiceId.StartsWith("openai_", StringComparison.Ordinal) => OpenAi,
        _ when voiceId.StartsWith("gemini_", StringComparison.Ordinal) => Gemini,
        _ when voiceId.StartsWith("elevenlabs_", StringComparison.Ordinal) => ElevenLabs,
        _ when voiceId.StartsWith("google_", StringComparison.Ordinal) => Google,
        _ => null
    };
}
