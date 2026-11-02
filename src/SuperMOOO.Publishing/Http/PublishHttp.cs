using System.Net;
using System.Text.Json;

namespace SuperMOOO.Publishing.Http;

public sealed record PublishResponse(HttpStatusCode Status, string? Location, JsonNode? Json);

/// <summary>HTTP ra nền tảng đăng bài: timeout theo từng request (F36), lỗi quy về <see cref="ProviderException"/>, không bao giờ đưa URL/token vào thông báo.</summary>
[Service]
public sealed class PublishHttp(IHttpClientProvider clients)
{
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(30);

    public static string Query(params (string Key, string Value)[] pairs) =>
        string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    public static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs) =>
        new(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));

    public static HttpRequestMessage Request(HttpMethod method, string url, string? bearer = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        return request;
    }

    public async Task<PublishResponse> SendAsync(string provider, HttpRequestMessage request, TimeSpan? timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? ApiTimeout);
        try
        {
            using var response = await clients.Get("publish").SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var json = TryParse(body);
            if (!response.IsSuccessStatusCode) throw Map(provider, response.StatusCode, json, body);
            return new PublishResponse(response.StatusCode, response.Headers.Location?.OriginalString, json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(provider, ProviderErrorKind.Timeout, $"{provider}: quá thời gian chờ phản hồi.");
        }
        catch (HttpRequestException)
        {
            throw new ProviderException(provider, ProviderErrorKind.Network, $"{provider}: không kết nối được; kiểm tra mạng và trạng thái bài đăng.");
        }
    }

    private static JsonNode? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }

    private static ProviderException Map(string provider, HttpStatusCode status, JsonNode? json, string body)
    {
        var detail = ErrorMessage(json);
        var kind = status switch
        {
            HttpStatusCode.TooManyRequests => ProviderErrorKind.RateLimited,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                body.Contains("quotaExceeded", StringComparison.OrdinalIgnoreCase) ? ProviderErrorKind.QuotaExceeded : ProviderErrorKind.Forbidden,
            _ => ProviderErrorKind.BadResponse
        };
        var hint = kind == ProviderErrorKind.Forbidden ? " Tài khoản hết quyền hoặc hết phiên; kết nối lại." : "";
        return new ProviderException(provider, kind, $"{provider}: HTTP {(int)status}{(detail is null ? "" : " — " + detail)}.{hint}");
    }

    private static string? ErrorMessage(JsonNode? json)
    {
        var text = json?["error"] switch
        {
            JsonObject o => o["message"]?.ToString(),
            JsonNode n when n is JsonValue => n.ToString(),
            _ => json?["error_description"]?.ToString()
        };
        return text is { Length: > 200 } ? text[..200] : text;
    }
}
