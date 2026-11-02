namespace SuperMOOO.Core.Models;

// Bảng live_product / live_faq. ClipPath: clip khớp môi dựng sẵn (GĐ4), rỗng = chỉ đọc TTS trên nền video lặp.
public sealed record LiveProduct(string Id, string Name, string Facts, string Script, string? ClipPath = null, DateTimeOffset? UpdatedAt = null);

public sealed record LiveFaq(string Id, string ProductId, string Question, string Answer, string? ClipPath = null, DateTimeOffset? UpdatedAt = null);

/// <summary>Nguồn trả lời bình luận: local = chỉ LLM, auto = FAQ trước rồi LLM, cloud = LLM đám mây.</summary>
public static class LiveAnswerSources
{
    public const string Local = "local";
    public const string Auto = "auto";
    public const string Cloud = "cloud";
    public static readonly IReadOnlyList<string> All = [Local, Auto, Cloud];
}

/// <summary>Khung phát riêng cho từng nền tảng; chỉ lưu bố cục, không lưu token hay stream key.</summary>
public sealed class LiveBroadcastProfile
{
    public string Layout { get; set; } = "";       // landscape | portrait; rỗng = theo nền tảng
    public string Fit { get; set; } = "contain";   // contain | cover
}

// Section "live".
public sealed class LiveSettings
{
    public string AnswerSource { get; set; } = LiveAnswerSources.Local;
    public string LocalProvider { get; set; } = "bundled";        // bundled (Qwen3 GGUF đóng gói) | local (Ollama/LM Studio)
    public string LocalModel { get; set; } = "qwen3:1.7b";       // model khi LocalProvider = local (Ollama/LM Studio)
    public string CloudProvider { get; set; } = "openai";
    public string CloudModel { get; set; } = "";                  // rỗng = model mặc định của provider
    public bool SpeakAnswers { get; set; } = true;
    public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    public string Platform { get; set; } = "tiktok";
    public Dictionary<string, LiveBroadcastProfile> Profiles { get; set; } = [];
    public string LoopVideoPath { get; set; } = "";               // nền video lặp khi không phát clip/câu trả lời
    public int Port { get; set; } = 18120;                         // tránh 8765 (OAuth), 18089–18091 (TTS native)
    public int NumCtx { get; set; } = 4096;
    public int ReplyCacheSeconds { get; set; } = 300;
}

public sealed record LiveLayout(string Id, string Label, int Width, int Height);

public sealed record LivePlatform(string Id, string Name, string DefaultLayout, string StudioUrl, string StudioName, string HelpUrl, string Instructions);

public static class LiveCatalog
{
    public static readonly IReadOnlyList<LiveLayout> Layouts =
    [
        new("landscape", "Ngang 16:9 · 1920 × 1080", 1920, 1080),
        new("portrait", "Dọc 9:16 · 1080 × 1920", 1080, 1920)
    ];

    public static readonly IReadOnlyList<LivePlatform> Platforms =
    [
        new("facebook", "Facebook", "landscape", "https://www.facebook.com/live/producer", "Facebook Live Producer",
            "https://www.facebookblueprint.com/student/page/239235-graphics-in-live-producer",
            "Mở Facebook Live Producer, chọn nơi phát và thiết lập phần mềm phát. Kết nối OBS theo thông tin mà Facebook cung cấp, kiểm tra preview rồi bắt đầu buổi live."),
        new("youtube", "YouTube", "landscape", "https://studio.youtube.com", "YouTube Studio",
            "https://support.google.com/youtube/answer/2907883?hl=vi",
            "Trong YouTube Studio, chọn Tạo → Phát trực tiếp và dùng bộ mã hóa. Kết nối OBS với kênh hoặc nhập thông tin luồng từ YouTube, sau đó kiểm tra preview trong phòng điều khiển."),
        new("tiktok", "TikTok", "portrait", "https://www.tiktok.com/studio/live", "TikTok LIVE",
            "https://inapp.tiktokv.com/falcon/webcast_mt/page/obs_intro/index.html",
            "Dùng phương thức LIVE mà tài khoản TikTok được cấp. Nếu có Server URL và Stream Key, cấu hình chúng trong OBS. Nếu chưa có quyền dùng bộ mã hóa, làm theo hướng dẫn LIVE của TikTok cho tài khoản của bạn.")
    ];

    public static LivePlatform Platform(string id) => Platforms.FirstOrDefault(p => p.Id == id) ?? Platforms[^1];
    public static LiveLayout Layout(string id) => Layouts.FirstOrDefault(l => l.Id == id) ?? Layouts[0];
}
