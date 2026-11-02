using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperMOOO.Core.Models;

/// <summary>Kind: direct | proxy | windows_vpn | wireguard | manual_vpn. Ref: id hồ sơ proxy / tên VPN Windows / đường dẫn .conf WireGuard / tên app VPN thủ công.</summary>
public sealed record NetworkRoute(string Id, string Name, string Kind, string? Ref = null, string? ExpectedCountry = null, bool Enabled = true)
{
    public const string Direct = "direct", Proxy = "proxy", WindowsVpn = "windows_vpn", WireGuard = "wireguard", ManualVpn = "manual_vpn";

    /// <summary>Tuyến cả máy: chỉ một tài khoản dùng tại một thời điểm.</summary>
    [JsonIgnore]
    public bool IsMachineWide => Kind is WindowsVpn or WireGuard;
}

public static class IpModes { public const string Pinned = "pinned", StableCountry = "stable_country"; }
public static class FailoverModes { public const string Strict = "strict", Ask = "ask", AutoSameCountry = "auto_same_country"; }
/// <summary>Khi IP ra ngoài khác quốc gia so với lần đăng thành công trước: hold = dời bài chờ người dùng chấp nhận, warn = chỉ ghi cảnh báo.</summary>
public static class CountryChangeModes { public const string Hold = "hold", Warn = "warn"; }

/// <summary>Chính sách IP của một tài khoản (ApprovedFallbackRouteId: tuyến dự phòng người dùng đã duyệt khi Failover = ask); serialize JSON để lưu vào pub_connections.ip_policy_json.</summary>
public sealed record AccountIpPolicy(string RouteId, IReadOnlyList<string>? FallbackRouteIds = null, string? CountryCode = null,
    string IpMode = IpModes.StableCountry, string? PinnedIp = null, string Failover = FailoverModes.Strict,
    string? ApprovedFallbackRouteId = null, string CountryChange = CountryChangeModes.Hold)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AccountIpPolicy? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AccountIpPolicy>(json, Json);
}

/// <summary>Tuyến đã bật và kiểm IP xong. Dispose để nhả khoá độc quyền và tắt tuyến nếu broker đã bật.</summary>
public sealed class RouteLease(Func<HttpMessageHandler> handlerFactory, string? egressIp, string? egressCountry, DateTimeOffset checkedAt,
    bool isExclusive, string routeId, Func<Task>? release = null, string? toolsProxy = null) : IAsyncDisposable, IDisposable
{
    private Func<Task>? _release = release;

    /// <summary>Proxy cho công cụ ngoài (yt-dlp --proxy); null = tuyến không qua proxy.</summary>
    public string? ToolsProxy { get; } = toolsProxy;

    public string RouteId { get; } = routeId;
    public string? EgressIp { get; } = egressIp;
    public string? EgressCountry { get; } = egressCountry;
    public DateTimeOffset CheckedAt { get; } = checkedAt;
    public bool IsExclusive { get; } = isExclusive;

    /// <summary>Handler mới mỗi lần gọi (HttpClient sở hữu và dispose nó).</summary>
    public HttpMessageHandler CreateHandler() => handlerFactory();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _release, null) is { } r) await r();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>IP/quốc gia thực tế không khớp policy (hoặc tuyến không dùng được); lớp trên dời job hoặc dùng fallback.</summary>
public sealed class RoutePolicyViolationException(string reason, string? routeId = null, string? egressIp = null, string? egressCountry = null)
    : Exception(reason)
{
    public string? RouteId { get; } = routeId;
    public string? EgressIp { get; } = egressIp;
    public string? EgressCountry { get; } = egressCountry;
}

/// <summary>Tuyến cả máy đang do tài khoản khác giữ (hoặc đang chờ tài khoản khác dùng mạng thường xong); lớp trên dời job ngắn rồi thử lại.</summary>
public sealed class RouteBusyException(string reason, string? routeId = null) : Exception(reason)
{
    public string? RouteId { get; } = routeId;
}

/// <summary>
/// Tuyến riêng cho các request crawl trong ngữ cảnh async hiện tại (S08 quét nguồn qua tuyến của S23); không có thì crawler đi tuyến chung của app.
/// </summary>
public static class CrawlRoute
{
    private static readonly AsyncLocal<RouteLease?> Ambient = new();

    public static RouteLease? Current => Ambient.Value;

    public static IDisposable Begin(RouteLease lease)
    {
        var previous = Ambient.Value;
        Ambient.Value = lease;
        return new Scope(previous);
    }

    private sealed class Scope(RouteLease? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
