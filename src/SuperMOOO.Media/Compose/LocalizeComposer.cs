using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Render;
using SuperMOOO.Media.Subtitles;

namespace SuperMOOO.Media.Compose;

/// <param name="OnlyDuringSubtitles">Chỉ làm mờ trong các khoảng có phụ đề mới.</param>
public sealed record BlurRegion(PercentBox Box, int Strength = 12, int Feather = 0, bool OnlyDuringSubtitles = false);

/// <summary>Video "patch" đã xoá phụ đề cứng (cùng fps nguồn), overlay tại (X, Y) pixel của khung nguồn trước mọi bước khác.</summary>
public sealed record InpaintPatch(string File, int X, int Y);

/// <summary>Ảnh/clip chèn thêm (canvas nền, logo, PiP) đặt trong hộp % của khung; Loop chỉ dùng cho PiP là video.</summary>
public sealed record OverlayAsset(string File, PercentBox? Box = null);

public sealed record LocalizeComposeOptions(
    string AspectRatio = "original", int? OutputResolution = null, bool Mirror = false, string Fit = "contain",
    BlurRegion? Blur = null, OverlayAsset? Canvas = null, OverlayAsset? Logo = null, OverlayAsset? Pip = null,
    SubtitleDocument? Subtitles = null, SubtitleStyle? SubtitleStyle = null, bool CutMeta = false, string? OutroFile = null,
    IReadOnlyList<(double Start, double End)>? BlurSpans = null, InpaintPatch? Patch = null);

/// <summary>Ghép video gốc + track lồng tiếng thành video cuối (port core/video_compositor.compose_video, dùng AssWriter/MaskFilter chung).</summary>
[Service]
public sealed class LocalizeComposer(FfmpegRunner ffmpeg, IMediaProbe probe, IntroOutroConcat concat,
    IRuntimeResources? resources = null, EncoderDetector? encoders = null)
{
    public const double CutMetaSeconds = 1.5;

    public async Task ComposeAsync(string video, string dubbedAudio, string output, LocalizeComposeOptions o,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var info = await probe.ProbeAsync(video, ct);
        if (info.Video is null) throw new MediaProbeException("File nguồn phải có luồng video.");
        var dub = await Audio.AudioInput.FromFileAsync(dubbedAudio, probe, ct);

        var (w, h) = Dimensions(info, o);
        var args = new List<string> { "-i", video, "-i", dubbedAudio };
        var graph = new List<string>();
        var current = "0:v";
        var n = 0;
        string Next() => $"v{++n}";
        void Step(string expr)
        {
            var label = Next();
            graph.Add($"[{current}]{expr}[{label}]");
            current = label;
        }

        if (o.Patch is { } patch)
        {
            // eof_action=pass: patch dừng sau đoạn chữ cuối thì lộ lại khung nguồn.
            args.AddRange(["-i", patch.File]);
            var label = Next();
            graph.Add($"[{current}][2:v]overlay={patch.X}:{patch.Y}:eof_action=pass[{label}]");
            current = label;
        }

        // Đã xoá phụ đề gốc bằng inpaint thì không làm mờ nữa; mốc dò chữ (BlurSpans) ưu tiên hơn mốc phụ đề mới, rỗng = không có chữ để che.
        // Vùng mờ vẽ theo khung nguồn (khung xem trước, vùng tự tìm) nên áp trước lật gương/đổi khổ; độ mờ/mềm viền quy về pixel nguồn
        // để nhìn như nhau ở khung ra.
        if (o.Blur is { } blur && o.Patch is null && o.BlurSpans is not { Count: 0 })
        {
            string? enable = null;
            if (o.BlurSpans is { } spans)
                enable = Between(spans);
            else if (blur.OnlyDuringSubtitles && o.Subtitles is { Cues.Count: > 0 } doc)
                enable = Between(doc.Cues.Select(c => (c.Start.TotalSeconds, c.End.TotalSeconds)));
            var (sw, sh) = info.DisplaySize();
            var cover = o.Fit.Equals("cover", StringComparison.OrdinalIgnoreCase);
            var scale = cover ? Math.Max((double)w / sw, (double)h / sh) : Math.Min((double)w / sw, (double)h / sh);
            var label = Next();
            graph.Add(MaskFilter.Build(current, label, blur.Box.ToPixels(sw, sh), MaskStyle.Blur, $"b{n}",
                Math.Max(1, (int)Math.Round(blur.Strength / scale)), (int)Math.Round(blur.Feather / scale), enable));
            current = label;
        }

        if (o.Mirror) Step("hflip");
        Step(o.Fit.Equals("cover", StringComparison.OrdinalIgnoreCase)
            ? $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1"
            : $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1");

        var input = o.Patch is null ? 2 : 3;
        foreach (var (asset, loopArgs, isPip) in new[] { (o.Canvas, new[] { "-loop", "1" }, false), (o.Logo, ["-loop", "1"], false), (o.Pip, ["-stream_loop", "-1"], true) })
        {
            if (asset is null) continue;
            if (!File.Exists(asset.File)) throw new FileNotFoundException("Không tìm thấy tệp chèn thêm.", asset.File);
            args.AddRange([.. loopArgs, "-i", asset.File]);
            var box = (asset.Box ?? new PercentBox(0, 0, 100, 100)).ToPixels(w, h);
            graph.Add($"[{input}:v]scale={box.W}:{box.H}:force_original_aspect_ratio=decrease,format=rgba[asset{input}]");
            var label = Next();
            graph.Add($"[{current}][asset{input}]overlay={box.X}:{box.Y}:shortest=1[{label}]");
            current = label;
            input++;
            _ = isPip;
        }

        var work = Path.Combine(Path.GetTempPath(), "aivs-compose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            if (o.Subtitles is { Cues.Count: > 0 } subs)
            {
                await AssWriter.WriteFileAsync(Path.Combine(work, "subs.ass"), subs, o.SubtitleStyle ?? new SubtitleStyle(FontSize: Math.Max(16, h / 22), PositionY: 82, Bold: true),
                    new AssOptions(w, h, "Output"), ct);
                Step("ass=filename=subs.ass");
            }

            var cut = o.CutMeta ? CutMetaSeconds : 0;
            if (cut > 0) Step($"trim=start={Fmt.N(cut)},setpts=PTS-STARTPTS");
            graph.Add($"[1:a]atrim=start={Fmt.N(cut)},asetpts=PTS-STARTPTS[aout]");
            var (videoOut, audioOut, duration) = (current, "aout", info.Duration);
            var extras = new List<string>();
            if (!string.IsNullOrWhiteSpace(o.OutroFile))
            {
                // Outro nối trong cùng filter graph: một lần encode; thân cắt như -shortest, phụ đề chỉ nằm ở thân.
                var body = TimeSpan.FromSeconds(Math.Max(0.1, Math.Min(info.Duration.TotalSeconds, dub.Duration.TotalSeconds) - cut));
                (videoOut, audioOut, duration) = await concat.AppendAsync(args, graph, current, "aout", body, null, o.OutroFile, w, h, 30, ct);
                extras.AddRange(["-ar", "48000", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", "-metadata:s:v:0", "encoder="]);
            }

            var encoder = resources is not null && encoders is not null ? await encoders.SelectAsync(resources.Plan.Encoder, ct) : EncoderProfile.X264;
            args.AddRange(["-filter_complex", string.Join(';', graph), "-map", $"[{videoOut}]", "-map", $"[{audioOut}]"]);
            args.AddRange(encoder.VideoArgs("faster", null, 22));
            args.AddRange(["-c:a", "aac", "-b:a", "192k", "-shortest", "-map_metadata", "-1", "-movflags", "+faststart", .. extras]);
            await ffmpeg.RunAsync(new FfmpegJob(args, output, duration, work, "Ghép video lồng tiếng"), progress, ct);
        }
        finally
        {
            ReupRenderer.TryDelete(work);
        }
    }

    private static string Between(IEnumerable<(double Start, double End)> spans) =>
        string.Join('+', spans.Select(s => $"between(t,{Fmt.N(s.Start, 3)},{Fmt.N(s.End, 3)})"));

    /// <summary>Khổ ra: mở rộng canvas theo tỉ lệ (không bao giờ nhỏ hơn nguồn), rồi quy về độ phân giải cạnh ngắn 360/720/1080 nếu có.</summary>
    public static (int Width, int Height) Dimensions(MediaInfo info, LocalizeComposeOptions o)
    {
        var (w, h) = info.DisplaySize();
        var aspect = AspectRatio.Parse(o.AspectRatio);
        if (!aspect.IsOriginal)
        {
            w = Math.Max(w, (int)Math.Round(h * aspect.Value));
            h = (int)Math.Round(w / aspect.Value);
        }

        if (o.OutputResolution is 360 or 720 or 1080)
        {
            var factor = (double)o.OutputResolution.Value / Math.Min(w, h);
            w = (int)Math.Round(w * factor);
            h = (int)Math.Round(h * factor);
        }

        return (Fmt.Even(w), Fmt.Even(h));
    }
}
