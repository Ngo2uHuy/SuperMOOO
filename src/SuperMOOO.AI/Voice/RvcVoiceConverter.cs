using SuperMOOO.AI.Worker;
using SuperMOOO.AI.Worker.V1;
using SuperMOOO.Core;
using SuperMOOO.Core.Abstractions;
using SuperMOOO.Core.Conventions;
using Grpc.Core;

namespace SuperMOOO.AI.Voice;

public sealed record RvcModel(string Name, string ModelPath, string? IndexPath);

public sealed record RvcConvertRequest(string InputPath, string OutputPath, string ModelPath, string? IndexPath = null,
    int PitchSemitones = 0, string F0Method = "rmvpe", double IndexRate = 0.5);

/// <summary>Plugin tuỳ chọn: worker chưa cài rvc-python thì mọi lời gọi trả UNIMPLEMENTED; UI dựa vào IsAvailableAsync để ẩn.</summary>
[Service]
public sealed class RvcVoiceConverter(IAiWorkerClient worker, AppPaths paths)
{
    private string ModelsDir => Path.Combine(paths.Models, "rvc");

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try { return (await worker.GetCapabilitiesAsync(ct)).VoiceConversion.Available; }
        catch (AiWorkerException) { return false; }
    }

    public async Task<IReadOnlyList<RvcModel>> ListModelsAsync(CancellationToken ct)
    {
        var response = await worker.ListVoiceModelsAsync(new ListVoiceModelsRequest { ModelsDir = ModelsDir }, ct);
        return [.. response.Models.Select(m => new RvcModel(m.Name, m.ModelPath, m.IndexPath.Length == 0 ? null : m.IndexPath))];
    }

    /// <summary>Chép .pth (và .index cùng tên nếu có) vào models\rvc; chỉ là file nên không cần worker.</summary>
    public Task ImportModelAsync(string modelPath, string? indexPath, CancellationToken ct)
    {
        if (!string.Equals(Path.GetExtension(modelPath), ".pth", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Model RVC phải là file .pth.", nameof(modelPath));
        Directory.CreateDirectory(ModelsDir);
        var name = Path.GetFileNameWithoutExtension(modelPath);
        File.Copy(modelPath, Path.Combine(ModelsDir, name + ".pth"), overwrite: true);
        if (!string.IsNullOrWhiteSpace(indexPath))
            File.Copy(indexPath, Path.Combine(ModelsDir, name + ".index"), overwrite: true);
        return Task.CompletedTask;
    }

    public async Task ConvertAsync(RvcConvertRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var rpc = new VoiceConvertRequest
        {
            InputPath = request.InputPath,
            OutputPath = request.OutputPath,
            ModelPath = request.ModelPath,
            IndexPath = request.IndexPath ?? "",
            PitchSemitones = request.PitchSemitones,
            F0Method = request.F0Method,
            IndexRate = request.IndexRate
        };
        await foreach (var e in worker.ConvertVoiceAsync(rpc, ct))
            if (e.EventCase == TaskEvent.EventOneofCase.Progress)
                progress?.Report(new ProgressInfo(e.Progress.Fraction, e.Progress.Message));
    }
}
