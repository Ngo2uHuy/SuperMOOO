using System.Collections.Concurrent;
using System.Text.Json;

namespace SuperMOOO.Infrastructure.Network;

/// <summary>Tra mã quốc gia (ISO 2 ký tự) của một IP qua ip-api.com; cache 1 giờ. Lỗi mạng/hạn mức → null, không chặn luồng gọi.</summary>
[Service]
public sealed class GeoIpLookup
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, (string Code, DateTimeOffset At)> _cache = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(6) };

    public async Task<string?> CountryAsync(string ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        if (_cache.TryGetValue(ip, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl) return hit.Code;
        try
        {
            // Chỉ gửi IP ra dịch vụ ngoài, không gửi gì khác.
            var body = await _http.GetStringAsync($"http://ip-api.com/json/{Uri.EscapeDataString(ip)}?fields=status,countryCode", ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var st) && st.GetString() == "success"
                && root.TryGetProperty("countryCode", out var cc) && cc.GetString() is { Length: 2 } code)
            {
                code = code.ToUpperInvariant();
                _cache[ip] = (code, DateTimeOffset.UtcNow);
                return code;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { }
        return null;
    }
}
