using SuperMOOO.AI.Worker;
using SuperMOOO.AI.Worker.V1;

namespace SuperMOOO.AI.Vision;

/// <summary>Hộp theo % khung nguồn (0–100).</summary>
public sealed record TextBand(double X, double Y, double W, double H);

/// <summary>Kết quả tự tìm vùng phụ đề cứng; Confidence &lt; 0,5 nên để người dùng kiểm tra lại.</summary>
public sealed record TextBandResult(TextBand Box, double Confidence, int FramesWithText, int FramesSampled);

public sealed record TextSpan(double Start, double End);

/// <summary>Video chỉ chứa cửa sổ quanh vùng chữ đã xoá, đặt tại (X, Y) pixel của khung nguồn.</summary>
public sealed record InpaintPatchResult(string File, int X, int Y, int Width, int Height, int Frames, int Inpainted);

/// <summary>Phụ đề cứng trong video nguồn qua AI worker: tìm vùng, dò lúc có chữ, xoá bằng inpaint (MI-GAN). Model do S16 tải vào models\vision.</summary>
[Service]
public sealed class BurnedSubtitleService(IAiWorkerClient worker, IMediaProbe probe, IToolLocator tools, AppPaths paths, IRuntimeResources? resources = null)
{
    public bool DetectorInstalled => File.Exists(WorkerLocation.TextDetModel(paths));

    public bool InpainterInstalled => File.Exists(WorkerLocation.InpaintModel(paths));

    /// <summary>Thao tác người dùng đang chờ (~15–25 s) nên không xếp lượt media sau job render dài.</summary>
    public async Task<TextBandResult?> LocateAsync(string video, CancellationToken ct)
    {
        var (w, h, _, duration) = await VideoAsync(video, ct);
        var r = await worker.LocateSubtitlesAsync(new LocateSubtitlesRequest
        {
            VideoPath = video, FfmpegPath = tools.Resolve(ToolKind.Ffmpeg), DetModelPath = Detector(), Width = w, Height = h, Duration = duration
        }, ct);
        return r.Found ? new TextBandResult(new TextBand(r.Box.X, r.Box.Y, r.Box.W, r.Box.H), r.Confidence, r.FramesWithText, r.FramesSampled) : null;
    }

    public async Task<IReadOnlyList<TextSpan>> TimelineAsync(string video, TextBand box, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var (w, h, _, duration) = await VideoAsync(video, ct);
        var request = new TextTimelineRequest
        {
            VideoPath = video, FfmpegPath = tools.Resolve(ToolKind.Ffmpeg), DetModelPath = Detector(), Width = w, Height = h, Duration = duration,
            Box = Rect(box)
        };
        using var lease = resources is null ? null : await resources.AcquireMediaAsync(ct);
        await foreach (var e in worker.TextTimelineAsync(request, ct))
        {
            if (e.EventCase == TextTimelineEvent.EventOneofCase.Progress) progress?.Report(new ProgressInfo(e.Progress.Fraction, e.Progress.Message));
            else if (e.EventCase == TextTimelineEvent.EventOneofCase.Result) return [.. e.Result.Segments.Select(s => new TextSpan(s.Start, s.End))];
        }

        throw new InvalidOperationException("AI worker không trả kết quả dò chữ.");
    }

    /// <param name="keyframeEvery">1 = vẽ lại mọi khung có nền đổi; N &gt; 1 dùng lại phần đã vẽ cho N-1 khung kế (nhanh hơn, có thể bóng mờ khi cảnh động).</param>
    public async Task<InpaintPatchResult> InpaintAsync(string video, TextBand box, IReadOnlyList<TextSpan> spans, string output, int keyframeEvery,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!InpainterInstalled) throw new InvalidOperationException("Chưa tải model xoá phụ đề (MI-GAN). Vào Cài đặt → AI worker → Tải model.");
        var (w, h, fps, duration) = await VideoAsync(video, ct);
        var request = new InpaintSubtitlesRequest
        {
            VideoPath = video, FfmpegPath = tools.Resolve(ToolKind.Ffmpeg), DetModelPath = Detector(), InpaintModelPath = WorkerLocation.InpaintModel(paths),
            Width = w, Height = h, Duration = duration, Fps = fps, Box = Rect(box), OutputPath = output, KeyframeEvery = keyframeEvery
        };
        request.Segments.AddRange(spans.Select(s => new TimeRange { Start = s.Start, End = s.End }));
        using var lease = resources is null ? null : await resources.AcquireMediaAsync(ct);
        await foreach (var e in worker.InpaintSubtitlesAsync(request, ct))
        {
            if (e.EventCase == InpaintEvent.EventOneofCase.Progress) progress?.Report(new ProgressInfo(e.Progress.Fraction, e.Progress.Message));
            else if (e.EventCase == InpaintEvent.EventOneofCase.Completed)
            {
                var c = e.Completed;
                return new InpaintPatchResult(c.OutputPath, c.X, c.Y, c.Width, c.Height, c.Frames, c.Inpainted);
            }
        }

        throw new InvalidOperationException("AI worker không trả kết quả xoá phụ đề.");
    }

    private string Detector() => DetectorInstalled
        ? WorkerLocation.TextDetModel(paths)
        : throw new InvalidOperationException("Chưa tải model phát hiện chữ. Vào Cài đặt → AI worker → Tải model.");

    private static PercentRect Rect(TextBand b) => new() { X = b.X, Y = b.Y, W = b.W, H = b.H };

    // ffmpeg tự xoay khung theo metadata nên worker nhận kích thước hiển thị.
    private async Task<(int Width, int Height, double Fps, double Duration)> VideoAsync(string video, CancellationToken ct)
    {
        var info = await probe.ProbeAsync(video, ct);
        if (info.Video is not { } v) throw new InvalidOperationException("File nguồn không có luồng video.");
        var rotated = Math.Abs(v.Rotation) % 180 == 90;
        return (rotated ? v.Height : v.Width, rotated ? v.Width : v.Height, v.Fps > 0 ? v.Fps : 30, info.Duration.TotalSeconds);
    }
}
