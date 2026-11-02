using System.Collections.Concurrent;
using SuperMOOO.Publishing.Contracts;

namespace SuperMOOO.Publishing.Http;

/// <summary>
/// Dispose khi xong lượt thao tác: nhả tuyến cả máy (VPN) nếu đang giữ độc quyền, hoặc nhả khoá ĐỌC chặn VPN cả máy của tài khoản khác;
/// client của tuyến thường được dùng chung nên không bị đóng.
/// </summary>
/// <param name="Reverify">Kiểm lại IP qua tuyến đang giữ (chỉ tuyến cả máy); null = bỏ qua.</param>
public sealed record ConnectionHttp(HttpClient Client, string RouteKind, string? RouteRef, string? EgressIp, string? EgressCountry,
    DateTimeOffset? IpCheckedAt, IDisposable? Release = null, Func<CancellationToken, Task>? Reverify = null) : IDisposable
{
    public void Dispose() => Release?.Dispose();
}

internal sealed class HeldRoute(HttpClient client, IDisposable lease) : IDisposable
{
    public void Dispose()
    {
        client.Dispose();
        lease.Dispose();
    }
}

/// <summary>
/// HttpClient cho đăng bài: theo tuyến của từng tài khoản (IAccountRoute: proxy/VPN + rate limit + ngắt mạch + Retry-After),
/// hoặc tuyến chung của app khi chưa có tài khoản (media host, đổi token lần đầu).
/// </summary>
[Service]
public sealed class PublishHttpClients(IAccountRoute route, IHttpClientProvider clients)
{
    private static readonly TimeSpan ShortUseHold = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<string, HttpClient> _byRoute = new();

    /// <summary>Tuyến chung của app (profileId được giữ để tương thích chữ ký cũ, chưa dùng).</summary>
    public Task<HttpClient> GetAsync(string? profileId, CancellationToken ct) => Task.FromResult(clients.Get("publish"));

    /// <summary>
    /// Client của tài khoản. Mỗi lần gọi đều xin tuyến (kiểm cổng VPN), nhưng giữ lại client cũ khi tuyến không đổi để không mở pool kết nối mới.
    /// Tuyến không dùng được → <see cref="AccountRouteUnavailableException"/>.
    /// </summary>
    public async Task<HttpClient> GetForConnectionAsync(string connectionId, CancellationToken ct)
    {
        var conn = await AcquireAsync(connectionId, ct);
        // Thao tác ngắn không có điểm kết thúc để nhả: tuyến cả máy (hoặc khoá ĐỌC của tuyến thường) được giữ một khoảng rồi tự nhả.
        if (conn.Release is not null) _ = Task.Delay(ShortUseHold).ContinueWith(_ => conn.Dispose(), TaskScheduler.Default);
        return conn.Client;
    }

    /// <summary>Như <see cref="GetForConnectionAsync"/>, kèm tuyến/IP thật để ghi vào lần thử đăng.</summary>
    public async Task<ConnectionHttp> AcquireAsync(string connectionId, CancellationToken ct)
    {
        var lease = await route.AcquireAsync(connectionId, ct);
        if (lease.HoldsRoute)
        {
            var own = new HttpClient(lease.Handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            return new ConnectionHttp(own, lease.RouteKind, lease.RouteRef, lease.EgressIp, lease.EgressCountry, lease.IpCheckedAt, new HeldRoute(own, lease),
                lease.Reverify);
        }
        var reverify = lease.Reverify;
        var http = await ClientForAsync(connectionId, lease);
        // lease vẫn giữ khoá ĐỌC tới khi người nhận Dispose.
        return new ConnectionHttp(http, lease.RouteKind, lease.RouteRef, lease.EgressIp, lease.EgressCountry, lease.IpCheckedAt, lease, reverify);
    }

    /// <summary>
    /// Kiểm lại IP/quốc gia ngay trước bước đăng cuối (sau khi upload media), cùng quy tắc với lúc cấp tuyến, qua chính tuyến đang giữ.
    /// Chỉ tuyến cả máy (VPN Windows/WireGuard/VPN bật tay); sai → <see cref="AccountRouteUnavailableException"/> để dời bài.
    /// </summary>
    public Task ReverifyAsync(ConnectionHttp connection, CancellationToken ct) => connection.Reverify?.Invoke(ct) ?? Task.CompletedTask;

    private Task<HttpClient> ClientForAsync(string connectionId, AccountRouteLease lease)
    {
        var key = $"{connectionId}|{lease.RouteKind}|{lease.RouteRef}";
        if (_byRoute.TryGetValue(key, out var cached))
        {
            lease.Handler.Dispose();
            return Task.FromResult(cached);
        }

        // Timeout vô hạn: connector tự đặt timeout theo từng request (upload chunk dài hơn lời gọi API).
        var client = new HttpClient(lease.Handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return Task.FromResult(_byRoute.GetOrAdd(key, client));
    }

    /// <summary>Kiểm tuyến của tài khoản trước thao tác người dùng bấm; không đạt → lỗi VpnDown hiện cho người dùng.</summary>
    public async Task EnsureRouteAsync(string connectionId, CancellationToken ct)
    {
        try { using var _ = await AcquireAsync(connectionId, ct); }
        catch (AccountRouteUnavailableException e) { throw new PublishingException(PublishingErrors.VpnDown, e.Message); }
    }
}
