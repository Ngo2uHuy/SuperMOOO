using System.Diagnostics;

namespace SuperMOOO.Infrastructure.Network;

/// <param name="Blocked">Bị chặn vì VPN cả máy của tài khoản khác: không được mở trình duyệt khác thay thế.</param>
public sealed record BrowserLaunch(bool Ok, string Message, bool Blocked = false);

/// <summary>
/// Mở Edge hệ thống với hồ sơ riêng của tài khoản (--user-data-dir dưới DataRoot) để đăng nhập/xác minh thủ công.
/// Tuyến proxy thì thêm --proxy-server; tuyến VPN/thủ công chỉ nhắc bật VPN (không tự bật ở đây, chỉ chạy khi người dùng bấm).
/// Không mở khi VPN cả máy đang bật cho tài khoản khác. Cửa sổ đã mở thì không chặn được VPN bật sau đó (Edge sống lâu, không giữ khoá).
/// </summary>
[Service]
public sealed partial class AccountBrowser(AppPaths paths, NetworkRouteBroker broker, ISettingsStore settings, MachineRouteLock machineLock)
{
    public static string? FindEdge() =>
        new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "Microsoft", "Edge", "Application", "msedge.exe"))
            .FirstOrDefault(File.Exists);

    public string ProfileDir(string connectionId) => Path.Combine(paths.DataRoot, "browser", connectionId);

    public BrowserLaunch Open(string connectionId, AccountIpPolicy? policy, string url, bool reuseExisting = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new BrowserLaunch(false, "Địa chỉ mở trình duyệt phải là HTTP hoặc HTTPS.");
        var edge = FindEdge();
        if (edge is null) return new BrowserLaunch(false, "Không tìm thấy Microsoft Edge trên máy.");

        var args = new List<string> { $"--user-data-dir={ProfileDir(connectionId)}", "--no-first-run", "--no-default-browser-check", "--new-window", "--disable-background-mode" };
        var note = "";
        var route = policy is null ? null : broker.ListRoutes().FirstOrDefault(r => r.Id == policy.RouteId);
        if (policy is not null && route is null)
            return new BrowserLaunch(false, "Tuyến mạng của tài khoản đã bị xoá. Hãy chọn lại tuyến trước khi mở trình duyệt.");
        if (route is { Enabled: false })
            return new BrowserLaunch(false, "Tuyến mạng của tài khoản đang tắt.");
        if (machineLock.ActiveWriter is { } vpn && vpn.Owner != connectionId && vpn.RouteId != route?.Id)
            return new BrowserLaunch(false, $"Đang có VPN cả máy '{vpn.Name}' bật cho tài khoản khác; chờ VPN tắt để không lộ IP.", true);
        if (route is { Kind: NetworkRoute.Proxy } && route.Ref is { } id)
        {
            var profile = settings.Get<NetworkSettings>("network").Profiles.FirstOrDefault(p => p.Id == id);
            if (profile is null) return new BrowserLaunch(false, $"Không có hồ sơ proxy '{id}' của tuyến '{route.Name}'.");
            var socks = profile.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
            if (socks && profile.Username is { Length: > 0 })
                return new BrowserLaunch(false, "Edge không hỗ trợ SOCKS5 có mật khẩu. Dùng proxy HTTP/HTTPS, hoặc proxy SOCKS5 không mật khẩu.");
            args.Add($"--proxy-server={profile.Scheme}://{profile.Host}:{profile.Port}");
            if (profile.Username is { Length: > 0 }) note = " Edge sẽ hỏi tên đăng nhập/mật khẩu proxy.";
        }
        else if (route is { Kind: NetworkRoute.WindowsVpn or NetworkRoute.WireGuard or NetworkRoute.ManualVpn })
            note = $" Hãy bật VPN '{route.Name}' trước khi đăng nhập.";

        var routeKey = (route?.Id ?? "") + "|" + string.Join("|", args);
        lock (_windowLock)
        {
            if (_sessions.TryGetValue(connectionId, out var session) && session.Process is { HasExited: false })
            {
                if (session.RouteKey != routeKey)
                    return new(false, "Tuyến mạng đã thay đổi. Đóng các cửa sổ Edge của tài khoản rồi mở lại để áp dụng tuyến mới.");
                if (reuseExisting && FindWindows().GetValueOrDefault(connectionId)?.Count > 0)
                    return new(true, "Tài khoản đã có cửa sổ đang mở." + note);
            }
        }
        Directory.CreateDirectory(ProfileDir(connectionId));
        var psi = new ProcessStartInfo(edge) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(url);
        var process = Process.Start(psi);
        if (process is null) return new BrowserLaunch(false, "Không khởi động được Microsoft Edge.");
        TrackBrowser(connectionId, process, routeKey);
        return new BrowserLaunch(true, "Đã mở Edge của tài khoản." + note);
    }
}
