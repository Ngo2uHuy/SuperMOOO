using SuperMOOO.Media.Audio;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Render;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Media.Effects;

public sealed record ReupRenderResult(string Path, TimeSpan Duration, int Width, int Height);

/// <summary>Xuất một video theo ReupEffectProfile (preset TikTok/Shorts/YouTube…): đúng một đường filter graph cho cả xuất lẫn xem trước.</summary>
[Service]
public sealed class ReupRenderer(FfmpegRunner ffmpeg, IMediaProbe probe, ReupFilterGraphBuilder builder, IntroOutroConcat concat,
    ILogger<ReupRenderer>? log = null)
{
    public async Task<ReupRenderResult> RenderAsync(string input, ReupEffectProfile profile, string output,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var (graph, _) = await PrepareAsync(input, profile, videoOnly: false, ct);
        var work = NewWorkDir();
        try
        {
            if (graph.AssContent is not null) await File.WriteAllTextAsync(Path.Combine(work, ReupGraph.AssFileName), graph.AssContent, new UTF8Encoding(false), ct);

            var intro = Existing(profile.IntroOutro?.IntroPath, "intro");
            var outro = Existing(profile.IntroOutro?.OutroPath, "outro");
            var args = new List<string>(graph.InputArgs);
            var filters = new List<string> { graph.FilterComplex };
            var final = graph;
            if (intro is not null || outro is not null)
            {
                // Intro/outro nối trong cùng filter graph: một lần encode thay vì encode thân rồi encode lại cả video; phụ đề/hiệu ứng chỉ ở thân.
                var (v, a, total) = await concat.AppendAsync(args, filters, graph.VideoLabel, graph.AudioLabel, graph.OutputDuration, intro, outro,
                    graph.Width, graph.Height, profile.Output.Fps > 0 ? profile.Output.Fps : 30, ct);
                final = graph with { VideoLabel = v, AudioLabel = a, OutputDuration = total };
            }

            args.AddRange(["-filter_complex", string.Join(';', filters)]);
            args.AddRange(ReupFilterGraphBuilder.OutputArgs(profile, final));
            await ffmpeg.RunAsync(new FfmpegJob(args, output, final.OutputDuration, work, "Đang xuất video"), progress, ct);

            var info = await probe.ProbeAsync(output, ct);
            return new ReupRenderResult(output, info.Duration, graph.Width, graph.Height);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>Một khung hình đúng filter graph xuất tại mốc <paramref name="at"/> của video gốc (xem trước khi kéo slider).</summary>
    public async Task RenderFrameAsync(string input, ReupEffectProfile profile, TimeSpan at, string outputImage, CancellationToken ct)
    {
        var info = await probe.ProbeAsync(input, ct);
        var start = TimeSpan.FromSeconds(Math.Clamp(at.TotalSeconds, 0, Math.Max(0, info.Duration.TotalSeconds - 0.2)));
        var framed = profile with
        {
            Video = profile.Video with { TrimStart = start, TrimEnd = null },
            IntroOutro = null
        };
        var (graph, _) = await PrepareAsync(input, framed, videoOnly: true, ct, info);
        var work = NewWorkDir();
        try
        {
            if (graph.AssContent is not null) await File.WriteAllTextAsync(Path.Combine(work, ReupGraph.AssFileName), graph.AssContent, new UTF8Encoding(false), ct);
            var args = new List<string>(graph.InputArgs) { "-filter_complex", graph.FilterComplex, "-map", $"[{graph.VideoLabel}]", "-frames:v", "1", "-an" };
            if (Path.GetExtension(outputImage).Equals(".jpg", StringComparison.OrdinalIgnoreCase)) args.AddRange(["-q:v", "2"]);
            args.AddRange(["-update", "1"]);
            await ffmpeg.RunAsync(new FfmpegJob(args, outputImage, WorkingDirectory: work, Label: "Khung xem trước", Light: true), null, ct);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private async Task<(ReupGraph Graph, MediaInfo Source)> PrepareAsync(string input, ReupEffectProfile profile, bool videoOnly,
        CancellationToken ct, MediaInfo? known = null)
    {
        var src = known ?? await probe.ProbeAsync(input, ct);
        AudioInput? voice = null;
        if (!videoOnly)
        {
            if (!string.IsNullOrWhiteSpace(profile.Audio.VoiceoverPath)) voice = await AudioInput.FromFileAsync(profile.Audio.VoiceoverPath, probe, ct);
            if (!string.IsNullOrWhiteSpace(profile.Audio.BgmPath)) await AudioInput.FromFileAsync(profile.Audio.BgmPath, probe, ct);
        }

        return (builder.Build(new ReupBuildInput(src, profile, voice, videoOnly)), src);
    }

    /// <summary>Intro/outro đã bị xoá/di chuyển thì bỏ qua (xuất phần thân) thay vì làm hỏng cả lượt xuất.</summary>
    private string? Existing(string? path, string kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return path;
        log?.LogWarning("Bỏ qua {Kind} không còn tồn tại: {Path}", kind, path);
        return null;
    }

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aivs-reup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* ffmpeg vừa thoát còn giữ handle; thư mục nằm trong %TEMP% */ }
    }
}
