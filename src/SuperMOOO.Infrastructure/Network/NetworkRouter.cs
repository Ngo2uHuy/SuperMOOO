using System.Net;

namespace SuperMOOO.Infrastructure.Network;

/// <summary>
/// Nguồn duy nhất quyết định HTTP ra ngoài đi trực tiếp, qua proxy hay VPN Windows. Trạng thái chỉ giữ trong phiên:
/// mỗi lần mở app bắt đầu ở "direct" để không âm thầm đi qua proxy đã chết.
/// </summary>
[Service(As = [typeof(INetworkRouter)])]
public sealed class NetworkRouter(ISettingsStore settings, ISecretStore secrets, WindowsVpnService vpn, IpVerifier verifier) : INetworkRouter
{
    // .NET ném NullReferenceException trong HttpConnectionPool.HandleAltSvc khi máy chủ (vd một số site) gửi header Alt-Svc h3; app không cần HTTP/3.
    static NetworkRouter() => AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http3Support", false);

    private volatile RouteState _state = new(new RouteSnapshot("direct", null, null, DateTimeOffset.UtcNow), null, null);

    public event EventHandler<RouteSnapshot>? Changed;

    public RouteSnapshot Current => _state.Snapshot;

    public HttpMessageHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        if (_state.Proxy is { } proxy)
            handler.Proxy = new WebProxy(proxy) { Credentials = _state.Credential };
        else
            handler.UseProxy = false;
        return handler;
    }

    public string? ProxyUriForTools()
    {
        if (_state.Proxy is not { } proxy) return null;
        if (_state.Credential is not { } c) return proxy.AbsoluteUri.TrimEnd('/');
        return $"{proxy.Scheme}://{Uri.EscapeDataString(c.UserName)}:{Uri.EscapeDataString(c.Password)}@{proxy.Authority}";
    }

    public async Task ConnectAsync(string profileId, CancellationToken ct = default)
    {
        var profile = settings.Get<NetworkSettings>("network").Profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new KeyNotFoundException($"Không có hồ sơ proxy '{profileId}'.");
        var password = await secrets.GetAsync(ProxyProfileService.PasswordKey(profileId), ct);
        var credential = profile.Username is { Length: > 0 } user ? new NetworkCredential(user, password ?? "") : null;
        var uri = new Uri($"{profile.Scheme}://{profile.Host}:{profile.Port}");
        Apply(new RouteState(new RouteSnapshot("proxy", uri.AbsoluteUri, null, DateTimeOffset.UtcNow), uri, credential));
    }

    public async Task ConnectVpnAsync(string name, CancellationToken ct = default)
    {
        await vpn.ConnectAsync(name, ct);
        Apply(new RouteState(new RouteSnapshot("vpn", null, name, DateTimeOffset.UtcNow), null, null));
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_state.Snapshot.VpnName is { } name) await vpn.DisconnectAsync(name, ct);
        Apply(new RouteState(new RouteSnapshot("direct", null, null, DateTimeOffset.UtcNow), null, null));
    }

    public async Task<IpCheckResult> VerifyAsync(CancellationToken ct)
    {
        using var client = new HttpClient(CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        return await verifier.VerifyAsync(client, ct);
    }

    private void Apply(RouteState state)
    {
        _state = state;
        Changed?.Invoke(this, state.Snapshot);
    }

    private sealed record RouteState(RouteSnapshot Snapshot, Uri? Proxy, NetworkCredential? Credential);
}
