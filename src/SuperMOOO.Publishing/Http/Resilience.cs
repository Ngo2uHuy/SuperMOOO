namespace SuperMOOO.Publishing.Http;

/// <summary>Trạng thái trong bộ nhớ (mất khi worker khởi động lại; máy chủ đích vẫn là nguồn sự thật qua 429/Retry-After).</summary>
[Service(As = [typeof(IRateLimiter)])]
public sealed class TokenBucketRateLimiter(RateLimiterOptions options, IClock clock) : IRateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public DateTimeOffset LastRefill;
        public DateTimeOffset BlockedUntil;
        public DateOnly Day;
        public int UsedToday;
    }

    private readonly Dictionary<string, Bucket> _buckets = [];
    private readonly object _lock = new();

    public RateLimitDecision TryAcquire(string platform, string connectionId, int cost = 1)
    {
        var config = options.For(platform);
        var capacity = Math.Max(1, config.Capacity);
        var rate = Math.Max(config.RefillPerSecond, 0.0001);
        cost = Math.Clamp(cost, 1, capacity);
        var now = clock.UtcNow;

        lock (_lock)
        {
            var b = GetBucket(platform, connectionId, capacity, now);
            b.Tokens = Math.Min(capacity, b.Tokens + (now - b.LastRefill).TotalSeconds * rate);
            b.LastRefill = now;

            if (b.BlockedUntil > now) return new RateLimitDecision(false, b.BlockedUntil, "Máy chủ yêu cầu chờ (Retry-After).");

            var day = DateOnly.FromDateTime(now.UtcDateTime);
            if (b.Day != day) { b.Day = day; b.UsedToday = 0; }
            if (config.DailyQuota > 0 && b.UsedToday + cost > config.DailyQuota)
                return new RateLimitDecision(false, new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), "Hết quota trong ngày.");

            if (b.Tokens + 1e-9 >= cost)
            {
                b.Tokens -= cost;
                b.UsedToday += cost;
                return new RateLimitDecision(true, null, null);
            }
            var wait = TimeSpan.FromSeconds((cost - b.Tokens) / rate);
            return new RateLimitDecision(false, now + wait, "Vượt giới hạn tốc độ gọi.");
        }
    }

    public void BlockUntil(string platform, string connectionId, DateTimeOffset until)
    {
        var config = options.For(platform);
        lock (_lock)
        {
            var b = GetBucket(platform, connectionId, Math.Max(1, config.Capacity), clock.UtcNow);
            if (until > b.BlockedUntil) b.BlockedUntil = until;
        }
    }

    private Bucket GetBucket(string platform, string connectionId, int capacity, DateTimeOffset now)
    {
        var key = platform + "|" + connectionId;
        if (!_buckets.TryGetValue(key, out var b))
            _buckets[key] = b = new Bucket { Tokens = capacity, LastRefill = now, Day = DateOnly.FromDateTime(now.UtcDateTime) };
        return b;
    }
}

[Service(As = [typeof(ICircuitBreaker)])]
public sealed class CircuitBreakerRegistry(CircuitBreakerOptions options, IClock clock) : ICircuitBreaker
{
    private sealed class Circuit(string scope)
    {
        public string Scope { get; } = scope;
        public CircuitState State;
        public int Failures;
        public DateTimeOffset OpenUntil;
        public bool ProbeInFlight;
        public string? Reason;
    }

    private readonly Dictionary<string, Circuit> _circuits = [];
    private readonly object _lock = new();

    public CircuitDecision Peek(string platform, string connectionId)
    {
        lock (_lock) return Decide(platform, connectionId, consume: false);
    }

    public CircuitDecision TryPass(string platform, string connectionId)
    {
        lock (_lock) return Decide(platform, connectionId, consume: true);
    }

    public void RecordSuccess(string platform, string connectionId)
    {
        lock (_lock)
            foreach (var c in new[] { Get("c:" + connectionId, "connection"), Get("p:" + platform, "platform") })
            {
                c.State = CircuitState.Closed;
                c.Failures = 0;
                c.ProbeInFlight = false;
                c.Reason = null;
            }
    }

    public void RecordFailure(string platform, string connectionId, FailureKind kind, string? reason = null)
    {
        var now = clock.UtcNow;
        lock (_lock)
        {
            Fail(Get("c:" + connectionId, "connection"), now, kind, options.ConnectionFailureThreshold, reason);
            // Lỗi xác thực/policy là của riêng tài khoản, không nói gì về sức khỏe của nền tảng.
            if (kind == FailureKind.Transient) Fail(Get("p:" + platform, "platform"), now, kind, options.PlatformFailureThreshold, reason);
        }
    }

    public void Reset(string connectionId)
    {
        lock (_lock) _circuits.Remove("c:" + connectionId);
    }

    private void Fail(Circuit c, DateTimeOffset now, FailureKind kind, int threshold, string? reason)
    {
        if (c.State == CircuitState.Open) return;   // phản hồi muộn của request đã bay trước khi mở
        c.ProbeInFlight = false;
        c.Failures++;
        if (c.State == CircuitState.HalfOpen || kind == FailureKind.Auth || c.Failures >= threshold)
        {
            c.State = CircuitState.Open;
            c.OpenUntil = now + (kind == FailureKind.Auth ? options.AuthOpenDuration : options.OpenDuration);
            c.Reason = reason ?? (kind == FailureKind.Auth ? "Lỗi xác thực/quyền (401/403)." : "Quá nhiều lỗi liên tiếp.");
        }
    }

    private CircuitDecision Decide(string platform, string connectionId, bool consume)
    {
        var now = clock.UtcNow;
        var circuits = new[] { Get("c:" + connectionId, "connection"), Get("p:" + platform, "platform") };
        var denials = circuits.Select(c => Evaluate(c, now)).Where(d => !d.Allowed).ToList();
        if (denials.Count > 0) return denials.OrderByDescending(d => d.RetryAt).First();

        var state = CircuitState.Closed;
        foreach (var c in circuits)
        {
            if (c.State == CircuitState.Closed) continue;
            state = CircuitState.HalfOpen;
            if (!consume) continue;
            c.State = CircuitState.HalfOpen;
            c.ProbeInFlight = true;
        }
        return new CircuitDecision(true, state, null, null, null);
    }

    private CircuitDecision Evaluate(Circuit c, DateTimeOffset now)
    {
        switch (c.State)
        {
            case CircuitState.Open when now < c.OpenUntil:
                return new CircuitDecision(false, CircuitState.Open, c.OpenUntil, c.Reason, c.Scope);
            case CircuitState.HalfOpen when c.ProbeInFlight:
                return new CircuitDecision(false, CircuitState.HalfOpen, now + options.HalfOpenRetry, "Đang thử lại kết nối.", c.Scope);
            default:
                return new CircuitDecision(true, c.State, null, null, c.Scope);
        }
    }

    private Circuit Get(string key, string scope)
    {
        if (!_circuits.TryGetValue(key, out var c)) _circuits[key] = c = new Circuit(scope);
        return c;
    }
}
