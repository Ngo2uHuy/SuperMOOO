namespace SuperMOOO.Core.Models;

public enum WhiteboardHandStyle { None, GoldPen, Custom }

/// <summary>DrawSpeed: hệ số tốc độ vẽ (1 = chuẩn). ColorFadeSec: thời gian nét đen chuyển sang ảnh màu. TailHoldSec: giữ khung cuối sau lời đọc.
/// Encoder: "auto" | "x264" | "nvenc" | "qsv" (theo EncoderDetector). Quality: "fast" | "balanced" | "high" (preset + CRF như VideoC).</summary>
public sealed record WhiteboardRenderOptions(double DrawSpeed = 1, double ColorFadeSec = 0.8, double TailHoldSec = 0.4,
    WhiteboardHandStyle Hand = WhiteboardHandStyle.GoldPen, string? CustomHandPath = null, string Encoder = "auto", string Quality = "balanced");

/// <summary>Aspect: "16:9" (1920x1080) hoặc "9:16" (1080x1920). AudioPath null = clip không âm thanh.
/// ExtraVideoFilters: filter ffmpeg nối sau khi kéo dài đoạn đứng yên (vd ass=filename=subs.ass); chừa chỗ cho phụ đề ASS/watermark ở bước sau.
/// WorkingDirectory: nơi ffmpeg chạy để filter tham chiếu file tương đối (xem FilterEscaping.Stage).</summary>
// Logo: ảnh logo overlay trong cùng lượt encode (filter_complex), không thêm lượt encode.
public sealed record WhiteboardSceneRequest(string ImagePath, double AudioDurationSec, string? AudioPath, string Aspect, int Fps,
    string OutputPath, WhiteboardRenderOptions Options, IReadOnlyList<string>? ExtraVideoFilters = null, string? WorkingDirectory = null,
    LogoOptions? Logo = null);

/// <summary>Thumbnail: ảnh cảnh phủ kín khung + chữ in hoa viền đen. ExtraFilters nối sau chữ (watermark chữ); Logo overlay cùng lượt encode.</summary>
public sealed record WhiteboardThumbnailRequest(string ImagePath, string Text, int Width, int Height, string OutputPath,
    IReadOnlyList<string>? ExtraFilters = null, string? WorkingDirectory = null, LogoOptions? Logo = null);

public static class SubtitleModes
{
    public const string Burn = "burn";
    public const string Srt = "srt";
    public const string Both = "both";
    public const string None = "none";
}

/// <summary>Mẫu thương hiệu của S18 (lưu trong settings "branding"). Phụ đề dùng SubtitleStyle (cỡ chữ/viền/toạ độ theo px khung 1080 cạnh ngắn);
/// watermark chữ + logo dùng mã vị trí TL/TR/BL/BR/C, hoặc toạ độ tự do (X/Y = % góc trên trái của hộp, null = dùng mã góc; preset cũ không có X/Y).
/// SubtitleMaxChars: số ký tự mỗi dòng (16–60); SubtitleUpperCase: viết hoa chữ phụ đề (ASS và .srt); WatermarkEnabled tắt thì bỏ cả chữ lẫn logo.
/// Cỡ watermark chữ quy theo cạnh ngắn của khung (1080 = nguyên giá trị).</summary>
public sealed class BrandingPreset
{
    public string Name { get; set; } = "Mặc định";
    public string SubtitleMode { get; set; } = SubtitleModes.Burn;
    public SubtitleStyle Subtitle { get; set; } = new("Segoe UI", 54, "#FFFFFF", "#000000", 6, 40, "bottom", Bold: true, PositionY: 86);
    public int SubtitleMaxChars { get; set; } = 42;
    public bool SubtitleUpperCase { get; set; }
    public bool WatermarkEnabled { get; set; } = true;
    public string WatermarkText { get; set; } = "";
    public string WatermarkFont { get; set; } = "Segoe UI";
    public int WatermarkSize { get; set; } = 40;
    public string WatermarkColor { get; set; } = "#FFFFFF";
    public string WatermarkOutline { get; set; } = "#000000";
    public double WatermarkOpacity { get; set; } = 0.85;
    public string WatermarkPosition { get; set; } = "TL";
    public double? WatermarkX { get; set; }
    public double? WatermarkY { get; set; }
    public string? LogoPath { get; set; }
    public string LogoPosition { get; set; } = "TR";
    public double? LogoX { get; set; }
    public double? LogoY { get; set; }
    public double LogoOpacity { get; set; } = 0.9;
    public double LogoSizePercent { get; set; } = 12;

    public bool BurnSubtitles => SubtitleMode is SubtitleModes.Burn or SubtitleModes.Both;
    public bool WriteSrt => SubtitleMode is SubtitleModes.Srt or SubtitleModes.Both;
}

public sealed class BrandingSettings
{
    public string Active { get; set; } = "Mặc định";
    public List<BrandingPreset> Presets { get; set; } = [new()];
}

/// <summary>Kịch bản một video: lời kể + prompt ảnh (tiếng Anh) từng cảnh, kèm SEO để đăng bài.</summary>
public sealed record WhiteboardScene(string Narration, string ImagePrompt);

public sealed record WhiteboardSeo(string Title, string Description, IReadOnlyList<string> Tags, IReadOnlyList<string> Hashtags);

public sealed record WhiteboardScript(string Title, string ThumbnailText, IReadOnlyList<WhiteboardScene> Scenes, WhiteboardSeo Seo);

/// <summary>Biểu mẫu "Sản xuất" của S18 và thông số dựng, nhớ giữa các lần mở (settings "whiteboard"; tương đương localStorage + RenderSettings của VideoC).</summary>
public sealed class WhiteboardFormSettings
{
    public string Style { get; set; } = "whiteboard";
    public string Tone { get; set; } = "friendly";
    public string CustomTone { get; set; } = "";
    public string Language { get; set; } = "vi";
    public int SceneCount { get; set; } = 6;
    public string Aspect { get; set; } = "16:9";
    public double Speed { get; set; } = 1;
    public int Fps { get; set; } = 30;
    public string Quality { get; set; } = "balanced";
    public string Encoder { get; set; } = "auto";
    public double DrawSpeed { get; set; } = 1;
    public double ColorFadeSec { get; set; } = 0.8;
    public double TailHoldSec { get; set; } = 0.4;
    public WhiteboardHandStyle Hand { get; set; } = WhiteboardHandStyle.GoldPen;
    public string CustomHandPath { get; set; } = "";
    public string OutputFolder { get; set; } = "";
}
