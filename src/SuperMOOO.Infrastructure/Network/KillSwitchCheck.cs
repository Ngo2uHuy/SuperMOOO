namespace SuperMOOO.Infrastructure.Network;

/// <summary>
/// Cảnh báo kill-switch cho tuyến cả máy ở S23 (chỉ cảnh báo, không chặn). WireGuard trên Windows chỉ chặn mạng ngoài tunnel khi peer có
/// AllowedIPs = 0.0.0.0/0 (và ::/0 cho IPv6); chia nhỏ kiểu 0.0.0.0/1 + 128.0.0.0/1 thì không chặn, nên phải so đúng /0.
/// </summary>
public static class KillSwitchCheck
{
    public const string AppHint = "Bật Kill Switch trong ứng dụng VPN để chặn mạng khi VPN rớt.";

    /// <summary>null = không có gì cần cảnh báo (hoặc loại tuyến không phải VPN cả máy).</summary>
    public static string? Warning(string kind, string? reference) => kind switch
    {
        NetworkRoute.WireGuard => string.IsNullOrWhiteSpace(reference) ? null : WireGuardWarning(reference.Trim()),
        NetworkRoute.WindowsVpn or NetworkRoute.ManualVpn => AppHint,
        _ => null
    };

    public static string? WireGuardWarning(string confPath)
    {
        string[] lines;
        try { lines = File.ReadAllLines(confPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Không đọc được file WireGuard để kiểm kill-switch: {e.Message}";
        }

        var section = "";
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var hasV6 = false;
        foreach (var raw in lines)
        {
            var line = raw.Split('#', 2)[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var values = line[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase) && key.Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase))
                allowed.UnionWith(values);
            else if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase) && key.Equals("Address", StringComparison.OrdinalIgnoreCase))
                hasV6 |= values.Any(v => v.Contains(':'));
        }

        var missing = new List<string>();
        if (!allowed.Contains("0.0.0.0/0")) missing.Add("0.0.0.0/0");
        if (hasV6 && !allowed.Contains("::/0")) missing.Add("::/0");
        return missing.Count == 0 ? null
            : $"VPN này không chặn mạng khi rớt (thiếu AllowedIPs = {string.Join(", ", missing)}), có thể lộ IP thật nếu VPN rớt giữa lúc đăng.";
    }
}
