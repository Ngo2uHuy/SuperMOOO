using System.Globalization;

namespace SuperMOOO.Core.Publishing;

public enum BlockReason { RateLimited, CircuitOpen }

/// <summary>Request bị chặn trước khi gửi (hết token/quota, Retry-After, circuit Open). Không sleep: runner dời job tới <see cref="RetryAt"/>.</summary>
public sealed class NetworkRequestBlockedException(BlockReason reason, DateTimeOffset retryAt, string message, string? connectionId = null)
    : Exception(message)
{
    public BlockReason Reason { get; } = reason;
    public DateTimeOffset RetryAt { get; } = retryAt;
    public string? ConnectionId { get; } = connectionId;
}

// --- Rate limiter: token bucket theo (platform, connectionId) + quota ngày (UTC) ---

public sealed record RateLimitConfig(int Capacity, double RefillPerSecond, int DailyQuota = 0);

[Service]
public sealed class RateLimiterOptions
{
    public RateLimitConfig Default { get; set; } = new(10, 1.0);
    public Dictionary<string, RateLimitConfig> Platforms { get; } = new(StringComparer.OrdinalIgnoreCase);
    public RateLimitConfig For(string platform) => Platforms.GetValueOrDefault(platform) ?? Default;
}

public sealed record RateLimitDecision(bool Allowed, DateTimeOffset? RetryAt, string? Reason);

public interface IRateLimiter
{
    RateLimitDecision TryAcquire(string platform, string connectionId, int cost = 1);
    /// <summary>Không cho gọi trước <paramref name="until"/> (Retry-After của máy chủ).</summary>
    void BlockUntil(string platform, string connectionId, DateTimeOffset until);
}

public static class RetryAfter
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    /// <summary>Retry-After dạng số giây hoặc HTTP-date; quá khứ → now; trần 1 ngày.</summary>
    public static DateTimeOffset? Parse(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return now + TimeSpan.FromSeconds(Math.Min(seconds, MaxWait.TotalSeconds));
        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return Clamp(date, now);
        return null;
    }

    public static DateTimeOffset? From(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return now + (delta > MaxWait ? MaxWait : delta < TimeSpan.Zero ? TimeSpan.Zero : delta);
        if (header?.Date is { } date) return Clamp(date, now);
        return null;
    }

    private static DateTimeOffset Clamp(DateTimeOffset at, DateTimeOffset now) => at < now ? now : at > now + MaxWait ? now + MaxWait : at;
}

// --- Circuit breaker theo connection và theo platform ---

public enum CircuitState { Closed, Open, HalfOpen }
public enum FailureKind { Transient, Auth }

public sealed record CircuitDecision(bool Allowed, CircuitState State, DateTimeOffset? RetryAt, string? Reason, string? Scope);

[Service]
public sealed class CircuitBreakerOptions
{
    public int ConnectionFailureThreshold { get; set; } = 5;
    public int PlatformFailureThreshold { get; set; } = 10;
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>401/403/policy: mở ngay cho connection và giữ lâu hơn (cần người dùng đăng nhập lại/sửa quyền).</summary>
    public TimeSpan AuthOpenDuration { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan HalfOpenRetry { get; set; } = TimeSpan.FromSeconds(5);
}

public interface ICircuitBreaker
{
    /// <summary>Không đổi trạng thái (dùng để runner quyết định dời job).</summary>
    CircuitDecision Peek(string platform, string connectionId);
    /// <summary>Half-open chỉ cho 1 request thử tại một thời điểm.</summary>
    CircuitDecision TryPass(string platform, string connectionId);
    void RecordSuccess(string platform, string connectionId);
    void RecordFailure(string platform, string connectionId, FailureKind kind, string? reason = null);
    void Reset(string connectionId);
}
