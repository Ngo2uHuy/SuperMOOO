using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Media.Render;

/// <summary>Nối intro + phần chính + outro bằng concat filter: chuẩn hoá độ phân giải, fps, sample rate; thiếu audio thì chèn im lặng (F12).</summary>
[Service]
public sealed class IntroOutroConcat(FfmpegRunner ffmpeg, IMediaProbe probe)
{
    public async Task ConcatAsync(IReadOnlyList<string> files, string output, int width, int height, int fps,
        OutputOptions encode, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (files.Count < 2) throw new ArgumentException("Cần ít nhất hai file để nối.", nameof(files));
        var args = new List<string>();
        var graph = new List<string>();
        var total = TimeSpan.Zero;
        for (var i = 0; i < files.Count; i++)
        {
            var info = await probe.ProbeAsync(files[i], ct);
            if (info.Video is null || info.Duration <= TimeSpan.Zero)
                throw new MediaProbeException($"{Path.GetFileName(files[i])} phải có video và thời lượng hợp lệ.");
            total += info.Duration;
            args.AddRange(["-i", files[i]]);
            graph.Add($"[{i}:v]{VideoNorm(width, height, fps)}[v{i}]");
            graph.Add(AudioNorm(info.Audio is not null ? $"[{i}:a]" : null, info.Duration, $"a{i}"));
        }

        graph.Add($"{string.Concat(Enumerable.Range(0, files.Count).Select(i => $"[v{i}][a{i}]"))}concat=n={files.Count}:v=1:a=1[vo][ao]");
        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[vo]", "-map", "[ao]"]);
        args.AddRange(EncoderProfile.For(encode.Encoder).VideoArgs(encode.Preset, encode.BitrateKbps));
        args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart"]);
        if (encode.StripMetadata) args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", "-metadata:s:v:0", "encoder="]);
        await ffmpeg.RunAsync(new FfmpegJob(args, output, total, Label: "Nối intro/outro"), progress, ct);
    }

    /// <summary>
    /// Nối intro/outro ngay trong filter graph của phần thân để cả video chỉ encode một lần (thay vì encode thân rồi encode lại toàn bộ).
    /// Thêm input vào <paramref name="args"/> và nhánh vào <paramref name="graph"/>; thân được cắt đúng <paramref name="bodyDuration"/>.
    /// </summary>
    public async Task<(string Video, string Audio, TimeSpan Duration)> AppendAsync(List<string> args, List<string> graph, string bodyVideo,
        string? bodyAudio, TimeSpan bodyDuration, string? intro, string? outro, int width, int height, int fps, CancellationToken ct)
    {
        var input = args.Count(a => a == "-i");
        var total = TimeSpan.Zero;
        var labels = new List<string>();

        async Task ExtraAsync(string? file)
        {
            if (string.IsNullOrWhiteSpace(file)) return;
            var info = await probe.ProbeAsync(file, ct);
            if (info.Video is null || info.Duration <= TimeSpan.Zero)
                throw new MediaProbeException($"{Path.GetFileName(file)} phải có video và thời lượng hợp lệ.");
            args.AddRange(["-i", file]);
            var tag = $"io{labels.Count}";
            graph.Add($"[{input}:v]{VideoNorm(width, height, fps)}[{tag}v]");
            graph.Add(AudioNorm(info.Audio is not null ? $"[{input}:a]" : null, info.Duration, tag + "a"));
            labels.Add($"[{tag}v][{tag}a]");
            total += info.Duration;
            input++;
        }

        await ExtraAsync(intro);
        var body = $"io{labels.Count}";
        graph.Add($"[{bodyVideo}]trim=duration={Fmt.N(bodyDuration.TotalSeconds, 3)},{VideoNorm(width, height, fps)}[{body}v]");
        graph.Add(AudioNorm(bodyAudio is null ? null : $"[{bodyAudio}]", bodyDuration, body + "a"));
        labels.Add($"[{body}v][{body}a]");
        total += bodyDuration;
        await ExtraAsync(outro);

        graph.Add($"{string.Concat(labels)}concat=n={labels.Count}:v=1:a=1[iovo][ioao]");
        return ("iovo", "ioao", total);
    }

    private static string VideoNorm(int width, int height, int fps) =>
        $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},settb=AVTB,setpts=PTS-STARTPTS";

    /// <summary>48 kHz stereo đúng thời lượng đoạn; đoạn không có audio thì chèn im lặng.</summary>
    private static string AudioNorm(string? source, TimeSpan duration, string label)
    {
        var d = Fmt.N(duration.TotalSeconds, 3);
        return source is not null
            ? $"{source}aresample=48000,aformat=channel_layouts=stereo,apad,atrim=duration={d},asetpts=PTS-STARTPTS[{label}]"
            : $"anullsrc=r=48000:cl=stereo,atrim=duration={d},asetpts=PTS-STARTPTS[{label}]";
    }
}
