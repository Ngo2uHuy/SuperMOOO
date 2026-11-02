using Grpc.Core;

namespace SuperMOOO.AI.Worker;

/// <summary>Lỗi từ worker, giữ nguyên mã gRPC để UI phân biệt (ví dụ Unimplemented = module tuỳ chọn chưa cài).</summary>
public sealed class AiWorkerException(StatusCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public StatusCode Code => code;

    public static Exception From(RpcException error, CancellationToken ct) =>
        error.StatusCode == StatusCode.Cancelled || ct.IsCancellationRequested
            ? new OperationCanceledException(ct)
            : new AiWorkerException(error.StatusCode, error.StatusCode == StatusCode.Unavailable
                ? "Không kết nối được AI worker."
                : error.Status.Detail, error);
}
