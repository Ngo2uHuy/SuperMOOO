using SuperMOOO.AI.Worker;
using SuperMOOO.AI.Worker.V1;
using SuperMOOO.Core;
using SuperMOOO.Core.Abstractions;
using SuperMOOO.Core.Conventions;

namespace SuperMOOO.AI.Audio;

public sealed record SpeechRegion(TimeSpan Start, TimeSpan End);

/// <summary>Ngưỡng 0 hoặc mili giây 0 = để worker dùng mặc định (0.5 / 250 / 400 / 30).</summary>
public sealed record SpeechDetectionOptions(double Threshold = 0, int MinSpeechMs = 0, int MinSilenceMs = 0, int SpeechPadMs = 0);

[Service]
public sealed class AudioCleanupService(IAiWorkerClient worker, WavPreparer wav, AppPaths paths, IToolLocator tools)
{
    private const int VadRate = 16000;
    private const int DenoiseRate = 44100;
    public const int SeparationRate = 44100;

    /// <summary>Đủ model tách giọng MDX (S16 → Tải model) và Python của AI worker.</summary>
    public bool CanSeparateVocals => File.Exists(WorkerLocation.SeparationModel(paths)) && tools.TryResolve(ToolKind.Python, out _);

    /// <summary>Đủ model RoFormer và PyTorch trong venv worker (worker vẫn báo FailedPrecondition nếu torch hỏng).</summary>
    public bool CanSeparateHighQuality => File.Exists(WorkerLocation.RoformerModel(paths)) && tools.TryResolve(ToolKind.Python, out _)
                                          && File.Exists(Path.Combine(paths.WorkerVenv, "Lib", "site-packages", "torch", "__init__.py"));

    /// <summary>Tách giọng gốc: input là WAV 44.1 kHz (mono/stereo), output là nền không lời WAV 44.1 kHz stereo. Worker xử lý theo đoạn nên RAM không tăng theo độ dài.</summary>
    public async Task SeparateVocalsAsync(string inputPath, string outputPath, string modelPath, int threads, IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        var request = new SeparateVocalsRequest
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            ModelPath = modelPath,
            Threads = threads
        };
        await foreach (var e in worker.SeparateVocalsAsync(request, ct))
            if (e.EventCase == TaskEvent.EventOneofCase.Progress)
                progress?.Report(new ProgressInfo(e.Progress.Fraction, e.Progress.Message));
    }

    public async Task<IReadOnlyList<SpeechRegion>> DetectSpeechAsync(string audioPath, SpeechDetectionOptions? options, CancellationToken ct)
    {
        options ??= new SpeechDetectionOptions();
        using var prepared = await wav.PrepareAsync(audioPath, VadRate, ct);
        var response = await worker.DetectSpeechAsync(new DetectSpeechRequest
        {
            AudioPath = prepared.Path,
            Threshold = options.Threshold,
            MinSpeechMs = options.MinSpeechMs,
            MinSilenceMs = options.MinSilenceMs,
            SpeechPadMs = options.SpeechPadMs
        }, ct);
        return [.. response.Regions.Select(r => new SpeechRegion(TimeSpan.FromSeconds(r.Start), TimeSpan.FromSeconds(r.End)))];
    }

    /// <summary>Khử nhiễu bằng ONNX; output là WAV 44.1 kHz mono. Model do S16 cài vào models\denoise.</summary>
    public async Task DenoiseAsync(string inputPath, string outputPath, double strength, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var prepared = await wav.PrepareAsync(inputPath, DenoiseRate, ct);
        var request = new DenoiseRequest
        {
            InputPath = prepared.Path,
            OutputPath = outputPath,
            ModelPath = WorkerLocation.DenoiserModel(paths),
            Strength = strength
        };
        await foreach (var e in worker.DenoiseAsync(request, ct))
            if (e.EventCase == TaskEvent.EventOneofCase.Progress)
                progress?.Report(new ProgressInfo(e.Progress.Fraction, e.Progress.Message));
    }
}
