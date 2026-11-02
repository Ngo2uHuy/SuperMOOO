using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Media.Preview;

/// <summary>Xem trước đúng bằng filter graph xuất (D2): đoạn ngắn 3–10 s hoặc một khung hình; không mô phỏng riêng bằng WPF.</summary>
[Service]
public sealed class PreviewRenderer(ReupRenderer renderer, FfmpegRunner ffmpeg, IMediaProbe probe)
{
    public const int MaxPreviewHeight = 720;

    /// <summary>Render <paramref name="seconds"/> giây nguồn bắt đầu từ <paramref name="start"/>, nhanh (x264 ultrafast, ≤ 720p).</summary>
    public async Task<ReupRenderResult> RenderClipAsync(string input, ReupEffectProfile profile, TimeSpan start, double seconds,
        string output, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var info = await probe.ProbeAsync(input, ct);
        var length = Math.Clamp(seconds, 3, 10);
        var originalStart = profile.Video.TrimStart ?? TimeSpan.Zero;
        start = TimeSpan.FromSeconds(Math.Clamp(start.TotalSeconds, originalStart.TotalSeconds, Math.Max(originalStart.TotalSeconds, info.Duration.TotalSeconds - 0.5)));
        var end = TimeSpan.FromSeconds(Math.Min(info.Duration.TotalSeconds, start.TotalSeconds + length));

        // Giọng đọc/phụ đề tính trên timeline ra: dịch lùi đúng phần đã bỏ đi phía trước đoạn xem trước.
        var speed = Math.Clamp(profile.Video.Speed, 0.5, 2);
        var shift = TimeSpan.FromSeconds(Math.Max(0, (start - originalStart).TotalSeconds) / speed);
        var subtitles = profile.Subtitles is { } s && s.TimeBase.Equals("Output", StringComparison.OrdinalIgnoreCase)
            ? s with { Document = new SubtitleDocument(s.Document.Cues.Select(c => c with { Start = c.Start - shift, End = c.End - shift }).Where(c => c.End > TimeSpan.Zero).ToList()) }
            : profile.Subtitles;

        var preview = profile with
        {
            Video = profile.Video with { TrimStart = start, TrimEnd = end },
            Audio = profile.Audio with { VoiceoverOffset = profile.Audio.VoiceoverOffset - shift },
            Subtitles = subtitles,
            IntroOutro = null,
            Output = profile.Output with
            {
                Height = Math.Min(profile.Output.Height, MaxPreviewHeight), Encoder = "x264", Preset = "ultrafast",
                BitrateKbps = null, WhenVoiceLonger = "CutVoice", StripMetadata = false
            }
        };
        return await renderer.RenderAsync(input, preview, output, progress, ct);
    }

    public Task RenderFrameAsync(string input, ReupEffectProfile profile, TimeSpan at, string outputImage, CancellationToken ct) =>
        renderer.RenderFrameAsync(input, profile, at, outputImage, ct);

    /// <summary>Chụp khung gốc tại mốc at (ảnh bìa, thumbnail).</summary>
    public async Task ExtractFrameAsync(string input, TimeSpan at, string outputImage, CancellationToken ct)
    {
        var info = await ProbeCachedAsync(input, ct);
        if (info.Video is null || at < TimeSpan.Zero || at >= info.Duration) throw new ArgumentException("Mốc chụp phải nằm trong thời lượng video.");
        var args = new List<string> { "-ss", Fmt.Sec(at), "-i", input, "-frames:v", "1", "-an" };
        if (Path.GetExtension(outputImage).Equals(".jpg", StringComparison.OrdinalIgnoreCase)) args.AddRange(["-q:v", "2"]);
        args.AddRange(["-update", "1"]);
        await ffmpeg.RunAsync(new FfmpegJob(args, outputImage, Label: "Chụp khung hình", Light: true), null, ct);
    }

    private sealed record ProbeEntry(string Key, MediaInfo Info);
    private ProbeEntry? _lastProbe;

    // Kéo thanh chụp khung liên tục trên cùng một file: ffprobe mỗi lần tốn ~40% thời gian chụp, nên nhớ kết quả theo đường dẫn + mốc sửa + kích thước.
    private async Task<MediaInfo> ProbeCachedAsync(string input, CancellationToken ct)
    {
        var file = new FileInfo(input);
        var key = file.Exists ? $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{file.Length}" : "";
        if (key.Length > 0 && _lastProbe is { } last && last.Key == key) return last.Info;
        var info = await probe.ProbeAsync(input, ct);
        if (key.Length > 0) _lastProbe = new ProbeEntry(key, info);
        return info;
    }
}
