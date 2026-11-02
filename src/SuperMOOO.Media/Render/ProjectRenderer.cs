using SuperMOOO.Media.Audio;
using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;

namespace SuperMOOO.Media.Render;

/// <param name="Narration">Trả file audio lời bình của clip (null = không có). TTS nằm ở tầng Workflows nên được tiêm vào đây.</param>
/// <param name="CaptionStyle">Kiểu chữ của phụ đề từng clip; null = chữ trắng viền đen mặc định.</param>
public sealed record RenderOptions(Func<int, RenderClip, CancellationToken, Task<string?>>? Narration = null, SubtitleStyle? CaptionStyle = null);

public sealed record RenderResult(IReadOnlyList<string> Files, TimeSpan Duration);

/// <summary>Port render_project của timeline_service: dựng từng clip → nối → trộn nhạc/giọng. Chỉ dùng nội bộ cho Review/Auto (không có editor).</summary>
[Service]
public sealed class ProjectRenderer(FfmpegRunner ffmpeg, IMediaProbe probe, IRuntimeResources? resources = null, EncoderDetector? encoders = null)
{
    private const double MinClipSeconds = 0.08;
    private const int MaxClips = 200;

    /// <summary>Montage: output là file .mp4. Individual: output là thư mục, mỗi clip một file part_001.mp4…</summary>
    public async Task<RenderResult> RenderAsync(RenderProject project, string output, RenderOptions? options,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (project.ExportMode == ExportMode.Individual)
        {
            if (!string.IsNullOrWhiteSpace(project.VoiceoverPath))
                throw new ArgumentException("File lời đọc toàn video chỉ dùng với kiểu xuất montage.");
            Directory.CreateDirectory(output);
            var files = new List<string>();
            var total = TimeSpan.Zero;
            try
            {
                for (var i = 0; i < project.Clips.Count; i++)
                {
                    var part = project with { Clips = [project.Clips[i]], ExportMode = ExportMode.Montage };
                    var scaled = new Progress<ProgressInfo>(p => progress?.Report(new ProgressInfo((i + Math.Max(0, p.Fraction)) / project.Clips.Count, $"Phần {i + 1}/{project.Clips.Count}: {p.Message}")));
                    var one = await RenderOneAsync(part, Path.Combine(output, $"part_{i + 1:000}.mp4"), options, i, scaled, ct);
                    files.AddRange(one.Files);
                    total += one.Duration;
                }
            }
            catch
            {
                foreach (var f in files) try { File.Delete(f); } catch (IOException) { /* bỏ qua */ }
                throw;
            }

            return new RenderResult(files, total);
        }

        return await RenderOneAsync(project, output, options, 0, progress, ct);
    }

    private async Task<RenderResult> RenderOneAsync(RenderProject project, string output, RenderOptions? options, int clipOffset,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var sources = new Dictionary<string, MediaInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in project.Clips.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            sources[path] = await probe.ProbeAsync(path, ct);
        var duration = await ValidateAsync(project, sources, ct);

        var (w, h) = Dimensions(project, sources[project.Clips[0].Path]);
        var work = Path.Combine(Path.GetTempPath(), "aivs-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var parts = new List<string>();
            for (var i = 0; i < project.Clips.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var clip = project.Clips[i];
                var part = Path.Combine(work, $"{i}.mp4");
                var narration = options?.Narration is { } n ? await n(clipOffset + i, clip, ct) : null;
                var scaled = new Progress<ProgressInfo>(p =>
                    progress?.Report(new ProgressInfo(0.85 * (i + Math.Max(0, p.Fraction)) / project.Clips.Count, $"Dựng clip {i + 1}/{project.Clips.Count}")));
                await RenderClipAsync(project, clip, sources[clip.Path], narration, part, work, i, w, h, options?.CaptionStyle, scaled, ct);
                parts.Add(part);
            }

            progress?.Report(new ProgressInfo(0.86, "Ghép các clip"));
            var manifest = Path.Combine(work, "concat.txt");
            await File.WriteAllTextAsync(manifest, string.Join('\n', parts.Select(p => "file '" + p.Replace('\\', '/').Replace("'", "'\\''") + "'")), ct);
            var joined = Path.Combine(work, "joined.mp4");
            await ffmpeg.RunAsync(new FfmpegJob(["-f", "concat", "-safe", "0", "-i", manifest, "-c", "copy"], joined, Label: "Ghép clip"), null, ct);
            var actual = (await probe.ProbeAsync(joined, ct)).Duration;

            var args = new List<string> { "-i", joined };
            var mix = new List<MixInput> { new("0:a", MixRole.Original) };
            var next = 1;
            if (!string.IsNullOrWhiteSpace(project.MusicPath))
            {
                args.AddRange(["-stream_loop", "-1", "-i", project.MusicPath]);
                mix.Add(new MixInput($"{next++}:a", MixRole.Bgm, project.MusicVolume));
            }

            if (!string.IsNullOrWhiteSpace(project.VoiceoverPath))
            {
                args.AddRange(["-i", project.VoiceoverPath]);
                mix.Add(new MixInput($"{next++}:a", MixRole.Voice, project.VoiceoverVolume));
            }

            if (mix.Count > 1)
            {
                var graph = AudioMixer.BuildGraph(mix, actual, smartDucking: false);
                args.AddRange(["-filter_complex", graph.Filter, "-map", "0:v:0", "-map", $"[{graph.OutputLabel}]", "-c:a", "aac", "-b:a", "192k"]);
            }
            else args.AddRange(["-map", "0:v:0", "-map", "0:a:0", "-c:a", "copy"]);

            args.AddRange(["-c:v", "copy", "-t", Fmt.N(actual.TotalSeconds, 3), "-movflags", "+faststart"]);
            await ffmpeg.RunAsync(new FfmpegJob(args, output, actual, Label: "Hoàn thiện âm thanh"), null, ct);

            var info = await probe.ProbeAsync(output, ct);
            if (info.Video is null || info.Audio is null || Math.Abs((info.Duration - duration).TotalSeconds) > Math.Max(0.4, parts.Count / (double)Math.Max(project.Fps, 1) * 2))
            {
                File.Delete(output);
                throw new InvalidOperationException("File xuất không khớp timeline; kết quả chưa được lưu.");
            }

            progress?.Report(new ProgressInfo(1, "Xong"));
            return new RenderResult([output], info.Duration);
        }
        finally
        {
            ReupRenderer.TryDelete(work);
        }
    }

    private async Task RenderClipAsync(RenderProject project, RenderClip clip, MediaInfo source, string? narration, string output,
        string work, int index, int w, int h, SubtitleStyle? captionStyle, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        var speed = clip.Speed;
        var length = (clip.End - clip.Start) / speed;
        var vf = new List<string> { $"setpts=(PTS-STARTPTS)/{Fmt.N(speed)}" };
        vf.AddRange(TransformFilters(clip));
        vf.Add(project.Fit.Equals("Cover", StringComparison.OrdinalIgnoreCase)
            ? $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}"
            : $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black");
        vf.Add("setsar=1");
        vf.Add($"fps={project.Fps}");

        if (project.Captions && !string.IsNullOrWhiteSpace(clip.Caption))
        {
            var doc = new SubtitleDocument([new SubtitleCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(length), clip.Caption)]);
            var style = captionStyle ?? new SubtitleStyle(FontSize: Math.Max(16, h / 27), PositionY: 88, Bold: true, Outline: 3);
            await File.WriteAllTextAsync(Path.Combine(work, $"cap{index}.ass"), AssWriter.Write(doc, style, new AssOptions(w, h, "Output")), new UTF8Encoding(false), ct);
            vf.Add($"ass=filename=cap{index}.ass");
        }

        var af = new List<string>
        {
            $"atrim=duration={Fmt.N(clip.End - clip.Start, 3)}", "asetpts=PTS-STARTPTS", Math.Abs(speed - 1) > 1e-6 ? Fmt.TempoChain(speed) : "anull",
            "volume=" + Fmt.N(clip.Volume, 4), $"apad,atrim=duration={Fmt.N(length, 3)}"
        };
        var fade = Math.Min(project.FadeSeconds, length / 2);
        if (fade > 0)
        {
            vf.AddRange([$"fade=t=in:d={Fmt.N(fade, 3)}", $"fade=t=out:st={Fmt.N(length - fade, 3)}:d={Fmt.N(fade, 3)}"]);
            af.AddRange([$"afade=t=in:d={Fmt.N(fade, 3)}", $"afade=t=out:st={Fmt.N(length - fade, 3)}:d={Fmt.N(fade, 3)}"]);
        }

        var args = new List<string> { "-ss", Fmt.N(clip.Start, 3), "-t", Fmt.N(clip.End - clip.Start, 3), "-i", clip.Path };
        string sourceAudio;
        var next = 1;
        if (source.Audio is not null) sourceAudio = "[0:a:0]";
        else
        {
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
            sourceAudio = "[1:a:0]";
            next = 2;
        }

        var graph = $"[0:v:0]{string.Join(',', vf)}[v];{sourceAudio}aresample=48000,{string.Join(',', af)}[source]";
        var audioLabel = "[source]";
        if (!string.IsNullOrWhiteSpace(narration))
        {
            var spoken = (await probe.ProbeAsync(narration, ct)).Duration.TotalSeconds;
            if (spoken > length + 0.02)
                throw new InvalidOperationException($"Lời bình clip {index + 1} dài {spoken:0.0}s, clip chỉ {length:0.0}s; tăng thời lượng hoặc rút gọn lời bình.");
            args.AddRange(["-i", narration]);
            graph += $";[{next}:a]aresample=48000,apad,atrim=duration={Fmt.N(length, 3)}[nar];[source][nar]amix=inputs=2:duration=longest:normalize=0[a]";
            audioLabel = "[a]";
        }

        var encoder = resources is not null && encoders is not null ? await encoders.SelectAsync(resources.Plan.Encoder, ct) : EncoderProfile.X264;
        args.AddRange(["-filter_complex", graph, "-map", "[v]", "-map", audioLabel, "-t", Fmt.N(length, 3)]);
        args.AddRange(encoder.VideoArgs("veryfast", null));
        args.AddRange(["-c:a", "aac", "-ar", "48000", "-ac", "2"]);
        await ffmpeg.RunAsync(new FfmpegJob(args, output, TimeSpan.FromSeconds(length), work, "Dựng clip"), progress, ct);
    }

    /// <summary>Xoay theo chiều kim đồng hồ rồi mới lật theo trục của ảnh hiển thị.</summary>
    public static IReadOnlyList<string> TransformFilters(RenderClip clip)
    {
        var list = clip.Rotation switch
        {
            0 => new List<string>(), 90 => ["transpose=1"], 180 => ["hflip", "vflip"], 270 => ["transpose=2"],
            _ => throw new ArgumentException("Góc xoay chỉ nhận 0, 90, 180, 270.")
        };
        if (clip.FlipH) list.Add("hflip");
        if (clip.FlipV) list.Add("vflip");
        return list;
    }

    public static (int Width, int Height) Dimensions(RenderProject project, MediaInfo first)
    {
        var aspect = AspectRatio.Parse(project.Aspect);
        var (sw, sh) = first.DisplaySize();
        if (project.Clips[0].Rotation is 90 or 270) (sw, sh) = (sh, sw);
        var ratio = aspect.IsOriginal ? (double)sw / Math.Max(sh, 1) : aspect.Value;
        var side = Math.Clamp(project.Height, 144, 2160);
        var (w, h) = ratio >= 1 ? (Fmt.Even(side * ratio), Fmt.Even(side)) : (Fmt.Even(side), Fmt.Even(side / ratio));
        if (w > 4096 || h > 4096) throw new ArgumentException("Tỉ lệ nguồn quá lớn; chọn 16:9, 9:16 hoặc 1:1.");
        return (w, h);
    }

    private async Task<TimeSpan> ValidateAsync(RenderProject project, IReadOnlyDictionary<string, MediaInfo> sources, CancellationToken ct)
    {
        if (project.Clips.Count == 0) throw new ArgumentException("Cần ít nhất một clip.");
        if (project.Clips.Count > MaxClips) throw new ArgumentException($"Tối đa {MaxClips} clip.");
        for (var i = 0; i < project.Clips.Count; i++)
        {
            var c = project.Clips[i];
            var info = sources[c.Path];
            if (c.Speed is < 0.25 or > 4) throw new ArgumentException($"Clip {i + 1}: tốc độ phải từ 0.25 đến 4.");
            if (c.Start < 0 || c.End - c.Start < MinClipSeconds)
                throw new ArgumentException($"Clip {i + 1}: điểm ra phải sau điểm vào ít nhất {MinClipSeconds} giây.");
            if (info.Video is null || c.End > info.Duration.TotalSeconds + 0.02)
                throw new ArgumentException($"Clip {i + 1}: nguồn không có video hoặc mốc cắt vượt thời lượng nguồn.");
        }

        var total = TimeSpan.FromSeconds(project.Clips.Sum(c => (c.End - c.Start) / c.Speed));
        if (total > TimeSpan.FromHours(4)) throw new ArgumentException("Timeline tối đa 4 giờ.");
        foreach (var path in new[] { project.MusicPath, project.VoiceoverPath }.Where(p => !string.IsNullOrWhiteSpace(p)))
            if ((await probe.ProbeAsync(path!, ct)).Audio is null) throw new ArgumentException("Nhạc nền/lời đọc phải là file có âm thanh.");
        if (!string.IsNullOrWhiteSpace(project.VoiceoverPath) && (await probe.ProbeAsync(project.VoiceoverPath, ct)).Duration > total + TimeSpan.FromSeconds(0.02))
            throw new ArgumentException("File lời đọc dài hơn timeline; thêm cảnh hoặc rút ngắn audio trước khi xuất để tránh mất lời.");
        return total;
    }
}

/// <summary>Chia nguồn thành các phần bằng nhau (port segment_source).</summary>
[Service]
public sealed class SegmentSourceService(IMediaProbe probe)
{
    public async Task<IReadOnlyList<RenderClip>> SplitAsync(string path, double seconds, CancellationToken ct) =>
        Split(await probe.ProbeAsync(path, ct), seconds);

    public IReadOnlyList<RenderClip> Split(MediaInfo info, double seconds = 60)
    {
        if (!double.IsFinite(seconds) || seconds is < 5 or > 600) throw new ArgumentException("Độ dài mỗi phần từ 5 đến 600 giây.");
        var total = info.Duration.TotalSeconds;
        if (info.Video is null || total <= 0.08) throw new ArgumentException("Nguồn không có video hợp lệ.");
        if (Math.Ceiling(total / seconds) > 200) throw new ArgumentException("Tối đa 200 phần; tăng độ dài mỗi phần.");

        var clips = new List<RenderClip>();
        var start = 0.0;
        while (start < total - 0.08)
        {
            var end = Math.Min(total, start + seconds);
            if (total - end is > 0 and < 0.08) end = total;
            clips.Add(new RenderClip(info.Path, start, end));
            start = end;
        }

        return clips;
    }
}
