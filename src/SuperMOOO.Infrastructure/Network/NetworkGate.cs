using System.Net.NetworkInformation;

namespace SuperMOOO.Infrastructure.Network;

public sealed record NetworkGateResult(bool Available, string? Detail);

/// <summary>Cổng chặn gửi khi tuyến hiện tại là VPN mà adapter đã rớt (fail-closed); proxy/direct luôn mở.</summary>
[Service]
public sealed class NetworkGate(INetworkRouter router)
{
    public NetworkGateResult Check()
    {
        if (router.Current.VpnName is not { } name) return new NetworkGateResult(true, null);

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && (string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n.Description, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (adapters.Count == 0) return new NetworkGateResult(false, $"Không tìm thấy adapter VPN \"{name}\".");
        return adapters.Any(n => n.OperationalStatus == OperationalStatus.Up)
            ? new NetworkGateResult(true, null)
            : new NetworkGateResult(false, $"Adapter VPN \"{name}\" không hoạt động.");
    }
}
