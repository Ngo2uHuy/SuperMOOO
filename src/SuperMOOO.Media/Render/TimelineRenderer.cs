using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;

namespace SuperMOOO.Media.Render;

/// <summary>Dùng chung đường render cho preview và export để audio, phụ đề và thời gian nhất quán.</summary>
[Service]
public sealed class TimelineRenderer(ProjectRenderer renderer, FfmpegRunner ffmpeg, IMediaProbe probe, AppPaths paths,
    IRuntimeResources? resources = null, EncoderDetector? encoders = null)
{
    public async Task RenderAsync(TimelineProject project, string output, bool preview,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        TimelineEditing.Validate(project, forRender: true);
        var inputs = project.Clips.Where(c => c.Track != TimelineTrack.Subtitle).ToArray();
        var target = Path.GetFullPath(output);
        if (!Path.GetExtension(target).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Đầu ra phải là file MP4.");
        foreach (var group in inputs.GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFullPath(group.Key).Equals(target, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFullPath(group.Key).Equals(FfmpegRunner.PartialPath(target), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("File xuất không được ghi đè media nguồn.");
            if (!File.Exists(group.Key)) throw new FileNotFoundException("Không tìm thấy media: " + group.Key);
            var info = await probe.ProbeAsync(group.Key, ct);
            foreach (var clip in group)
                if (clip.Out > info.Duration.TotalSeconds + 0.02 || (clip.Track == TimelineTrack.Video ? info.Video is null : info.Audio is null))
                    throw new ArgumentException("Nguồn đã thay đổi hoặc không có luồng media phù hợp: " + group.Key);
        }
        var work = Path.Combine(paths.CacheDir("preview"), "timeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var height = preview ? 360 : project.Height;
            var video = new RenderProject(project.Name, project.Clips.Where(c => c.Track == TimelineTrack.Video)
                .Select(c => new RenderClip(c.Path, c.In, c.Out, Volume: c.Muted ? 0 : c.Volume)).ToArray(),
                project.Aspect, "Contain", height, project.Fps, 0, false, null, null, 0, null, 0, ExportMode.Montage);
            var baseFile = Path.Combine(work, "video.mp4");
            await renderer.RenderAsync(video, baseFile, null,
                new Progress<ProgressInfo>(p => progress?.Report(p with { Fraction = p.Fraction * 0.8 })), ct);
            var duration = TimeSpan.FromSeconds(project.Duration);
            var args = new List<string> { "-i", baseFile };
            var graph = new List<string>();
            var labels = new List<string> { "[0:a:0]" };
            var audio = project.Clips.Where(c => c.Track is TimelineTrack.Voiceover or TimelineTrack.Music && !c.Muted).ToArray();
            for (var i = 0; i < audio.Length; i++)
            {
                var clip = audio[i];
                args.AddRange(["-ss", Fmt.N(clip.In, 6), "-t", Fmt.N(clip.Duration, 6), "-i", clip.Path]);
                var delay = (long)Math.Round(clip.Start * 48000);
                graph.Add($"[{i + 1}:a:0]aresample=48000,asetpts=PTS-STARTPTS,volume={Fmt.N(clip.Volume, 4)},adelay={delay}S:all=1[a{i}]");
                labels.Add($"[a{i}]");
            }
            graph.Add($"{string.Concat(labels)}amix=inputs={labels.Count}:duration=first:normalize=0,alimiter=limit=0.95:latency=1[a]");
            var cues = project.Clips.Where(c => c.Track == TimelineTrack.Subtitle && !c.Muted).OrderBy(c => c.Start)
                .Select((c, i) => new SubtitleCue(i + 1, TimeSpan.FromSeconds(c.Start), TimeSpan.FromSeconds(c.End), c.Text)).ToArray();
            if (cues.Length > 0)
            {
                var info = await probe.ProbeAsync(baseFile, ct);
                var w = info.Video!.Width;
                var h = info.Video.Height;
                await File.WriteAllTextAsync(Path.Combine(work, "captions.ass"), AssWriter.Write(new SubtitleDocument(cues),
                    new SubtitleStyle(FontSize: Math.Max(16, h / 22), PositionY: 88), new AssOptions(w, h, "Output")), ct);
                graph.Add("[0:v:0]ass=filename=captions.ass[v]");
            }
            args.AddRange(["-filter_complex", string.Join(';', graph), "-map", cues.Length > 0 ? "[v]" : "0:v:0", "-map", "[a]"]);
            if (cues.Length > 0)
            {
                var encoder = resources is not null && encoders is not null ? await encoders.SelectAsync(resources.Plan.Encoder, ct) : EncoderProfile.X264;
                args.AddRange(encoder.VideoArgs("veryfast", null, preview ? 26 : 21));
            }
            else args.AddRange(["-c:v", "copy"]);
            args.AddRange(["-c:a", "aac", "-ar", "48000", "-ac", "2", "-t", Fmt.N(project.Duration, 6), "-movflags", "+faststart"]);
            var rendered = Path.Combine(work, "rendered.mp4");
            await ffmpeg.RunAsync(new FfmpegJob(args, rendered, duration, work, "Trộn timeline"),
                new Progress<ProgressInfo>(p => progress?.Report(p with { Fraction = 0.8 + p.Fraction * 0.2 })), ct);
            var outputInfo = await probe.ProbeAsync(rendered, ct);
            if (outputInfo.Video is null || outputInfo.Audio is null || Math.Abs(outputInfo.Duration.TotalSeconds - project.Duration) > 0.15)
                throw new InvalidOperationException("File xuất không khớp thời lượng timeline.");
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(rendered, target, overwrite: true);
        }
        finally { Effects.ReupRenderer.TryDelete(work); }
    }
}
