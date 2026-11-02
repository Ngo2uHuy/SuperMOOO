using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Infrastructure.Network;

/// <summary>
/// Rate limit (token bucket + quota ngày + Retry-After) và circuit breaker cho request của một tài khoản.
/// 429/503 kèm Retry-After chỉ chặn lần gọi sau (không tính là lỗi breaker); 401/403 mở breaker ngay; 5xx/timeout/lỗi kết nối tính lỗi tạm.
/// Request mang <see cref="CallerHandlesAuth"/>: 401/403 không mở breaker (connector còn làm mới token và thử lại 1 lần,
/// quota 403 không phải lỗi xác thực); tầng ứng dụng tự ghi lỗi Auth khi kết luận cần kết nối lại.
/// </summary>
public sealed class ResilienceHandler(string platform, string connectionId, IRateLimiter limiter, ICircuitBreaker breaker, IClock clock)
    : DelegatingHandler
{
    public const string CallerHandlesAuthOptionName = "SuperMOOO.CallerHandlesAuth";
    public static readonly HttpRequestOptionsKey<bool> CallerHandlesAuth = new(CallerHandlesAuthOptionName);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Limiter trước breaker: breaker half-open đã nhận request thử thì phải gửi thật, không bỏ rơi slot thử.
        var rate = limiter.TryAcquire(platform, connectionId);
        if (!rate.Allowed)
            throw new NetworkRequestBlockedException(BlockReason.RateLimited, rate.RetryAt ?? clock.UtcNow + TimeSpan.FromSeconds(1),
                rate.Reason ?? "Vượt giới hạn tốc độ gọi.", connectionId);

        var gate = breaker.TryPass(platform, connectionId);
        if (!gate.Allowed)
            throw new NetworkRequestBlockedException(BlockReason.CircuitOpen, gate.RetryAt ?? clock.UtcNow + TimeSpan.FromSeconds(5),
                gate.Reason ?? "Kết nối tạm ngắt.", connectionId);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            breaker.RecordFailure(platform, connectionId, FailureKind.Transient, "Lỗi kết nối hoặc hết thời gian chờ.");
            throw;
        }

        if (RetryAfter.From(response, clock.UtcNow) is { } until) limiter.BlockUntil(platform, connectionId, until);

        var code = (int)response.StatusCode;
        var callerHandlesAuth = request.Options.TryGetValue(CallerHandlesAuth, out var deferred) && deferred;
        if (code is 401 or 403 && !callerHandlesAuth) breaker.RecordFailure(platform, connectionId, FailureKind.Auth, $"Máy chủ từ chối (HTTP {code}).");
        else if (code >= 500) breaker.RecordFailure(platform, connectionId, FailureKind.Transient, $"Lỗi máy chủ (HTTP {code}).");
        else breaker.RecordSuccess(platform, connectionId);   // 429 là tín hiệu tốc độ (đã xử lý bằng limiter), không phải sự cố
        return response;
    }
}
