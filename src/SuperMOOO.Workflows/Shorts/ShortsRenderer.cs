using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Subtitles;
using F = SuperMOOO.Media.Ffmpeg.Fmt;

namespace SuperMOOO.Workflows.Shorts;

public sealed record ShortsRenderOptions(int Width, bool BlurFill, bool Captions, string CaptionStyle, int FontSize, bool TitleOverlay);

/// <summary>Cắt từng đoạn, đổi khung dọc 9:16 (nền mờ hoặc lấp đầy cắt giữa), đốt phụ đề ASS và chữ tiêu đề/hook ở đầu trong một lệnh ffmpeg.</summary>
[Service]
public sealed class ShortsRenderer(FfmpegRunner ffmpeg, EncoderDetector encoders, SubtitleStylePresets presets)
{
    private const double TitleSeconds = 3.5;

    public async Task<string> RenderAsync(string source, ShortClip clip, IReadOnlyList<SubtitleEntry> entries, ShortsRenderOptions o,
        string output, string work, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var width = F.Even(o.Width);
        var height = F.Even(width * 16.0 / 9);
        var span = TimeSpan.FromSeconds(clip.End - clip.Start);

        var cues = new List<SubtitleCue>();
        if (o.Captions)
        {
            var style = presets.Get(o.CaptionStyle) with { FontSize = Math.Max(16, o.FontSize), MarginV = Math.Max(120, height / 8) };
            foreach (var e in entries.Where(e => e.End > clip.Start && e.Start < clip.End))
            {
                var text = e.Display.Trim();
                if (text.Length == 0) continue;
                var inside = e.Start >= clip.Start && e.End <= clip.End;
                cues.Add(new SubtitleCue(cues.Count + 1, TimeSpan.FromSeconds(Math.Max(e.Start, clip.Start)), TimeSpan.FromSeconds(Math.Min(e.End, clip.End)),
                    text, inside && string.IsNullOrWhiteSpace(e.Translation) ? e.Words : null, style));
            }
        }

        var overlay = o.TitleOverlay ? (clip.Hook.Length > 0 ? clip.Hook : clip.Title) : "";
        if (overlay.Length > 0)
        {
            var titleStyle = new SubtitleStyle(FontSize: Math.Max(24, o.FontSize), Bold: true, Outline: 3, BoxBackground: true, BackOpacity: 0.55,
                PositionY: 14, BoxLeft: 6, BoxWidth: 88);
            cues.Add(new SubtitleCue(cues.Count + 1, TimeSpan.FromSeconds(clip.Start),
                TimeSpan.FromSeconds(Math.Min(clip.End, clip.Start + TitleSeconds)), overlay, null, titleStyle));
        }

        Directory.CreateDirectory(work);
        var hasAss = cues.Count > 0;
        if (hasAss)
            await AssWriter.WriteFileAsync(Path.Combine(work, "subs.ass"), new SubtitleDocument(cues), presets.Get(o.CaptionStyle),
                new AssOptions(width, height, "Source", TimeSpan.FromSeconds(clip.Start), 1, span), ct);

        var fit = o.BlurFill
            ? $"[0:v]split[a][b];[a]scale={F.Even(width / 4.0)}:{F.Even(height / 4.0)}:force_original_aspect_ratio=increase,crop={F.Even(width / 4.0)}:{F.Even(height / 4.0)},boxblur=10:2,scale={width}:{height}[bg];"
              + $"[b]scale={width}:{height}:force_original_aspect_ratio=decrease[fg];[bg][fg]overlay=(W-w)/2:(H-h)/2,setsar=1"
            : $"[0:v]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1";
        var graph = fit + (hasAss ? ",ass=filename=subs.ass[v]" : "[v]");

        var best = await encoders.BestAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            var profile = attempt == 0 ? best : EncoderProfile.X264;
            List<string> args = ["-ss", F.Sec(TimeSpan.FromSeconds(clip.Start)), "-t", F.Sec(span), "-i", source, "-filter_complex", graph, "-map", "[v]", "-map", "0:a:0?"];
            args.AddRange(profile.VideoArgs("fast", null, 20));
            args.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
            try
            {
                await ffmpeg.RunAsync(new FfmpegJob(args, output, span, work, "Dựng Short"), progress, ct);
                return output;
            }
            catch (FfmpegException) when (attempt == 0 && best != EncoderProfile.X264) { }
        }
    }
}
