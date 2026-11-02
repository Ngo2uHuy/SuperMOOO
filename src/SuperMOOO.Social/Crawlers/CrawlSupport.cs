using System.Globalization;
using System.Net;
using System.Text;
using SuperMOOO.Infrastructure.Network;
using SuperMOOO.Social.Platforms;

namespace SuperMOOO.Social.Crawlers;

public sealed record HttpReply(string Body, Uri FinalUri, HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers);

/// <summary>HTTP cho crawler: qua route mạng chung, timeout theo từng lời gọi, lỗi có kiểu (không nuốt, F37).</summary>
[Service]
public sealed class CrawlHttp(IHttpClientProvider clients, MachineRouteLock? routeLock = null)
{
    public const string ClientName = "social";
    public const string DesktopAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public async Task<HttpReply> SendAsync(string platform, HttpMethod method, string url, IReadOnlyDictionary<string, string>? headers = null,
        HttpContent? content = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        // Request mang cookie/token tài khoản đi tuyến chung: không chạy khi VPN cả máy đang bật cho tài khoản khác (phiên bị thấy đổi sang IP nước khác).
        await using var shared = routeLock is not null && CrawlRoute.Current is null && HasIdentity(headers) ? await EnterSharedAsync(routeLock, platform, ct) : null;
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopAgent);
        if (headers is not null)
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            // Luồng quét đang giữ tuyến riêng (S08): request đi đúng tuyến đó thay vì tuyến chung của app.
            using var routed = CrawlRoute.Current is { } lease ? new HttpClient(lease.CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan } : null;
            using var response = await (routed ?? clients.Get(ClientName)).SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode) throw Failure(platform, response.StatusCode, body);

            var reply = response.Headers.Concat(response.Content.Headers)
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            return new HttpReply(body, response.RequestMessage?.RequestUri ?? new Uri(url), response.StatusCode, reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(platform, ProviderErrorKind.Timeout, "Hết thời gian chờ phản hồi.");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(platform, ProviderErrorKind.Network, $"Lỗi mạng: {ex.Message}");
        }
    }

    public static bool HasIdentity(IReadOnlyDictionary<string, string>? headers) =>
        headers is not null && headers.Any(h => (h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                                                && !string.IsNullOrWhiteSpace(h.Value));

    /// <summary>Khoá ĐỌC cho traffic mang danh tính ngoài luồng đăng; VPN cả máy giữ quá lâu → lỗi mạng có kiểu.</summary>
    public static async Task<MachineRouteLock.Hold> EnterSharedAsync(MachineRouteLock routeLock, string platform, CancellationToken ct)
    {
        try { return await routeLock.EnterReadAsync(null, MachineRouteLock.IdentityWait, ct); }
        catch (RouteBusyException ex) { throw new ProviderException(platform, ProviderErrorKind.Network, ex.Message); }
    }

    public async Task<string> GetStringAsync(string platform, string url, IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null, CancellationToken ct = default) =>
        (await SendAsync(platform, HttpMethod.Get, url, headers, null, timeout, ct)).Body;

    public async Task<JsonNode> GetJsonAsync(string platform, string url, IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null, CancellationToken ct = default) =>
        ParseJson(platform, await GetStringAsync(platform, url, headers, timeout, ct));

    public static JsonNode ParseJson(string platform, string body)
    {
        try
        {
            return JsonNode.Parse(body) ?? throw new ProviderException(platform, ProviderErrorKind.BadResponse, "Phản hồi rỗng.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ProviderException(platform, ProviderErrorKind.BadResponse, "Phản hồi không phải JSON (có thể bị chặn chống bot).");
        }
    }

    private static ProviderException Failure(string platform, HttpStatusCode status, string body)
    {
        var kind = status switch
        {
            HttpStatusCode.Unauthorized => ProviderErrorKind.KeyInvalid,
            HttpStatusCode.Forbidden => ProviderErrorKind.Forbidden,
            HttpStatusCode.TooManyRequests => ProviderErrorKind.RateLimited,
            _ => ProviderErrorKind.BadResponse
        };
        // Trang chặn của Cloudflare/WAF là HTML dài, không đưa nguyên văn lên giao diện.
        var detail = body.TrimStart().StartsWith('<')
            ? body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) || body.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                ? "bị chặn chống bot (Cloudflare), thử lại sau hoặc đổi mạng/proxy."
                : "máy chủ trả về trang HTML thay vì dữ liệu."
            : body.Length > 240 ? body[..240] : body;
        return new ProviderException(platform, kind, $"HTTP {(int)status}: {detail}".Trim());
    }
}

/// <summary>Đọc cookie/token đã lưu theo quy ước secret `platform:&lt;id&gt;` (P6 ghi, P7 chỉ đọc).</summary>
[Service]
public sealed class PlatformCredentials(ISecretStore secrets)
{
    public async Task<string?> GetAsync(string platform, string name, CancellationToken ct = default)
    {
        var id = PlatformRegistry.NormalizeId(platform) ?? platform;
        var value = await secrets.GetAsync(PlatformSecrets.Key(id, name), ct);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public Task<string?> CookieAsync(string platform, CancellationToken ct = default) => GetAsync(platform, PlatformSecrets.Cookie, ct);

    public Task<string?> TokenAsync(string platform, CancellationToken ct = default) => GetAsync(platform, PlatformSecrets.Token, ct);

    /// <summary>Header Cookie/Authorization cho nền tảng, kèm header mặc định của crawler.</summary>
    public async Task<Dictionary<string, string>> HeadersAsync(string platform, IDictionary<string, string>? baseHeaders = null, CancellationToken ct = default)
    {
        var headers = new Dictionary<string, string>(baseHeaders ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (await CookieAsync(platform, ct) is { } cookie) headers["Cookie"] = cookie;
        if (await TokenAsync(platform, ct) is { } token) headers["Authorization"] = $"Bearer {token}";
        return headers;
    }
}

/// <summary>Đọc JSON mềm: bỏ qua kiểu sai thay vì ném, vì cấu trúc phản hồi của nền tảng hay đổi.</summary>
public static class JsonX
{
    public static JsonNode? At(JsonNode? node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node is JsonObject o) node = o[key];
            else return null;
        }

        return node;
    }

    public static string? Str(JsonNode? node, params string[] path) => At(node, path) switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s,
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    public static long? Long(JsonNode? node, params string[] path) => At(node, path) switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => (long)d,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
        _ => null
    };

    public static double? Double(JsonNode? node, params string[] path) => At(node, path) switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    public static IEnumerable<JsonObject> Objects(JsonNode? node, params string[] path) =>
        At(node, path) is JsonArray array ? array.OfType<JsonObject>() : [];

    /// <summary>Số kiểu "1.2K", "3.4万", "5M" → số nguyên.</summary>
    public static long? Compact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().Replace(",", "");
        var scale = 1d;
        if (t.EndsWith("万", StringComparison.Ordinal)) { scale = 10_000; t = t[..^1]; }
        else if (t.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { scale = 1_000; t = t[..^1]; }
        else if (t.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { scale = 1_000_000; t = t[..^1]; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (long)(n * scale) : null;
    }

    public static DateTimeOffset? FromUnixSeconds(long? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    /// <summary>"20240131" (yt-dlp upload_date) hoặc ISO 8601.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length == 8 && DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return new DateTimeOffset(d, TimeSpan.Zero);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso) ? iso : null;
    }

    public static string Quote(string value) => Uri.EscapeDataString(value);

    public static string Query(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in parameters.Where(p => p.Value is not null))
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v!));
        }

        return sb.ToString();
    }
}
