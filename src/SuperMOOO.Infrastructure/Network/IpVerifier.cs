using System.Text.Json;

namespace SuperMOOO.Infrastructure.Network;

/// <summary>Hỏi IP và quốc gia thật mà máy chủ bên ngoài nhìn thấy, thử lần lượt từng dịch vụ.</summary>
[Service]
public sealed class IpVerifier
{
    private static readonly (string Url, string Ip, string Country)[] Endpoints =
    [
        ("https://ipwho.is/", "ip", "country"),
        ("https://ipapi.co/json/", "ip", "country_name")
    ];

    public async Task<IpCheckResult> VerifyAsync(HttpClient client, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var (url, ipField, countryField) in Endpoints)
        {
            try
            {
                using var response = await client.GetAsync(url, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (response.IsSuccessStatusCode && root.TryGetProperty(ipField, out var ip) && ip.GetString() is { Length: > 0 } address)
                    return new IpCheckResult(true, address,
                        root.TryGetProperty(countryField, out var country) ? country.GetString() : null, null);
                errors.Add($"{new Uri(url).Host}: HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add($"{new Uri(url).Host}: {ex.Message}");
            }
        }

        return new IpCheckResult(false, null, null, string.Join("; ", errors));
    }
}
