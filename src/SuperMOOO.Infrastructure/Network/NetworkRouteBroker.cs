using System.Net;

namespace SuperMOOO.Infrastructure.Network;

/// <summary>
/// Cấp tuyến mạng riêng cho từng tài khoản. Proxy/direct/VPN bật tay chạy song song (handler riêng, không đụng route toàn app) và giữ khoá ĐỌC
/// của <see cref="MachineRouteLock"/>; VPN Windows/WireGuard là tuyến cả máy nên giữ khoá GHI (độc quyền). Cả hai giữ tới khi lease được Dispose,
/// vì khi một VPN cả máy bật, HTTP trực tiếp của app cũng đi qua VPN đó (không thể tách) và kết nối tới proxy có thể đứt.
/// </summary>
[Service]
public sealed class NetworkRouteBroker(ISettingsStore settings, ISecretStore secrets, WindowsVpnService vpn, WireGuardTunnelService wireGuard,
    IpVerifier verifier, GeoIpLookup geo, MachineRouteLock machineLock)
{

    public IReadOnlyList<NetworkRoute> ListRoutes() => settings.Get<NetworkSettings>("network").Routes;

    /// <summary>Thử tuyến chính của policy; Failover=auto_same_country thì thử tiếp FallbackRouteIds (mỗi lease đều bị đối chiếu quốc gia của policy). Hết tuyến thì ném lỗi của tuyến đầu.</summary>
    /// <param name="market">Quốc gia của hồ sơ thị trường; chỉ áp khi policy và tuyến chưa đặt quốc gia riêng.</param>
    /// <param name="owner">Id tài khoản giữ lease: cùng tài khoản xin lại khi đang giữ thì vào lại ngay, không chờ.</param>
    public async Task<RouteLease> AcquireAsync(AccountIpPolicy policy, CancellationToken ct = default, TimeSpan? lockWait = null, MarketRequirement? market = null,
        string? owner = null)
    {
        var routes = ListRoutes();
        var chain = new List<string> { policy.RouteId };
        if (policy.Failover == FailoverModes.AutoSameCountry && policy.FallbackRouteIds is { } fb) chain.AddRange(fb);

        RoutePolicyViolationException? first = null;
        foreach (var id in chain)
        {
            var route = routes.FirstOrDefault(r => r.Id == id);
            if (route is null || !route.Enabled)
            {
                first ??= new RoutePolicyViolationException($"Tuyến mạng '{id}' không tồn tại hoặc đang tắt.", id);
                continue;
            }
            try { return await AcquireAsync(route, policy, ct, lockWait, market, owner); }
            catch (RoutePolicyViolationException ex) { first ??= ex; }
        }
        throw first ?? new RoutePolicyViolationException("Không có tuyến mạng nào dùng được.", policy.RouteId);
    }

    public async Task<RouteLease> AcquireAsync(NetworkRoute route, AccountIpPolicy? policy, CancellationToken ct = default, TimeSpan? lockWait = null,
        MarketRequirement? market = null, string? owner = null)
    {
        if (!route.Enabled) throw new RoutePolicyViolationException($"Tuyến '{route.Name}' đang tắt.", route.Id);

        var exclusive = route.IsMachineWide;
        // lockWait != null: không chờ lâu để luồng đăng bài không bị chặn; quá hạn → RouteBusyException để lớp trên dời job.
        var wait = lockWait ?? Timeout.InfiniteTimeSpan;
        var held = exclusive ? await machineLock.EnterWriteAsync(owner, route, wait, ct) : await machineLock.EnterReadAsync(owner, wait, ct);
        Func<Task>? undo = null;
        try
        {
            Func<HttpMessageHandler> factory;
            string? toolsProxy = null;
            switch (route.Kind)
            {
                case NetworkRoute.Direct or NetworkRoute.ManualVpn:
                    factory = PlainHandler;
                    break;
                case NetworkRoute.Proxy:
                    (factory, toolsProxy) = await ProxyFactoryAsync(route, ct);
                    break;
                case NetworkRoute.WindowsVpn:
                    {
                        var name = Require(route);
                        if (!await vpn.IsConnectedAsync(name, ct))
                        {
                            await vpn.ConnectAsync(name, ct);
                            undo = () => vpn.DisconnectAsync(name, CancellationToken.None);
                        }
                        factory = PlainHandler;
                        break;
                    }
                case NetworkRoute.WireGuard:
                    {
                        var conf = Require(route);
                        var tunnel = WireGuardTunnelService.TunnelName(conf);
                        if (!await wireGuard.IsRunningAsync(tunnel, ct))
                        {
                            await wireGuard.InstallAsync(conf, ct);
                            undo = () => wireGuard.UninstallAsync(tunnel, CancellationToken.None);
                        }
                        factory = PlainHandler;
                        break;
                    }
                default:
                    throw new InvalidOperationException($"Loại tuyến không hỗ trợ: {route.Kind}");
            }

            var (ip, country) = await VerifyAsync(route, policy, factory, market, ct);
            // Tắt VPN khi lượt GHI cuối cùng nhả (cùng tài khoản có thể đang giữ lồng nhau), trước khi lease ĐỌC khác được chạy.
            if (undo is not null) held.OnLastRelease(undo);
            return new RouteLease(factory, ip, country, DateTimeOffset.UtcNow, exclusive, route.Id, () => held.DisposeAsync().AsTask(), toolsProxy);
        }
        catch
        {
            try { if (undo is not null) await undo(); }
            catch { /* giữ lỗi gốc */ }
            await held.DisposeAsync();
            throw;
        }
    }

    /// <summary>Kiểm IP/quốc gia của tài khoản không có tuyến riêng (tuyến chung của app hoặc proxy riêng) theo hồ sơ thị trường.</summary>
    public Task<(string? Ip, string? Country)> VerifyMarketAsync(Func<HttpMessageHandler> factory, MarketRequirement market, CancellationToken ct = default) =>
        VerifyAsync(new NetworkRoute("app", "Tuyến chung của app", NetworkRoute.Direct), null, factory, market, ct);

    /// <summary>
    /// Kiểm lại IP/quốc gia qua tuyến đang giữ (không xin lại khoá độc quyền), cùng quy tắc với lúc cấp tuyến; dùng ngay trước bước đăng cuối
    /// vì VPN cả máy có thể rớt giữa lúc upload và phần còn lại đi bằng IP thật.
    /// </summary>
    public async Task<(string? Ip, string? Country)> ReverifyAsync(NetworkRoute route, AccountIpPolicy? policy, RouteLease lease, MarketRequirement? market,
        CancellationToken ct)
    {
        try { return await VerifyAsync(route, policy, lease.CreateHandler, market, ct); }
        catch (RoutePolicyViolationException ex)
        {
            throw new RoutePolicyViolationException($"Kiểm lại IP ngay trước bước đăng cuối không đạt: {ex.Message}", ex.RouteId, ex.EgressIp, ex.EgressCountry);
        }
    }

    /// <summary>Hỏi trạng thái tuyến cả máy bằng API cục bộ (không gọi mạng); null = loại tuyến không có API cục bộ (VPN bật tay, proxy, direct).</summary>
    public Func<CancellationToken, Task<bool>>? StatusProbe(NetworkRoute route) => route.Kind switch
    {
        NetworkRoute.WireGuard when route.Ref is { Length: > 0 } conf => c => wireGuard.IsRunningAsync(WireGuardTunnelService.TunnelName(conf), c),
        // Adapter còn Up thì khỏi mở powershell mỗi nhịp canh; chỉ hỏi Get-VpnConnection khi adapter không thấy/không Up.
        NetworkRoute.WindowsVpn when route.Ref is { Length: > 0 } name => c => WindowsVpnService.IsAdapterUp(name) ? Task.FromResult(true) : vpn.IsConnectedAsync(name, c),
        _ => null
    };

    private async Task<(string? Ip, string? Country)> VerifyAsync(NetworkRoute route, AccountIpPolicy? policy, Func<HttpMessageHandler> factory,
        MarketRequirement? market, CancellationToken ct)
    {
        if (route.Kind == NetworkRoute.Direct && policy is null && market is null) return (null, null);

        using var client = new HttpClient(factory(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        var check = await verifier.VerifyAsync(client, ct);
        var manual = route.Kind == NetworkRoute.ManualVpn;
        // Quốc gia đặt riêng ở policy/tuyến được ưu tiên; S23 cảnh báo khi khác hồ sơ.
        var expected = policy?.CountryCode ?? route.ExpectedCountry ?? market?.Country;
        var byMarket = market is not null && policy?.CountryCode is null && route.ExpectedCountry is null;
        var hint = manual ? $" Hãy bật VPN '{route.Ref}'{(expected is { Length: > 0 } ? $" quốc gia {expected}" : "")}." : "";

        if (!check.Ok || check.Ip is not { Length: > 0 } ip)
            throw new RoutePolicyViolationException($"Không kiểm được IP qua tuyến '{route.Name}': {check.Message}.{hint}", route.Id);

        var country = await geo.CountryAsync(ip, ct);

        if (policy?.IpMode == IpModes.Pinned && policy.PinnedIp is { Length: > 0 } pinned && pinned != ip)
            throw new RoutePolicyViolationException($"IP hiện tại {ip} khác IP cố định {pinned}.{hint}", route.Id, ip, country);

        // Đã yêu cầu quốc gia thì không tra được cũng phải chặn: thà dời job còn hơn đăng nhầm quốc gia.
        if (expected is { Length: > 0 } && (manual || policy is null || policy.IpMode == IpModes.StableCountry))
        {
            if (byMarket && (country is null || !country.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                throw new RoutePolicyViolationException($"Tài khoản thuộc hồ sơ '{market!.ProfileName}' cần IP {expected}, IP hiện tại {ip} thuộc {country ?? "quốc gia không tra được"}. "
                    + "Gắn tuyến VPN/proxy đúng quốc gia ở Tài khoản & kênh.", route.Id, ip, country);
            if (country is null)
                throw new RoutePolicyViolationException($"Không tra được quốc gia của IP {ip}, cần {expected}.{hint}", route.Id, ip, null);
            if (!country.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new RoutePolicyViolationException($"IP {ip} thuộc quốc gia {country}, cần {expected}.{hint}", route.Id, ip, country);
        }

        return (ip, country);
    }

    private async Task<(Func<HttpMessageHandler> Factory, string ToolsProxy)> ProxyFactoryAsync(NetworkRoute route, CancellationToken ct)
    {
        var id = Require(route);
        var profile = settings.Get<NetworkSettings>("network").Profiles.FirstOrDefault(p => p.Id == id)
            ?? throw new RoutePolicyViolationException($"Không có hồ sơ proxy '{id}' cho tuyến '{route.Name}'.", route.Id);
        var password = await secrets.GetAsync(ProxyProfileService.PasswordKey(id), ct);
        var credential = profile.Username is { Length: > 0 } user ? new NetworkCredential(user, password ?? "") : null;
        var uri = new Uri($"{profile.Scheme}://{profile.Host}:{profile.Port}");
        // yt-dlp nhận proxy qua tham số --proxy nên cần URI có kèm tài khoản proxy.
        var tools = credential is null ? uri.AbsoluteUri.TrimEnd('/')
            : $"{uri.Scheme}://{Uri.EscapeDataString(credential.UserName)}:{Uri.EscapeDataString(credential.Password)}@{uri.Authority}";
        return (() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), Proxy = new WebProxy(uri) { Credentials = credential } }, tools);
    }

    private static HttpMessageHandler PlainHandler() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), UseProxy = false };

    private static string Require(NetworkRoute route) =>
        route.Ref is { Length: > 0 } r ? r : throw new RoutePolicyViolationException($"Tuyến '{route.Name}' chưa khai báo tham chiếu (Ref).", route.Id);
}
