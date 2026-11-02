using System.Net;
using Microsoft.Extensions.Logging;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Infrastructure.Network;

public static class ProxyHandlerFactory
{
    public static SocketsHttpHandler Create(ProxyProfile profile, string? password)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), ConnectTimeout = TimeSpan.FromSeconds(15) };
        handler.Proxy = new WebProxy(new Uri($"{profile.Scheme}://{profile.Host}:{profile.Port}"))
        {
            Credentials = profile.Username is { Length: > 0 } user ? new NetworkCredential(user, password ?? "") : null
        };
        return handler;
    }
}

/// <summary>
/// Cấp tuyến mạng cho tài khoản đăng bài. Tài khoản có chính sách IP (ip_policy_json, hoặc route_profile_id trỏ tới một tuyến mạng) đi qua
/// <see cref="NetworkRouteBroker"/>: bật tuyến, kiểm IP/quốc gia, giữ lease tới khi người nhận Dispose (tuyến cả máy độc quyền).
/// Không có tuyến riêng thì giữ hành vi cũ: tuyến chung của app hoặc proxy riêng (RouteProfileId); cổng VPN đóng thì không cấp.
/// Mọi lease không phải tuyến cả máy đều giữ khoá ĐỌC của <see cref="MachineRouteLock"/> tới Dispose: VPN cả máy của tài khoản khác không được bật
/// giữa chừng làm lộ IP quốc gia khác.
/// </summary>
[Service(As = [typeof(IAccountRoute)])]
public sealed class AccountRoute(IConnectionRepository connections, INetworkRouter router, NetworkGate gate, ISettingsStore settings,
    ISecretStore secrets, IRateLimiter limiter, ICircuitBreaker breaker, IClock clock, NetworkRouteBroker broker,
    IActionRequiredRepository actions, MachineRouteLock machineLock, ILogger<AccountRoute> log) : IAccountRoute
{
    private static readonly TimeSpan GateRetry = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ViolationRetry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AskRetry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(1);

    public async Task<AccountRouteLease> AcquireAsync(string connectionId, CancellationToken ct)
    {
        var connection = await connections.GetAsync(connectionId, ct)
            ?? throw new InvalidOperationException($"Không tìm thấy tài khoản '{connectionId}'.");
        var routes = broker.ListRoutes();
        var policy = AccountIpPolicy.FromJson(connection.IpPolicyJson)
            ?? (connection.RouteProfileId is { } rid && routes.Any(r => r.Id == rid) ? new AccountIpPolicy(rid) : null);
        // Tài khoản thuộc hồ sơ thị trường có quốc gia: luôn kiểm IP ra ngoài trước khi đăng, kể cả khi không có tuyến riêng.
        var market = settings.Get<MarketSettings>(MarketSettings.Section).RequirementFor(connection.Id);
        if (policy is not null) return await AcquireByPolicyAsync(connection, policy, routes, market, ct);

        var check = gate.Check();
        if (!check.Available)
            throw new AccountRouteUnavailableException(check.Detail ?? "VPN chưa kết nối.", clock.UtcNow + GateRetry);

        MachineRouteLock.Hold shared;
        try { shared = await machineLock.EnterReadAsync(connection.Id, BusyWait, ct); }
        catch (RouteBusyException ex) { throw new AccountRouteUnavailableException(ex.Message, clock.UtcNow + BusyRetry); }
        try { return await AcquireSharedAsync(connection, market, shared, ct); }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private async Task<AccountRouteLease> AcquireSharedAsync(SocialConnection connection, MarketRequirement? market, MachineRouteLock.Hold shared,
        CancellationToken ct)
    {
        Func<HttpMessageHandler> factory;
        string kind;
        string? routeRef;
        if (connection.RouteProfileId is null)
        {
            factory = router.CreateHandler;
            kind = router.Current.Mode;
            routeRef = router.Current.VpnName ?? router.Current.ProxyUri;
        }
        else
        {
            var profile = settings.Get<NetworkSettings>("network").Profiles.FirstOrDefault(p => p.Id == connection.RouteProfileId)
                ?? throw new AccountRouteUnavailableException($"Không tìm thấy tuyến '{connection.RouteProfileId}' của tài khoản.", clock.UtcNow + GateRetry);
            var password = await secrets.GetAsync(ProxyProfileService.PasswordKey(profile.Id), ct);
            factory = () => ProxyHandlerFactory.Create(profile, password);
            kind = "proxy";
            routeRef = profile.Id;
        }

        (string? Ip, string? Country) egress = (null, null);
        DateTimeOffset? checkedAt = null;
        if (market is not null)
        {
            try { egress = await broker.VerifyMarketAsync(factory, market, ct); }
            catch (RoutePolicyViolationException ex) { throw await ViolationAsync(connection, ex, ct); }
            checkedAt = clock.UtcNow;
            await actions.ResolveByRefAsync(ActionKinds.VpnDown, connection.Id, ct);
        }

        var handler = new ResilienceHandler(connection.Platform.ToString(), connection.Id, limiter, breaker, clock) { InnerHandler = factory() };
        return new AccountRouteLease(handler, kind, routeRef, egress.Ip, egress.Country, checkedAt, shared: shared);
    }

    private async Task<AccountRouteLease> AcquireByPolicyAsync(SocialConnection connection, AccountIpPolicy policy,
        IReadOnlyList<NetworkRoute> routes, MarketRequirement? market, CancellationToken ct)
    {
        RouteLease lease;
        try
        {
            try { lease = await broker.AcquireAsync(policy, ct, BusyWait, market, connection.Id); }
            catch (RoutePolicyViolationException ex) when (policy.Failover == FailoverModes.Ask && policy.FallbackRouteIds is { Count: > 0 } fallbacks)
            {
                var approved = policy.ApprovedFallbackRouteId is { } id && fallbacks.Contains(id) ? routes.FirstOrDefault(r => r.Id == id) : null;
                if (approved is null)
                {
                    var names = string.Join(", ", fallbacks.Select(f => routes.FirstOrDefault(r => r.Id == f)?.Name ?? f));
                    await actions.RaiseAsync(ActionKinds.RouteFailoverAsk, connection.Id, $"Duyệt tuyến dự phòng cho \"{connection.DisplayName}\"",
                        $"{ex.Message} Tuyến dự phòng: {names}. Duyệt ở màn Tài khoản & kênh (S23) để đăng tiếp.", ct);
                    throw new AccountRouteUnavailableException($"{ex.Message} Chờ bạn duyệt tuyến dự phòng.", clock.UtcNow + AskRetry);
                }
                // Quốc gia/IP cố định của policy vẫn được kiểm trên tuyến dự phòng.
                lease = await broker.AcquireAsync(approved, policy, ct, BusyWait, market, connection.Id);
            }
        }
        catch (RouteBusyException ex)
        {
            throw new AccountRouteUnavailableException(ex.Message, clock.UtcNow + BusyRetry);
        }
        catch (RoutePolicyViolationException ex)
        {
            throw await ViolationAsync(connection, ex, ct);
        }

        try { return await WrapAsync(connection, policy, routes, market, lease, ct); }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private async Task<AccountRouteLease> WrapAsync(SocialConnection connection, AccountIpPolicy policy, IReadOnlyList<NetworkRoute> routes,
        MarketRequirement? market, RouteLease lease, CancellationToken ct)
    {
        await actions.ResolveByRefAsync(ActionKinds.VpnDown, connection.Id, ct);
        if (lease.RouteId == policy.RouteId)
        {
            await actions.ResolveByRefAsync(ActionKinds.RouteFailoverAsk, connection.Id, ct);
            // Tuyến chính dùng lại được: bỏ duyệt dự phòng cũ để lần sau phải hỏi lại.
            if (policy.ApprovedFallbackRouteId is not null)
            {
                connection.IpPolicyJson = (policy with { ApprovedFallbackRouteId = null }).ToJson();
                await connections.UpsertAsync(connection, ct);
            }
        }

        var route = routes.FirstOrDefault(r => r.Id == lease.RouteId);
        var kind = route?.Kind switch { NetworkRoute.Direct => "direct", NetworkRoute.Proxy => "proxy", _ => "vpn" };
        HttpMessageHandler handler = new ResilienceHandler(connection.Platform.ToString(), connection.Id, limiter, breaker, clock) { InnerHandler = lease.CreateHandler() };
        // Tuyến cả máy: giao lease cho người nhận giữ tới hết lượt upload (handler độc quyền); tuyến thường giữ khoá ĐỌC, handler dùng chung được.
        IDisposable? release = lease.IsExclusive ? lease : null;

        Func<CancellationToken, Task>? reverify = null;
        if (route is { Kind: NetworkRoute.WindowsVpn or NetworkRoute.WireGuard or NetworkRoute.ManualVpn })
        {
            reverify = async c =>
            {
                try { await broker.ReverifyAsync(route, policy, lease, market, c); }
                catch (RoutePolicyViolationException ex) { throw await ViolationAsync(connection, ex, c); }
            };
            // VPN bật tay không có API cục bộ để canh: chỉ dựa vào kiểm lại IP trước bước đăng cuối.
            if (lease.IsExclusive && broker.StatusProbe(route) is { } probe)
            {
                var message = $"VPN '{route.Name}' rớt giữa lúc đăng; đã dừng tải lên để tránh lộ IP. Bài được dời.";
                handler = new RouteWatchHandler(probe, () => new AccountRouteUnavailableException(message, clock.UtcNow + GateRetry),
                    () => _ = RaiseRouteLostAsync(connection, message)) { InnerHandler = handler };
            }
        }
        return new AccountRouteLease(handler, kind, lease.RouteId, lease.EgressIp, lease.EgressCountry, lease.CheckedAt, release, reverify,
            lease.IsExclusive ? null : lease);
    }

    private async Task RaiseRouteLostAsync(SocialConnection connection, string message)
    {
        // Gọi từ vòng canh nền, không ai chờ: lỗi ghi log thay vì thành ngoại lệ không quan sát.
        try { await actions.RaiseAsync(ActionKinds.VpnDown, connection.Id, $"VPN của \"{connection.DisplayName}\" rớt", message, CancellationToken.None); }
        catch (Exception ex) { log.LogWarning(ex, "Không ghi được việc cần làm VPN rớt cho tài khoản {Id}", connection.Id); }
    }

    private async Task<AccountRouteUnavailableException> ViolationAsync(SocialConnection connection, RoutePolicyViolationException ex, CancellationToken ct)
    {
        await actions.RaiseAsync(ActionKinds.VpnDown, connection.Id, $"Tuyến mạng của \"{connection.DisplayName}\" không đạt",
            $"{ex.Message} Bài được dời, không đăng cho tới khi tuyến/IP đúng chính sách.", ct);
        return new AccountRouteUnavailableException(ex.Message, clock.UtcNow + ViolationRetry);
    }
}
