using System.Net.NetworkInformation;

namespace SuperMOOO.Infrastructure.Network;

/// <summary>VPN đã cấu hình sẵn trong Windows: liệt kê bằng Get-VpnConnection, nối/ngắt bằng rasdial.</summary>
[Service]
public sealed class WindowsVpnService(IProcessRunner runner)
{
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(new ProcessSpec("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Get-VpnConnection | ForEach-Object { $_.Name }"]), null, ct);
        return result.ExitCode == 0 ? result.OutputTail.Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];
    }

    public async Task<bool> IsConnectedAsync(string name, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(new ProcessSpec("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"(Get-VpnConnection -Name '{name.Replace("'", "''")}').ConnectionStatus"]), null, ct);
        return result.ExitCode == 0 && result.OutputTail.Any(l => l.Trim().Equals("Connected", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adapter mang tên VPN đang Up (kiểm trong tiến trình, không mở powershell).</summary>
    public static bool IsAdapterUp(string name) => NetworkInterface.GetAllNetworkInterfaces()
        .Any(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.OperationalStatus == OperationalStatus.Up
                  && (string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(n.Description, name, StringComparison.OrdinalIgnoreCase)));

    public Task ConnectAsync(string name, CancellationToken ct = default) => RasdialAsync(name, [], ct);

    public Task DisconnectAsync(string name, CancellationToken ct = default) => RasdialAsync(name, ["/disconnect"], ct);

    private async Task RasdialAsync(string name, string[] extra, CancellationToken ct)
    {
        // rasdial coi tham số bắt đầu bằng '/' là công tắc, nên không cho tên VPN trùng dạng đó.
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.StartsWith('-'))
            throw new ArgumentException($"Tên VPN không hợp lệ: '{name}'.", nameof(name));

        var result = await runner.RunAsync(new ProcessSpec("rasdial.exe", [name, .. extra]), null, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"rasdial thất bại (mã {result.ExitCode}): {string.Join(" ", result.OutputTail.TakeLast(3))}");
    }
}
