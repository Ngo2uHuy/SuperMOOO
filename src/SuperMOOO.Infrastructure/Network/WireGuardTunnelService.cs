using System.Security.Principal;

namespace SuperMOOO.Infrastructure.Network;

/// <summary>Điều khiển WireGuard chính thức (người dùng tự cài) qua wireguard.exe; tunnel là dịch vụ Windows WireGuardTunnel$&lt;tên&gt;.</summary>
[Service]
public sealed class WireGuardTunnelService(IProcessRunner runner)
{
    public static string TunnelName(string confPath) => Path.GetFileNameWithoutExtension(confPath);

    public static string? FindExecutable() => new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
        .Select(f => Path.Combine(Environment.GetFolderPath(f), "WireGuard", "wireguard.exe")).FirstOrDefault(File.Exists);

    public async Task<bool> IsRunningAsync(string tunnelName, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(new ProcessSpec("sc.exe", ["query", $"WireGuardTunnel${tunnelName}"]), null, ct);
        return result.ExitCode == 0 && result.OutputTail.Any(l => l.Contains("RUNNING", StringComparison.OrdinalIgnoreCase));
    }

    public async Task InstallAsync(string confPath, CancellationToken ct = default)
    {
        if (!File.Exists(confPath)) throw new FileNotFoundException($"Không thấy file cấu hình WireGuard: {confPath}");
        await RunAsync(["/installtunnelservice", confPath], "bật", ct);
    }

    public Task UninstallAsync(string tunnelName, CancellationToken ct = default) => RunAsync(["/uninstalltunnelservice", tunnelName], "tắt", ct);

    private async Task RunAsync(string[] args, string action, CancellationToken ct)
    {
        var exe = FindExecutable() ?? throw new InvalidOperationException("Chưa cài WireGuard (không thấy wireguard.exe trong Program Files). Hãy cài WireGuard chính thức rồi thử lại.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException($"Cần chạy SuperMOOO bằng quyền quản trị (Run as administrator) để {action} tunnel WireGuard.");

        var result = await runner.RunAsync(new ProcessSpec(exe, args), null, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"WireGuard {action} tunnel thất bại (mã {result.ExitCode}): {string.Join(" ", result.OutputTail.TakeLast(3))}");
    }
}
