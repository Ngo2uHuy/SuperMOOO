namespace SuperMOOO.Core.Pipelines;

/// <summary>Kết quả một job pipeline đã chạy xong (kèm input gốc), để các bên quan sát như ghi manifest bàn giao.</summary>
public sealed record PipelineCompletion(string JobId, string PipelineId, JsonObject Input, JsonNode Result);

/// <summary>Được gọi sau khi pipeline hoàn tất, trước khi job chuyển Succeeded. Lỗi của observer chỉ được log, không làm hỏng job.</summary>
public interface IPipelineCompletionObserver
{
    Task OnCompletedAsync(PipelineCompletion completion, CancellationToken ct);
}
