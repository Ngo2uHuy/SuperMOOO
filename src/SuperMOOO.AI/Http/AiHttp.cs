using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuperMOOO.AI.Http;

/// <summary>Gửi request cloud với timeout riêng từng lời gọi (F36) và map lỗi HTTP sang ProviderException có kiểu.</summary>
internal static partial class AiHttp
{
    public const string ClientName = "ai";
    private const int ErrorBodyLimit = 64 * 1024;

    [GeneratedRegex(@"\b(?:invalid|expired|incorrect)\s+api[\s_-]*key\b|\bapi[\s_-]*key\s+(?:is\s+)?(?:invalid|expired|not\s+valid)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeyMessage();

    [GeneratedRegex("quota|billing|insufficient|exhausted", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaMessage();

    public static async Task<string> SendTextAsync(IHttpClientProvider http, string providerId, HttpRequestMessage request,
        TimeSpan timeout, CancellationToken ct, int maxBytes = 8 * 1024 * 1024) =>
        Encoding.UTF8.GetString(await SendAsync(http, providerId, request, timeout, ct, maxBytes));

    public static async Task<byte[]> SendAsync(IHttpClientProvider http, string providerId, HttpRequestMessage request,
        TimeSpan timeout, CancellationToken ct, int maxBytes = 8 * 1024 * 1024)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.Get(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            var body = await ReadLimitedAsync(response.Content, response.IsSuccessStatusCode ? maxBytes : ErrorBodyLimit, providerId, cts.Token);
            if (!response.IsSuccessStatusCode) throw MapError(providerId, status, Encoding.UTF8.GetString(body));
            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(providerId, ProviderErrorKind.Timeout,
                $"Dịch vụ {providerId} quá thời gian ({timeout.TotalSeconds:0}s); tác vụ chưa được tự động gửi lại.");
        }
        catch (HttpRequestException)
        {
            throw new ProviderException(providerId, ProviderErrorKind.Network,
                $"Không kết nối được {providerId}. Kiểm tra mạng/proxy; tác vụ chưa được tự động gửi lại.");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, string providerId, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > limit)
                throw new ProviderException(providerId, ProviderErrorKind.BadResponse, "Kết quả vượt giới hạn dung lượng.");
        }

        return ms.ToArray();
    }

    // Chỉ dùng mã/cụm từ cố định, không trả nguyên văn lỗi của provider.
    internal static ProviderException MapError(string id, int status, string body) => status switch
    {
        400 when IsKeyError(body) => new(id, ProviderErrorKind.KeyInvalid, $"API key {id} không hợp lệ hoặc đã hết hạn; nhập lại tại Cài đặt → API key."),
        400 => new(id, ProviderErrorKind.BadResponse, $"{id} từ chối yêu cầu (HTTP 400); kiểm tra model và tham số."),
        401 => new(id, ProviderErrorKind.KeyInvalid, $"API key {id} không hợp lệ (HTTP 401)."),
        402 => new(id, ProviderErrorKind.QuotaExceeded, $"Tài khoản {id} hết số dư (HTTP 402)."),
        403 => new(id, ProviderErrorKind.Forbidden, $"{id} từ chối quyền truy cập (HTTP 403): thiếu quyền hoặc model chưa được cấp."),
        404 => new(id, ProviderErrorKind.BadResponse, $"{id}: model hoặc endpoint không tồn tại (HTTP 404); chọn model khác ở Cài đặt → Model AI."),
        429 when QuotaMessage().IsMatch(body) => new(id, ProviderErrorKind.QuotaExceeded, $"{id} hết hạn mức (HTTP 429)."),
        429 => new(id, ProviderErrorKind.RateLimited, $"{id} giới hạn tốc độ (HTTP 429); chờ rồi thử lại."),
        >= 500 => new(id, ProviderErrorKind.Network, $"{id} đang lỗi (HTTP {status})."),
        _ => new(id, ProviderErrorKind.BadResponse, $"{id} trả HTTP {status}.")
    };

    internal static bool IsKeyError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind != JsonValueKind.Object) return false;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : root;

            var reasons = new List<string?> { Str(error, "reason"), Str(error, "code") };
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                reasons.AddRange(details.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object).Select(d => Str(d, "reason")));
            string[] known = ["API_KEY_INVALID", "API_KEY_EXPIRED", "INVALID_API_KEY", "EXPIRED_API_KEY"];
            if (reasons.Any(r => r is not null && known.Contains(r.ToUpperInvariant()))) return true;
            return Str(error, "message") is { } m && KeyMessage().IsMatch(m);
        }
        catch (JsonException) { return false; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonNode ParseJson(string providerId, string text)
    {
        try { return JsonNode.Parse(text) ?? throw new JsonException(); }
        catch (JsonException) { throw new ProviderException(providerId, ProviderErrorKind.BadResponse, $"{providerId} không trả JSON hợp lệ."); }
    }

    public static HttpRequestMessage Json(HttpMethod method, string url, JsonNode? body, string? bearer = null, string? googKey = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (googKey is not null) request.Headers.TryAddWithoutValidation("x-goog-api-key", googKey);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }
}
