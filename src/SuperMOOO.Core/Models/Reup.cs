namespace SuperMOOO.Core.Models;

public enum ExportMode { Montage, Individual }

public sealed record CanvasOptions(string Aspect = "Original", string Fit = "Contain", bool BlurBackground = false);
public sealed record VideoEffects(bool FlipH = false, double ZoomPercent = 100, double Speed = 1, double Brightness = 0,
    double Contrast = 1, double Saturation = 1, TimeSpan? TrimStart = null, TimeSpan? TrimEnd = null);
public sealed record LogoOptions(string Path, string Position = "TR", double Opacity = 1, double MaxWidthPercent = 15, double? X = null, double? Y = null);
public sealed record SubtitleBurn(SubtitleDocument Document, SubtitleStyle Style, string TimeBase = "Source");
/// <summary>Model tách giọng gốc: roformer (Mel-Band RoFormer, PyTorch, chất lượng cao) hoặc mdx (UVR MDX-Net Voc_FT, ONNX, nhanh).</summary>
public static class SeparationQuality
{
    public const string Roformer = "roformer", Mdx = "mdx", Default = Roformer;
}

/// <param name="BackgroundPath">Nền đã tách giọng (bước separate_vocals gắn lúc xuất) thay cho tiếng gốc; null = tiếng gốc đầy đủ.</param>
public sealed record AudioMixSettings(bool MuteOriginal = false, double OriginalVolume = 1,
    string? VoiceoverPath = null, TimeSpan VoiceoverOffset = default, double VoiceoverVolume = 1,
    string? BgmPath = null, double BgmVolume = 0.12, bool BgmLoop = true, bool SmartDucking = true,
    string Denoise = "Off", string? VoiceEffect = null, double PitchSemitones = 0,
    bool SeparateVocals = false, string SeparationQuality = SuperMOOO.Core.Models.SeparationQuality.Default, string? BackgroundPath = null);
public sealed record IntroOutro(string? IntroPath, string? OutroPath);
public sealed record OutputOptions(int Height = 1080, int Fps = 30, string Encoder = "x264", string Preset = "medium",
    int? BitrateKbps = null, bool StripMetadata = true, string WhenVoiceLonger = "FreezeLastFrame", string FileNameTemplate = "{name}_{preset}");

public sealed record ReupEffectProfile(string PresetId, CanvasOptions Canvas, VideoEffects Video, LogoOptions? Logo,
    SubtitleBurn? Subtitles, AudioMixSettings Audio, IntroOutro? IntroOutro, OutputOptions Output);

public sealed record RenderProject(string Name, IReadOnlyList<RenderClip> Clips, string Aspect, string Fit, int Height, int Fps,
    double FadeSeconds, bool Captions, string? VoiceId, string? MusicPath, double MusicVolume,
    string? VoiceoverPath, double VoiceoverVolume, ExportMode ExportMode);        // Montage | Individual (port timeline_service.Project)
public sealed record RenderClip(string Path, double Start, double End, double Speed = 1, double Volume = 1,
    string Caption = "", string Narration = "", int Rotation = 0, bool FlipH = false, bool FlipV = false);
