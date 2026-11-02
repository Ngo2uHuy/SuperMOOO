using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using SuperMOOO.AI.Audio;
using SuperMOOO.AI.Worker;
using SuperMOOO.Media.Ffmpeg;
using Microsoft.Extensions.Logging;
using F = SuperMOOO.Media.Ffmpeg.Fmt;

namespace SuperMOOO.Workflows.Steps;

public sealed class ProbeOptions
{
    public bool RequireVideo { get; set; } = true;
}

public sealed class ProbeStep(IMediaProbe probe) : PipelineStep<ProbeOptions>
{
    public override string Kind => "probe";

    protected override async Task<StepOutput> RunAsync(StepContext c, ProbeOptions o, CancellationToken ct)
    {
        var source = Io.Source(c);
        if (!File.Exists(source)) throw new FileNotFoundException("Không tìm thấy file nguồn.", source);
        var info = await probe.ProbeAsync(source, ct);
        if (o.RequireVideo && info.Video is null) throw new MediaProbeException("File nguồn phải có video hợp lệ.");
        if (info.Duration <= TimeSpan.Zero) throw new MediaProbeException("Không xác định được thời lượng file nguồn.");
        var (w, h) = info.Video is null ? (0, 0) : info.DisplaySize();
        return Io.Out([("duration", Io.Fmt(info.Duration.TotalSeconds)), ("hasVideo", (info.Video is not null).ToString()),
            ("hasAudio", (info.Audio is not null).ToString()), ("width", w.ToString(CultureInfo.InvariantCulture)),
            ("height", h.ToString(CultureInfo.InvariantCulture))]);
    }
}

public sealed class ExtractAudioOptions
{
    [Range(8000, 96000)] public int SampleRate { get; set; } = 16000;
    public bool Mono { get; set; } = true;
}

public sealed class ExtractAudioStep(FfmpegRunner ffmpeg, IMediaProbe probe) : PipelineStep<ExtractAudioOptions>
{
    public override string Kind => "extract_audio";

    protected override async Task<StepOutput> RunAsync(StepContext c, ExtractAudioOptions o, CancellationToken ct)
    {
        var source = Io.Source(c);
        var info = await probe.ProbeAsync(source, ct);
        var output = Io.Work(c, "audio.wav");
        var rate = o.SampleRate.ToString(CultureInfo.InvariantCulture);
        List<string> args;
        if (info.Audio is not null)
        {
            args = ["-i", source, "-vn", "-map", "0:a:0"];
            if (o.Mono) args.AddRange(["-ac", "1"]);
            args.AddRange(["-ar", rate, "-c:a", "pcm_s16le"]);
        }
        else if (Io.Str(c, "segmentsFile") is not null)
        {
            // Nguồn câm nhưng người dùng đã nhập phụ đề: dùng nền im lặng để các bước sau vẫn chạy.
            args = ["-f", "lavfi", "-i", $"anullsrc=r={rate}:cl=mono", "-t", F.N(info.Duration.TotalSeconds, 3), "-c:a", "pcm_s16le"];
        }
        else throw new InvalidOperationException("Video không có âm thanh; hãy nhập phụ đề trước khi lồng tiếng.");

        await ffmpeg.RunAsync(new FfmpegJob(args, output, info.Duration, Label: "Tách âm thanh"), c.Progress, ct);
        return Io.Out([("audio", output)], output);
    }
}

public sealed class DenoiseOptions
{
    [RegularExpression("Fast|Ai", ErrorMessage = "Mode phải là Fast hoặc Ai.")] public string Mode { get; set; } = "Fast";
    [Range(0.0, 1.0)] public double Strength { get; set; } = 0.8;
}

public sealed class DenoiseStep(FfmpegRunner ffmpeg, AudioCleanupService cleanup) : PipelineStep<DenoiseOptions>
{
    public override string Kind => "denoise";

    protected override async Task<StepOutput> RunAsync(StepContext c, DenoiseOptions o, CancellationToken ct)
    {
        var input = Io.State(c, "audio");
        var output = Io.Work(c, "denoised.wav");
        if (o.Mode == "Ai") await cleanup.DenoiseAsync(input, output, o.Strength, c.Progress, ct);
        else
            await ffmpeg.RunAsync(new FfmpegJob(["-i", input, "-af", "afftdn=nf=-25", "-c:a", "pcm_s16le"], output, Label: "Khử nhiễu"), c.Progress, ct);
        // Bản đã khử nhiễu dùng cho cả nhận diện lẫn trộn tiếng gốc (giữ hành vi Craw).
        return Io.Out([("audio", output), ("denoisedAudio", output)], output);
    }
}

public sealed class SeparateVocalsOptions
{
    /// <summary>roformer: Mel-Band RoFormer (PyTorch, chất lượng cao); mdx: UVR MDX-Net Voc_FT (ONNX, nhanh).</summary>
    [RegularExpression("roformer|mdx", ErrorMessage = "Quality phải là roformer hoặc mdx.")] public string Quality { get; set; } = SeparationQuality.Default;
}

/// <summary>
/// Tách giọng gốc để bước trộn chỉ thay lời, giữ nhạc/hiệu ứng (state "backgroundAudio"). Cache theo SHA-256 nội dung file nguồn + tên model
/// nên chạy lại/đổi giọng không tách lại. Thiếu PyTorch/model RoFormer thì dùng MDX (cảnh báo); thiếu cả hai hoặc worker cũ chưa có RPC
/// thì cảnh báo và trộn tiếng gốc + ducking như cũ.
/// </summary>
public sealed class SeparateVocalsStep(FfmpegRunner ffmpeg, AudioCleanupService cleanup, AppPaths paths, IRuntimeResources resources,
    ILogger<SeparateVocalsStep> log) : PipelineStep<SeparateVocalsOptions>
{
    private const string Fallback = "Không tách được giọng gốc ({0}): dùng tiếng gốc + ducking như cũ.";
    private const string FastFallback = "Không dùng được RoFormer ({0}): tách giọng bằng MDX Voc_FT (nhanh, kém hơn một chút).";

    public override string Kind => "separate_vocals";

    protected override async Task<StepOutput> RunAsync(StepContext c, SeparateVocalsOptions o, CancellationToken ct)
    {
        if (Io.StateOrNull(c, "hasAudio") == "False") return Io.Out([]);
        var warnings = new List<string>();
        var models = new List<string>();
        if (o.Quality == SeparationQuality.Roformer)
        {
            if (cleanup.CanSeparateHighQuality) models.Add(WorkerLocation.RoformerModel(paths));
            else warnings.Add(Warn(FastFallback, "thiếu PyTorch hoặc model RoFormer, cài ở S16 → AI worker"));
        }
        if (cleanup.CanSeparateVocals) models.Add(WorkerLocation.SeparationModel(paths));
        if (models.Count == 0) return Skip(c, warnings, "thiếu model tách giọng hoặc AI worker, cài ở S16 → Tải model");

        var source = Io.Source(c);
        string hash;
        await using (var stream = File.OpenRead(source))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        string Cache(string model) => Path.Combine(paths.CacheDir("separation"), $"{hash}_{Path.GetFileNameWithoutExtension(model)}.wav");
        if (File.Exists(Cache(models[0]))) return Done(c, warnings, Cache(models[0]));

        Directory.CreateDirectory(paths.CacheDir("separation"));
        var input = Io.Work(c, "separate_in.wav");
        var rate = AudioCleanupService.SeparationRate.ToString(CultureInfo.InvariantCulture);
        await ffmpeg.RunAsync(new FfmpegJob(["-i", source, "-vn", "-map", "0:a:0", "-ac", "2", "-ar", rate, "-c:a", "pcm_s16le"], input,
            Label: "Tách âm thanh"), null, ct);
        try
        {
            for (var i = 0; ; i++)
            {
                var cache = Cache(models[i]);
                try
                {
                    // Worker ghi qua .partial rồi đổi tên nên file cache tồn tại là đã đủ.
                    if (!File.Exists(cache)) await cleanup.SeparateVocalsAsync(input, cache, models[i], resources.Plan.AiThreads, c.Progress, ct);
                    return Done(c, warnings, cache);
                }
                catch (AiWorkerException e) when (e.Code == Grpc.Core.StatusCode.FailedPrecondition && i + 1 < models.Count)
                {
                    warnings.Add(Warn(FastFallback, e.Message));
                }
            }
        }
        catch (AiWorkerException e) when (e.Code is Grpc.Core.StatusCode.Unavailable or Grpc.Core.StatusCode.Unimplemented
                                              or Grpc.Core.StatusCode.FailedPrecondition)
        {
            return Skip(c, warnings, e.Message);
        }
        finally
        {
            File.Delete(input);
        }
    }

    private static StepOutput Done(StepContext c, List<string> warnings, string background)
    {
        var values = new List<(string, string)> { ("backgroundAudio", background) };
        if (warnings.Count > 0) values.Add(("warnings", Io.Warnings(c, warnings)));
        return Io.Out(values, background);
    }

    private StepOutput Skip(StepContext c, List<string> warnings, string reason)
    {
        warnings.Add(Warn(Fallback, reason));
        return Io.Out([("warnings", Io.Warnings(c, warnings))]);
    }

    private string Warn(string format, string reason)
    {
        var message = string.Format(CultureInfo.InvariantCulture, format, reason);
        log.LogWarning("{Message}", message);
        return message;
    }
}

public sealed class VadOptions
{
    [Range(0.0, 0.95)] public double Threshold { get; set; }
    public int MinSpeechMs { get; set; }
    public int MinSilenceMs { get; set; }
    public int SpeechPadMs { get; set; }
    public bool Export { get; set; }
}

public sealed record SpeechSpan(double Start, double End);

public sealed class VadStep(AudioCleanupService cleanup, IOutputRegistry outputs) : PipelineStep<VadOptions>
{
    public override string Kind => "vad";

    protected override async Task<StepOutput> RunAsync(StepContext c, VadOptions o, CancellationToken ct)
    {
        var regions = await cleanup.DetectSpeechAsync(Io.State(c, "audio"),
            new SpeechDetectionOptions(o.Threshold, o.MinSpeechMs, o.MinSilenceMs, o.SpeechPadMs), ct);
        var spans = regions.Select(r => new SpeechSpan(Math.Round(r.Start.TotalSeconds, 3), Math.Round(r.End.TotalSeconds, 3))).ToList();
        var json = JsonSerializer.Serialize(spans, JsonSerializerOptions.Web);
        var file = Io.Work(c, "speech.json");
        await File.WriteAllTextAsync(file, json, ct);

        var files = new List<string> { file };
        if (o.Export)
        {
            var exported = Io.UniquePath(Io.OutputDir(c), $"{Io.Name(c)}_speech.json");
            await File.WriteAllTextAsync(exported, json, ct);
            await outputs.RegisterOutputAsync(exported, "audio", ct);
            files.Add(exported);
        }

        return Io.Out([("speechRegions", file), ("speechSeconds", Io.Fmt(spans.Sum(s => s.End - s.Start)))], [.. files]);
    }
}

public sealed class TrimSilenceOptions
{
    [Range(0, 2000)] public int PadMs { get; set; }
}

public sealed class TrimSilenceStep(FfmpegRunner ffmpeg, AudioCleanupService cleanup) : PipelineStep<TrimSilenceOptions>
{
    public override string Kind => "trim_silence";

    protected override async Task<StepOutput> RunAsync(StepContext c, TrimSilenceOptions o, CancellationToken ct)
    {
        var input = Io.State(c, "audio");
        List<SpeechSpan> spans;
        if (Io.StateOrNull(c, "speechRegions") is { } file)
            spans = JsonSerializer.Deserialize<List<SpeechSpan>>(await File.ReadAllTextAsync(file, ct), JsonSerializerOptions.Web) ?? [];
        else
            spans = [.. (await cleanup.DetectSpeechAsync(input, null, ct)).Select(r => new SpeechSpan(r.Start.TotalSeconds, r.End.TotalSeconds))];
        if (spans.Count == 0) throw new InvalidOperationException("Không phát hiện lời nói nên không có gì để giữ lại.");

        var pad = o.PadMs / 1000.0;
        var graph = new StringBuilder();
        // Mỗi nhãn đầu vào chỉ dùng được một lần nên tách luồng trước khi cắt.
        graph.Append(CultureInfo.InvariantCulture, $"[0:a]asplit={spans.Count}{string.Concat(Enumerable.Range(0, spans.Count).Select(i => $"[s{i}]"))};");
        for (var i = 0; i < spans.Count; i++)
            graph.Append(CultureInfo.InvariantCulture,
                $"[s{i}]atrim=start={F.N(Math.Max(0, spans[i].Start - pad), 3)}:end={F.N(spans[i].End + pad, 3)},asetpts=PTS-STARTPTS[a{i}];");
        for (var i = 0; i < spans.Count; i++) graph.Append($"[a{i}]");
        graph.Append(CultureInfo.InvariantCulture, $"concat=n={spans.Count}:v=0:a=1[out]");

        // Graph dài nên đưa qua file script thay vì dòng lệnh (giới hạn độ dài lệnh Windows).
        var script = Io.Work(c, "trim.filter");
        await File.WriteAllTextAsync(script, graph.ToString(), ct);
        var output = Io.Work(c, "trimmed.wav");
        await ffmpeg.RunAsync(new FfmpegJob(["-i", input, "-/filter_complex", script, "-map", "[out]", "-c:a", "pcm_s16le"], output,
            Label: "Cắt khoảng lặng"), c.Progress, ct);
        return Io.Out([("audio", output)], output);
    }
}
