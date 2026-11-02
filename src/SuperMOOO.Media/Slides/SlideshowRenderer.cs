using SuperMOOO.Media.Audio;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Render;

namespace SuperMOOO.Media.Slides;

/// <param name="Slide">Ảnh (Ken Burns) hoặc video nền (lặp cho đủ thời lượng giọng).</param>
public sealed record SlideshowOptions(string Slide, string Voiceover, int Height = 1080, bool KenBurns = true, bool Fade = true,
    bool Waveform = true, string WaveformColor = "#00FFCC", string? Intro = null, string? Outro = null, int Fps = 30);

/// <summary>Một slide + giọng đọc → video YouTube: Ken Burns bằng zoompan, sóng âm bằng showwaves, fade, intro/outro.</summary>
[Service]
public sealed class SlideshowRenderer(FfmpegRunner ffmpeg, IMediaProbe probe, IntroOutroConcat concat,
    IRuntimeResources? resources = null, EncoderDetector? encoders = null)
{
    private static readonly HashSet<string> VideoExt = [".mp4", ".mov", ".mkv", ".webm"];

    public async Task RenderAsync(SlideshowOptions o, string output, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var voice = await AudioInput.FromFileAsync(o.Voiceover, probe, ct);
        if (!File.Exists(o.Slide)) throw new FileNotFoundException("Không tìm thấy slide.", o.Slide);

        var h = Math.Clamp(o.Height, 360, 2160);
        var w = Fmt.Even(h * 16.0 / 9);
        var fps = Math.Clamp(o.Fps, 15, 60);
        var seconds = voice.Duration.TotalSeconds;
        var isVideo = VideoExt.Contains(Path.GetExtension(o.Slide).ToLowerInvariant());
        var frames = Math.Max(1, (int)Math.Ceiling(seconds * fps));

        string bg;
        if (o.KenBurns && !isVideo)
            // Ảnh đơn (không -loop): zoompan sinh đủ `frames` khung; phóng 1.0→1.2 tuyến tính, cắt phủ kín để không méo hình.
            bg = $"[0:v]scale={w * 2}:{h * 2}:force_original_aspect_ratio=increase,crop={w * 2}:{h * 2},"
                 + $"zoompan=z='1+0.2*on/{frames}':d={frames}:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s={w}x{h}:fps={fps},setsar=1";
        else
            bg = $"[0:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps}";
        if (o.Fade && seconds >= 3) bg += $",fade=t=in:st=0:d=1,fade=t=out:st={Fmt.N(Math.Max(0.5, seconds - 1), 2)}:d=1";
        bg += "[bg]";

        var color = "0x" + o.WaveformColor.TrimStart('#');
        var graph = o.Waveform
            ? $"{bg};[1:a]showwaves=s={w}x120:mode=line:colors={color}:scale=cbrt:rate={fps}[wave];[bg][wave]overlay=0:H-140,format=yuv420p[vout]"
            : $"{bg};[bg]format=yuv420p[vout]";

        // Ảnh tĩnh phải lặp để đủ khung hình theo giọng đọc; zoompan tự sinh khung từ ảnh đơn nên Ken Burns không cần lặp.
        string[] slideIn = isVideo ? ["-stream_loop", "-1", "-i", o.Slide] : o.KenBurns ? ["-i", o.Slide] : ["-loop", "1", "-i", o.Slide];
        var hasExtras = !string.IsNullOrWhiteSpace(o.Intro) || !string.IsNullOrWhiteSpace(o.Outro);
        var work = Path.Combine(Path.GetTempPath(), "aivs-slide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var body = hasExtras ? Path.Combine(work, "body.mp4") : output;
            var encoder = resources is not null && encoders is not null ? await encoders.SelectAsync(resources.Plan.Encoder, ct) : EncoderProfile.X264;
            await ffmpeg.RunAsync(new FfmpegJob([.. slideIn, "-i", voice.Path, "-filter_complex", graph, "-map", "[vout]", "-map", "1:a",
                .. encoder.VideoArgs("veryfast", null, 23), "-c:a", "aac", "-b:a", "192k",
                "-t", Fmt.N(seconds, 3), "-movflags", "+faststart"], body, voice.Duration, Label: "Dựng video slide"), progress, ct);

            if (hasExtras)
                await concat.ConcatAsync(new[] { o.Intro, body, o.Outro }.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).ToList(),
                    output, w, h, fps, new OutputOptions(Preset: "faster", StripMetadata: false), progress, ct);
        }
        finally
        {
            Effects.ReupRenderer.TryDelete(work);
        }
    }
}
