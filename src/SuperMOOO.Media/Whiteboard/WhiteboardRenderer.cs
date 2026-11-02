using System.Buffers;
using System.Runtime.InteropServices;
using SuperMOOO.Media.Branding;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Media.Whiteboard;

/// <summary>
/// Dựng clip một cảnh vẽ tay: ảnh cảnh → nét vẽ dần → chuyển sang ảnh màu → giữ khung cuối. Chỉ khung có chuyển động được dựng và gửi
/// qua stdin ffmpeg; đoạn đứng yên kéo dài bằng tpad (clone khung cuối). Khung 16:9 = 1920x1080, 9:16 = 1080x1920.
/// </summary>
[Service]
public sealed class WhiteboardRenderer(FfmpegRunner ffmpeg, EncoderDetector encoders, IMediaProbe probe)
{
    private const double HandHeightRatio = 0.5;
    private readonly object _handLock = new();
    private (string Path, int Short, DateTime Modified, HandSprite Sprite)? _handCache;

    public static string GoldPenHandPath => Path.Combine(AppContext.BaseDirectory, "Assets", "whiteboard", "hand_gold_pen.png");

    public static (int Width, int Height) FrameSize(string aspect) => aspect switch
    {
        "16:9" => (1920, 1080),
        "9:16" => (1080, 1920),
        _ => throw new ArgumentException($"Tỉ lệ khung không hỗ trợ: {aspect}", nameof(aspect))
    };

    public async Task RenderSceneAsync(WhiteboardSceneRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (request.AudioDurationSec <= 0) throw new ArgumentException("Thời lượng lời đọc phải dương.", nameof(request));
        if (request.Fps is < 1 or > 120) throw new ArgumentException("FPS không hợp lệ.", nameof(request));
        var (width, height) = FrameSize(request.Aspect);
        var options = request.Options;
        var fps = request.Fps;
        var total = request.AudioDurationSec + Math.Max(0, options.TailHoldSec);

        byte[]? scene = await RawImageDecoder.FitOnWhiteAsync(ffmpeg, request.ImagePath, width, height, ct);
        try
        {
        var hand = await LoadHandAsync(options, Math.Min(width, height), ct);
        var encoder = await encoders.SelectAsync(options.Encoder, ct);
        var inv = CultureInfo.InvariantCulture;
        var seconds = total.ToString("0.###", inv);
        var filters = $"scale=out_color_matrix=bt709:out_range=tv,format={encoder.PixFmt},tpad=stop_mode=clone:stop_duration={seconds}";
        if (request.ExtraVideoFilters is { Count: > 0 } extra) filters += "," + string.Join(',', extra);

        var args = new List<string>
        {
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-framerate", fps.ToString(inv), "-i", "pipe:0"
        };
        if (request.AudioPath is not null) args.AddRange(["-i", request.AudioPath]);
        BrandingFilters.AddVideoGraph(args, filters, request.Logo, request.AudioPath is null ? 1 : 2, width, height);
        if (request.AudioPath is not null)
        {
            if (request.Logo is null) args.AddRange(["-map", "0:v"]);
            args.AddRange(["-map", "1:a", "-af", "apad"]);
        }
        args.AddRange(["-t", seconds]);
        var (preset, crf) = options.Quality switch { "fast" => ("veryfast", 23), "high" => ("medium", 18), _ => ("faster", 20) };
        args.AddRange(encoder.VideoArgs(preset, null, crf));
        args.AddRange(["-g", (fps * 2).ToString(inv), "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"]);
        if (request.AudioPath is not null) args.AddRange(["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2"]);
        else args.Add("-an");
        args.AddRange(["-movflags", "+faststart"]);

        // Composer dựng bên trong writer để phần nặng CPU (nét vẽ, thứ tự ô) nằm sau lease media của FfmpegRunner.
        Task Writer(Stream stdin, CancellationToken token) => Task.Run(async () =>
        {
            using var composer = new FrameComposer(scene!, width, height, fps, request.AudioDurationSec, options, hand);
            ArrayPool<byte>.Shared.Return(scene!);
            scene = null;
            var motion = composer.MotionFrames;
            for (var f = 0; f < motion; f++)
                await stdin.WriteAsync(composer.Compose(f, fps), token);
            if (motion < composer.TotalFrames) await stdin.WriteAsync(composer.Final(), token);
        }, token);

            await ffmpeg.RunAsync(new FfmpegJob(args, request.OutputPath, TimeSpan.FromSeconds(total), request.WorkingDirectory,
                "Dựng cảnh vẽ tay", StdinWriter: Writer), progress, ct);
        }
        finally
        {
            if (scene is not null) ArrayPool<byte>.Shared.Return(scene);
        }
    }

    private async Task<HandSprite?> LoadHandAsync(WhiteboardRenderOptions options, int shortSide, CancellationToken ct)
    {
        var path = options.Hand switch
        {
            WhiteboardHandStyle.GoldPen => GoldPenHandPath,
            WhiteboardHandStyle.Custom => options.CustomHandPath,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        var modified = File.GetLastWriteTimeUtc(path);
        lock (_handLock)
            if (_handCache is { } c && c.Path == path && c.Short == shortSide && c.Modified == modified) return c.Sprite;

        try
        {
            var info = await probe.ProbeAsync(path, ct);
            if (info.Video is not { Width: > 0, Height: > 0 } video) return null;
            var s = HandHeightRatio * shortSide / video.Height;
            var w = Math.Max(1, (int)Math.Round(video.Width * s, MidpointRounding.AwayFromZero));
            var h = Math.Max(1, (int)Math.Round(video.Height * s, MidpointRounding.AwayFromZero));
            double tipX = 0, tipY = 0;
            var sidecar = Path.ChangeExtension(path, ".json");
            if (File.Exists(sidecar))
            {
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(sidecar, ct));
                    if (doc.RootElement.TryGetProperty("tipX", out var tx) && tx.TryGetDouble(out var x)
                        && doc.RootElement.TryGetProperty("tipY", out var ty) && ty.TryGetDouble(out var y))
                    {
                        tipX = x * s;
                        tipY = y * s;
                    }
                }
                catch (JsonException) { /* sidecar tuỳ chọn */ }
            }

            var raw = await RawImageDecoder.ScaleKeepAlphaAsync(ffmpeg, path, w, h, ct);
            try
            {
                var pixels = raw.AsSpan(0, w * h * 4).ToArray();
                Premultiply(MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()));
                var sprite = new HandSprite(pixels, w, h, tipX, tipY);
                lock (_handLock) _handCache = (path, shortSide, modified, sprite);
                return sprite;
            }
            finally { ArrayPool<byte>.Shared.Return(raw); }
        }
        catch (Exception ex) when (ex is MediaProbeException or FfmpegException or InvalidDataException)
        {
            return null;    // thiếu ảnh tay thì vẽ không tay, như bản gốc
        }
    }

    private static void Premultiply(Span<uint> px)
    {
        for (var i = 0; i < px.Length; i++)
        {
            var p = px[i];
            var a = p >> 24;
            if (a == 255) continue;
            if (a == 0) { px[i] = 0; continue; }
            var b = ((p & 0xFF) * a + 127) / 255;
            var g = (((p >> 8) & 0xFF) * a + 127) / 255;
            var r = (((p >> 16) & 0xFF) * a + 127) / 255;
            px[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }
    }
}
