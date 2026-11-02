using SuperMOOO.Media.Audio;
using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;

namespace SuperMOOO.Media.Talkshow;

/// <param name="Stage">Ảnh sân khấu có màn hình xanh (#00FF00) để lộ phông phía sau.</param>
/// <param name="Scenery">Ảnh (lia chậm) hoặc video (lặp) hiện sau màn hình.</param>
public sealed record StageOptions(string DialogueAudio, string Stage, string Scenery, SubtitleDocument? Captions = null,
    string? Music = null, double MusicVolume = 0.06, int Height = 720, bool ScreenKey = true);

/// <summary>Dựng sân khấu talkshow: phông + sân khấu key xanh + hội thoại (+ nhạc nền, phụ đề). Tạo giọng TTS nằm ở tầng Workflows.</summary>
[Service]
public sealed class StageCompositor(FfmpegRunner ffmpeg, IMediaProbe probe, IRuntimeResources? resources = null, EncoderDetector? encoders = null)
{
    private static readonly HashSet<string> ImageExt = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    private const int Fps = 25;

    public async Task RenderAsync(StageOptions o, string output, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var dialogue = await AudioInput.FromFileAsync(o.DialogueAudio, probe, ct);
        Require(o.Stage, o.Scenery);
        if (!string.IsNullOrWhiteSpace(o.Music) && !File.Exists(o.Music)) throw new FileNotFoundException("Không tìm thấy nhạc nền.", o.Music);
        var protectedFiles = new[] { o.Stage, o.Scenery, o.DialogueAudio, o.Music }.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.GetFullPath(f!));
        if (protectedFiles.Contains(Path.GetFullPath(output), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("File xuất không được ghi đè nguồn media.");

        var (w, h) = Size(o.Height);
        var args = new List<string>();
        var graph = Visual(o, w, h, args);
        args.AddRange(["-i", dialogue.Path]);
        var audioIdx = 2;
        var useMusic = !string.IsNullOrWhiteSpace(o.Music);
        if (useMusic) args.AddRange(["-stream_loop", "-1", "-i", o.Music!]);

        var work = Path.Combine(Path.GetTempPath(), "aivs-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var video = "[composite]";
            if (o.Captions is { Cues.Count: > 0 } doc)
            {
                var style = new SubtitleStyle(FontSize: (int)Math.Round(h * 0.045), PositionY: 94, Bold: true, Outline: 3);
                await AssWriter.WriteFileAsync(Path.Combine(work, "caps.ass"), doc, style, new AssOptions(w, h, "Output"), ct);
                graph.Add("[composite]ass=filename=caps.ass[captioned]");
                video = "[captioned]";
            }

            graph.Add($"{video}format=yuv420p[video]");
            var mix = useMusic
                ? $"[{audioIdx + 0}:a]aresample=48000[dlg];[{audioIdx + 1}:a]aresample=48000,volume={Fmt.N(o.MusicVolume, 3)},apad,atrim=duration={Fmt.N(dialogue.Duration.TotalSeconds, 3)}[bgm];[dlg][bgm]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95[audio]"
                : $"[{audioIdx}:a]aresample=48000[audio]";
            graph.Add(mix);

            var encoder = resources is not null && encoders is not null ? await encoders.SelectAsync(resources.Plan.Encoder, ct) : EncoderProfile.X264;
            args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[video]", "-map", "[audio]", "-t", Fmt.N(dialogue.Duration.TotalSeconds, 3)]);
            args.AddRange(encoder.VideoArgs("veryfast", null, 23));
            args.AddRange(["-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart"]);
            await ffmpeg.RunAsync(new FfmpegJob(args, output, dialogue.Duration, work, "Dựng sân khấu"), progress, ct);
        }
        finally
        {
            ReupRenderer.TryDelete(work);
        }
    }

    /// <summary>Một khung hình sân khấu (không âm thanh) để xem trước bố cục.</summary>
    public async Task PreviewFrameAsync(string stage, string scenery, bool screenKey, int height, string outputImage, CancellationToken ct)
    {
        Require(stage, scenery);
        var (w, h) = Size(height);
        var args = new List<string>();
        var graph = Visual(new StageOptions("", stage, scenery, ScreenKey: screenKey, Height: height), w, h, args);
        graph.Add("[composite]format=yuv420p[video]");
        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[video]", "-frames:v", "1", "-update", "1"]);
        await ffmpeg.RunAsync(new FfmpegJob(args, outputImage, Label: "Khung sân khấu", Light: true), null, ct);
    }

    private static List<string> Visual(StageOptions o, int w, int h, List<string> args)
    {
        var scenery = ImageExt.Contains(Path.GetExtension(o.Scenery).ToLowerInvariant());
        if (scenery) args.AddRange(["-loop", "1", "-framerate", Fps.ToString(CultureInfo.InvariantCulture)]);
        else args.AddRange(["-stream_loop", "-1"]);
        args.AddRange(["-i", o.Scenery, "-loop", "1", "-framerate", Fps.ToString(CultureInfo.InvariantCulture), "-i", o.Stage]);

        // Phông ảnh lia nhẹ bằng zoompan (d=1 mỗi khung của ảnh lặp) để không phải sinh video AI.
        var back = scenery
            ? $"[0:v]scale={w * 2}:{h * 2}:force_original_aspect_ratio=increase,crop={w * 2}:{h * 2},zoompan=z='1.05':x='iw/2-iw/zoom/2+sin(on/140)*iw/100':y='ih/2-ih/zoom/2':d=1:s={w}x{h}:fps={Fps},setsar=1[scene]"
            : $"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1,fps={Fps}[scene]";
        return
        [
            back,
            $"[1:v]scale={w}:{h},setsar=1,format=rgba{(o.ScreenKey ? ",chromakey=0x00FF00:0.14:0.03" : "")}[stage]",
            "[scene][stage]overlay=0:0:shortest=1[composite]"
        ];
    }

    private static (int Width, int Height) Size(int height)
    {
        var h = Fmt.Even(Math.Clamp(height, 360, 1440));
        return (Fmt.Even(h * 16.0 / 9), h);
    }

    private static void Require(string stage, string scenery)
    {
        foreach (var f in new[] { stage, scenery })
            if (!File.Exists(f)) throw new FileNotFoundException("Chưa có hình/video sân khấu.", f);
    }
}
