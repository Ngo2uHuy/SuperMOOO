using System.Diagnostics;

namespace SuperMOOO.Infrastructure.Network;

public sealed record ProxyTestResult(bool Ok, string? Ip, string? Country, long LatencyMs, string? Error);

/// <summary>Kiểm tra một hồ sơ proxy (hoặc tuyến hiện tại) bằng cách hỏi IP ra ngoài; chỉ chạy khi người dùng bấm.</summary>
[Service]
public sealed class ProxyTester(ISettingsStore settings, ISecretStore secrets, NetworkGate gate, INetworkRouter router, IpVerifier verifier)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<ProxyTestResult> TestAsync(string? profileId, CancellationToken ct = default)
    {
        HttpMessageHandler handler;
        if (profileId is null)
        {
            var check = gate.Check();
            if (!check.Available) return new ProxyTestResult(false, null, null, 0, check.Detail ?? "VPN chưa kết nối.");
            handler = router.CreateHandler();
        }
        else
        {
            var profile = settings.Get<NetworkSettings>("network").Profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile is null) return new ProxyTestResult(false, null, null, 0, $"Không có hồ sơ proxy '{profileId}'.");
            handler = ProxyHandlerFactory.Create(profile, await secrets.GetAsync(ProxyProfileService.PasswordKey(profileId), ct));
        }

        using var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout };
        var sw = Stopwatch.StartNew();
        var result = await verifier.VerifyAsync(client, ct);
        return new ProxyTestResult(result.Ok, result.Ip, result.Country, sw.ElapsedMilliseconds, result.Message);
    }
}
