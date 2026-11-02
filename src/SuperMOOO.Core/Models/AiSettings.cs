namespace SuperMOOO.Core.Models;

// Section "ai.models": lựa chọn provider/model theo tác vụ (API key nằm ở ISecretStore, scope "ai").
public sealed class AiModelsSettings
{
    public string TextProvider { get; set; } = "gemini";
    public string TextModel { get; set; } = "gemini-3.8-flash";
    public string AsrBackend { get; set; } = "local.whisper";     // local.whisper | openai | groq | gemini
    public string AsrModel { get; set; } = "turbo";
    public string Translator { get; set; } = "auto";              // auto (LLM nếu có key, không thì local-mt nếu đã cài, không thì Google free) | llm | google-free | local-mt
    public string TtsProvider { get; set; } = "native.vieneu";
    public string TtsVoice { get; set; } = "cpp_vieneu_minh_quan_pro";
    public string TtsModel { get; set; } = "";                    // model TTS cloud của TtsProvider; rỗng = mặc định
    public string OpenAiCompatUrl { get; set; } = "https://api.openai.com/v1";   // provider "openai_compat": mọi server tương thích OpenAI; key tuỳ chọn
    public string OpenAiTtsUrl { get; set; } = "";                // rỗng = https://api.openai.com/v1
    public string LocalLlmUrl { get; set; } = "http://127.0.0.1:11434/v1";   // provider "local" (Ollama, LM Studio…): server tương thích OpenAI, không cần key
}

// Section "ai.images": nguồn ảnh AI cho video vẽ tay.
public sealed class AiImagesSettings
{
    public string Provider { get; set; } = "pollinations";        // pollinations | openai | gemini | sdwebui
    public string Model { get; set; } = "";                       // rỗng = mặc định của provider
    public string SdWebUiUrl { get; set; } = "http://127.0.0.1:7860";
    public int PollinationsIntervalSeconds { get; set; } = 45;     // Pollinations không key chỉ cho ~1 ảnh/45 s mỗi IP; có key thì giãn cách theo giá trị này
    public string OpenAiUrl { get; set; } = "";                   // rỗng = https://api.openai.com/v1
    public string GeminiUrl { get; set; } = "";                   // rỗng = https://generativelanguage.googleapis.com/v1beta
}

// Section "tts.native": cấu hình audiocpp_server.exe.
public sealed class TtsNativeSettings
{
    public string Backend { get; set; } = "cpu";                  // cpu | cuda | vulkan
    public int Port { get; set; } = 18089;
    public int Threads { get; set; } = Math.Min(6, Environment.ProcessorCount);
    public int Device { get; set; }
    public int MaxLoadedModels { get; set; } = 2;                  // VieNeu + OmniVoice cùng nạp (~1,6 GB): đổi ngôn ngữ không phải nạp lại, không lỗi 503 khi model kia đang bận
    public int ChunkSize { get; set; } = 600;
    public int Steps { get; set; } = 32;
    public string Language { get; set; } = "vi";
    public bool LowMemory { get; set; }
    // Mã đọc (vi, ja, ko, zh, en, "*" = ngôn ngữ khác) → provider đặt lên ưu tiên 1; phần còn lại theo bảng mặc định (TtsLanguageRouter).
    public Dictionary<string, string> LanguagePriority { get; set; } = [];
}

// Section "tts.edge": endpoint không chính thức nên mặc định tắt, token cấu hình được.
public sealed class TtsEdgeSettings
{
    public bool Enabled { get; set; }
    public string TrustedClientToken { get; set; } = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    // "<định dạng token>-<phiên bản Edge>": server chỉ xét tiền tố (1 = SHA-256, 2 = CS64+SHA-256); để ở settings để sửa không cần build lại.
    public string SecMsGecVersion { get; set; } = "2-143.0.3650.75";
    public int MinIntervalMs { get; set; } = 500;
}
