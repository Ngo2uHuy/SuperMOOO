namespace SuperMOOO.Core.Abstractions;

/// <summary>Tuyến mạng đã cấp cho một tài khoản đăng bài. Dispose khi xong (VPN Windows là tuyến cả máy nên độc quyền).</summary>
/// <param name="Handler">Handler HTTP đi đúng tuyến (proxy/VPN/trực tiếp); người nhận dựng HttpClient và tự dispose.</param>
/// <param name="RouteKind">direct | proxy | vpn.</param>
/// <param name="EgressIp">IP ra ngoài đã kiểm (null = chưa kiểm).</param>
/// <param name="reverify">Kiểm lại IP/quốc gia qua chính tuyến này (tuyến cả máy); null = không cần kiểm lại.</param>
/// <param name="shared">Khoá đọc chặn VPN cả máy của tài khoản khác bật giữa chừng; giữ tới Dispose nhưng handler vẫn dùng chung được.</param>
public sealed class AccountRouteLease(HttpMessageHandler handler, string routeKind, string? routeRef, string? egressIp, string? egressCountry,
    DateTimeOffset? ipCheckedAt, IDisposable? release = null, Func<CancellationToken, Task>? reverify = null, IDisposable? shared = null) : IDisposable
{
    private IDisposable? _shared = shared;

    public Func<CancellationToken, Task>? Reverify { get; } = reverify;

    public HttpMessageHandler Handler { get; } = handler;
    public string RouteKind { get; } = routeKind;
    public string? RouteRef { get; } = routeRef;
    public string? EgressIp { get; } = egressIp;
    public string? EgressCountry { get; } = egressCountry;
    public DateTimeOffset? IpCheckedAt { get; } = ipCheckedAt;

    /// <summary>Tuyến cả máy đang được giữ độc quyền: người nhận phải giữ lease suốt thao tác rồi Dispose, không dùng lại handler.</summary>
    public bool HoldsRoute => release is not null;

    public void Dispose()
    {
        try { release?.Dispose(); }
        finally { Interlocked.Exchange(ref _shared, null)?.Dispose(); }
    }
}

/// <summary>Tuyến mạng không dùng được lúc này (VPN rớt, IP sai quốc gia, tuyến độc quyền đang bận): dời job tới <see cref="RetryAt"/>.</summary>
public sealed class AccountRouteUnavailableException(string message, DateTimeOffset retryAt) : Exception(message)
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}

/// <summary>Cấp tuyến mạng cho một tài khoản (connection). Không đạt (VPN rớt…) thì ném <see cref="AccountRouteUnavailableException"/>, không gửi request.</summary>
public interface IAccountRoute
{
    Task<AccountRouteLease> AcquireAsync(string connectionId, CancellationToken ct);
}

public sealed record ProductionOrder(string RequestId, string Topic, string Language, string Aspect, int? TargetDurationSec, string? Notes,
    string? CampaignId);

/// <summary>Pipeline nội bộ sản xuất video theo yêu cầu của chiến dịch (Talkshow 16:9, Whiteboard 16:9/9:16).</summary>
public interface IContentProducer
{
    /// <summary>Khoá producer lưu ở production_requests.producer.</summary>
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<string> SupportedAspects { get; }
    /// <summary>Xếp job sản xuất với owner "campaign:&lt;id&gt;"; trả id job. Chống trùng theo <see cref="ProductionOrder.RequestId"/>.</summary>
    Task<string> EnqueueAsync(ProductionOrder order, CancellationToken ct);
}

public enum FrameSampleStatus { Ok, Unavailable, Failed }

/// <param name="FrameHashes">dHash 64 bit mỗi khung, theo thứ tự thời gian.</param>
public sealed record FrameSampleResult(FrameSampleStatus Status, IReadOnlyList<ulong>? FrameHashes, string? Message);

/// <summary>Lấy khung hình (ffmpeg có lease media) để tính chữ ký gần trùng.</summary>
public interface IFrameSampler
{
    Task<FrameSampleResult> SampleAsync(string videoPath, double durationSeconds, int frameCount, CancellationToken ct);
}
