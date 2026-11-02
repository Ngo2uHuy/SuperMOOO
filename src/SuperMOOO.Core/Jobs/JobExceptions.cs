namespace SuperMOOO.Core.Jobs;

/// <summary>Handler ném khi lỗi không được tự thử lại (vd bước cloud tính phí): job chuyển WaitingManualRetry.</summary>
public sealed class JobWaitingManualRetryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Job chủ động hẹn chạy lại (cổng mạng đóng, hết quota, chờ xử lý): dời tới <see cref="Due"/>, không tính lượt thử.</summary>
public sealed class JobDeferredException(DateTimeOffset due, string message) : Exception(message)
{
    public DateTimeOffset Due { get; } = due;
}

/// <summary>Lỗi tạm thời (429/5xx, mất mạng): thử lại theo backoff có jitter nếu còn lượt.</summary>
/// <param name="retryAt">Không thử lại trước mốc này (vd Retry-After của máy chủ); backoff vẫn áp dụng nếu dài hơn.</param>
public sealed class TransientJobException(string message, Exception? inner = null, DateTimeOffset? retryAt = null) : Exception(message, inner)
{
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

/// <summary>Khoá trong payload job mà JobQueue hiểu chung cho mọi loại job.</summary>
public static class JobPayloadKeys
{
    /// <summary>Hai job cùng khoá (vd "account:&lt;id&gt;") không chạy đồng thời trong cùng lane.</summary>
    public const string ConcurrencyKey = "concurrencyKey";
}

public sealed record JobPreRunContext(string JobId, string Type, JsonNode Payload, int Attempts, JobLane? Lane = null);

/// <param name="DeferUntil">Dời job tới mốc này, không tính lượt thử.</param>
public sealed record JobPreRunDecision(DateTimeOffset DeferUntil, string Reason);

/// <summary>
/// Kiểm tra trước khi chạy handler (tuyến mạng, ngắt mạch, ổ đầy, cờ dừng tự động). Trả null = cho chạy.
/// Đăng ký bằng [Service(As = [typeof(IJobPreRunCheck)])]; chỉ áp cho loại job mà <see cref="AppliesTo"/> trả true.
/// </summary>
public interface IJobPreRunCheck
{
    bool AppliesTo(string jobType);
    Task<JobPreRunDecision?> CheckAsync(JobPreRunContext job, CancellationToken ct);
}
