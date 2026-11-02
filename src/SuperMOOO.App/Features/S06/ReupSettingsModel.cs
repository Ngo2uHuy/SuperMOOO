using System.Globalization;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Subtitles;

namespace SuperMOOO.App.Features.S06;

/// <summary>
/// Dạng sửa được của <see cref="ReupEffectProfile"/> cho màn S06: Load nhận toàn bộ hồ sơ (preset thay hết, F11),
/// Build trả hồ sơ mới để xem trước/xuất (bản sao, sửa tiếp không đổi job đã xếp, F22).
/// </summary>
public sealed partial class ReupSettingsModel : ObservableObject
{
    [ObservableProperty] public partial string PresetId { get; set; } = "tiktok";

    // Canvas
    [ObservableProperty] public partial string Aspect { get; set; } = "Original";
    [ObservableProperty] public partial string Fit { get; set; } = "Contain";
    [ObservableProperty] public partial bool BlurBackground { get; set; }

    // Video
    [ObservableProperty] public partial bool FlipH { get; set; }
    [ObservableProperty] public partial double Zoom { get; set; } = 100;
    [ObservableProperty] public partial double Speed { get; set; } = 1;
    [ObservableProperty] public partial double Brightness { get; set; }
    [ObservableProperty] public partial double Contrast { get; set; } = 1;
    [ObservableProperty] public partial double Saturation { get; set; } = 1;
    [ObservableProperty] public partial string TrimStart { get; set; } = "";
    [ObservableProperty] public partial string TrimEnd { get; set; } = "";

    // Logo
    [ObservableProperty] public partial string LogoPath { get; set; } = "";
    [ObservableProperty] public partial string LogoPosition { get; set; } = "TR";
    [ObservableProperty] public partial double LogoOpacity { get; set; } = 1;
    [ObservableProperty] public partial double LogoMaxWidth { get; set; } = 15;

    // Âm thanh
    [ObservableProperty] public partial bool MuteOriginal { get; set; }
    [ObservableProperty] public partial double OriginalVolume { get; set; } = 1;
    [ObservableProperty] public partial string VoiceoverPath { get; set; } = "";
    [ObservableProperty] public partial double VoiceoverOffset { get; set; }
    [ObservableProperty] public partial double VoiceoverVolume { get; set; } = 1;
    [ObservableProperty] public partial string BgmPath { get; set; } = "";
    [ObservableProperty] public partial double BgmVolume { get; set; } = 0.12;
    [ObservableProperty] public partial bool BgmLoop { get; set; } = true;
    [ObservableProperty] public partial bool SmartDucking { get; set; } = true;
    [ObservableProperty] public partial bool SeparateVocals { get; set; }
    [ObservableProperty] public partial string SeparationQuality { get; set; } = Core.Models.SeparationQuality.Default;
    [ObservableProperty] public partial string Denoise { get; set; } = "Off";
    [ObservableProperty] public partial string VoiceEffect { get; set; } = "None";
    [ObservableProperty] public partial double Pitch { get; set; }

    // Phụ đề
    [ObservableProperty] public partial bool BurnSubtitles { get; set; } = true;
    [ObservableProperty] public partial string SubtitleTimeBase { get; set; } = "Source";
    [ObservableProperty] public partial string FontName { get; set; } = "Arial";
    [ObservableProperty] public partial int FontSize { get; set; } = 48;
    [ObservableProperty] public partial string TextColor { get; set; } = "#FFFFFF";
    [ObservableProperty] public partial string OutlineColor { get; set; } = "#000000";
    [ObservableProperty] public partial int Outline { get; set; } = 2;
    [ObservableProperty] public partial double PositionY { get; set; } = 82;
    [ObservableProperty] public partial bool BoxBackground { get; set; }

    // Intro/Outro
    [ObservableProperty] public partial string IntroPath { get; set; } = "";
    [ObservableProperty] public partial string OutroPath { get; set; } = "";

    // Đầu ra
    [ObservableProperty] public partial int Height { get; set; } = 1080;
    [ObservableProperty] public partial int Fps { get; set; } = 30;
    [ObservableProperty] public partial string Encoder { get; set; } = "x264";
    [ObservableProperty] public partial string EncodePreset { get; set; } = "medium";
    [ObservableProperty] public partial string Bitrate { get; set; } = "";
    [ObservableProperty] public partial bool StripMetadata { get; set; } = true;
    [ObservableProperty] public partial string WhenVoiceLonger { get; set; } = "FreezeLastFrame";
    [ObservableProperty] public partial string FileNameTemplate { get; set; } = "{name}_{preset}";

    public void Load(ReupEffectProfile p)
    {
        PresetId = p.PresetId;
        (Aspect, Fit, BlurBackground) = (p.Canvas.Aspect, p.Canvas.Fit, p.Canvas.BlurBackground);
        (FlipH, Zoom, Speed, Brightness, Contrast, Saturation) = (p.Video.FlipH, p.Video.ZoomPercent, p.Video.Speed, p.Video.Brightness, p.Video.Contrast, p.Video.Saturation);
        TrimStart = p.Video.TrimStart is { } s ? SubtitleEditing.FormatTime(s.TotalSeconds) : "";
        TrimEnd = p.Video.TrimEnd is { } e ? SubtitleEditing.FormatTime(e.TotalSeconds) : "";
        (LogoPath, LogoPosition, LogoOpacity, LogoMaxWidth) = p.Logo is { } l ? (l.Path, l.Position, l.Opacity, l.MaxWidthPercent) : ("", "TR", 1, 15);

        var a = p.Audio;
        (MuteOriginal, OriginalVolume, VoiceoverPath, VoiceoverOffset, VoiceoverVolume) = (a.MuteOriginal, a.OriginalVolume, a.VoiceoverPath ?? "", a.VoiceoverOffset.TotalSeconds, a.VoiceoverVolume);
        (BgmPath, BgmVolume, BgmLoop, SmartDucking, Denoise, VoiceEffect, Pitch) = (a.BgmPath ?? "", a.BgmVolume, a.BgmLoop, a.SmartDucking, a.Denoise, a.VoiceEffect ?? "None", a.PitchSemitones);
        (SeparateVocals, SeparationQuality) = (a.SeparateVocals, a.SeparationQuality);

        // Hồ sơ không có phụ đề (preset) không được tắt hardsub: người dùng có thể đang sửa cue ở danh sách.
        if (p.Subtitles is { } sub)
        {
            BurnSubtitles = true;
            SubtitleTimeBase = sub.TimeBase;
            (FontName, FontSize, TextColor, OutlineColor, Outline) = (sub.Style.FontName, sub.Style.FontSize, sub.Style.PrimaryColor, sub.Style.OutlineColor, sub.Style.Outline);
            (PositionY, BoxBackground) = (sub.Style.PositionY ?? 82, sub.Style.BoxBackground);
        }

        (IntroPath, OutroPath) = (p.IntroOutro?.IntroPath ?? "", p.IntroOutro?.OutroPath ?? "");
        var o = p.Output;
        (Height, Fps, Encoder, EncodePreset, Bitrate) = (o.Height, o.Fps, o.Encoder, o.Preset, o.BitrateKbps?.ToString(CultureInfo.InvariantCulture) ?? "");
        (StripMetadata, WhenVoiceLonger, FileNameTemplate) = (o.StripMetadata, o.WhenVoiceLonger, o.FileNameTemplate);
    }

    /// <summary>Hồ sơ từ các ô trên màn; <paramref name="cues"/> là phụ đề đang sửa. Giá trị sai (mốc cắt, bitrate) ném FormatException.</summary>
    public ReupEffectProfile Build(SubtitleDocument cues)
    {
        var subtitles = BurnSubtitles && cues.Cues.Count > 0
            ? new SubtitleBurn(cues, new SubtitleStyle(FontName, FontSize, TextColor, OutlineColor, Outline, PositionY: PositionY, BoxBackground: BoxBackground, Bold: true), SubtitleTimeBase)
            : null;
        int? bitrate = string.IsNullOrWhiteSpace(Bitrate) ? null
            : int.TryParse(Bitrate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps) && kbps > 0 ? kbps
            : throw new FormatException("Bitrate phải là số kbps dương (hoặc để trống).");

        return new ReupEffectProfile(PresetId,
            new CanvasOptions(Aspect, Fit, BlurBackground),
            new VideoEffects(FlipH, Zoom, Speed, Brightness, Contrast, Saturation, Trim(TrimStart), Trim(TrimEnd)),
            string.IsNullOrWhiteSpace(LogoPath) ? null : new LogoOptions(LogoPath, LogoPosition, LogoOpacity, LogoMaxWidth),
            subtitles,
            new AudioMixSettings(MuteOriginal, OriginalVolume, NullIfEmpty(VoiceoverPath), TimeSpan.FromSeconds(VoiceoverOffset), VoiceoverVolume,
                NullIfEmpty(BgmPath), BgmVolume, BgmLoop, SmartDucking, Denoise, VoiceEffect == "None" ? null : VoiceEffect, Pitch,
                SeparateVocals, SeparationQuality),
            string.IsNullOrWhiteSpace(IntroPath) && string.IsNullOrWhiteSpace(OutroPath) ? null : new IntroOutro(NullIfEmpty(IntroPath), NullIfEmpty(OutroPath)),
            new OutputOptions(Height, Fps, Encoder, EncodePreset, bitrate, StripMetadata, WhenVoiceLonger, FileNameTemplate));
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static TimeSpan? Trim(string text) => string.IsNullOrWhiteSpace(text) ? null : TimeSpan.FromSeconds(SubtitleEditing.ParseTime(text));
}
