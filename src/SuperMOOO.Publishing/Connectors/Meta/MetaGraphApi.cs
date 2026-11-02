using System.Net;
using System.Text.Json.Nodes;

namespace SuperMOOO.Publishing.Connectors.Meta;

/// <summary>Graph API của Meta (Facebook Page, Instagram qua Facebook Login): phiên bản, gửi có xác thực, map lỗi.</summary>
public static class MetaGraphApi
{
    /// <summary>Phiên bản mới nhất theo changelog (v26.0, 29/07/2026). Mỗi phiên bản sống ~2 năm: nâng định kỳ.</summary>
    public const string Version = "v26.0";
    public const string Host = "graph.facebook.com";
    public const string Api = "https://graph.facebook.com/" + Version;
    public const string RuploadVideo = "https://rupload.facebook.com/video-upload/" + Version;

    /// <summary>Không có gợi ý thời gian từ Meta (X-App-Usage chỉ có %): chờ mặc định trước khi thử lại.</summary>
    public static TimeSpan DefaultRateLimitBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Lỗi rate limit của Graph (4 app, 17 user, 32 Page, 613 custom, 341 app limit, 80001/80002 BUC Page/Instagram).</summary>
    public static bool IsRateLimitCode(int code) => code is 4 or 17 or 32 or 613 or 341 or 80001 or 80002;

    /// <summary>
    /// GET/POST Graph với token của kết nối (header Bearer: token không nằm trong URL).
    /// Lỗi xác thực (190/102/HTTP 401) → làm mới token đúng 1 lần rồi gửi lại; vẫn lỗi → Auth.
    /// </summary>
    public static async Task<JsonNode?> CallAsync(ConnectorSession session, Func<string, HttpRequestMessage> build, TimeSpan timeout,
        string platform, CancellationToken ct)
    {
        var (json, error) = await TryCallAsync(session.Http, build(await session.Tokens.GetAsync(ct)), timeout, platform, ct);
        if (error is null) return json;
        if (error.Kind != ConnectorErrorKind.Auth) throw error;

        (json, error) = await TryCallAsync(session.Http, build(await session.Tokens.RefreshAsync(ct)), timeout, platform, ct);
        if (error is null) return json;
        if (error.Kind == ConnectorErrorKind.Auth)
            throw new ConnectorException(ConnectorErrorKind.Auth, error.Message + " Token bị từ chối kể cả sau khi làm mới; cần kết nối lại.",
                null, error.HttpStatus, error.Reason);
        throw error;
    }

    public static Task<JsonNode?> GetAsync(ConnectorSession session, string url, TimeSpan timeout, string platform, CancellationToken ct) =>
        CallAsync(session, token => Authorized(HttpMethod.Get, url, token), timeout, platform, ct);

    public static Task<JsonNode?> PostFormAsync(ConnectorSession session, string url, TimeSpan timeout, string platform, CancellationToken ct,
        params (string Key, string Value)[] form) =>
        CallAsync(session, token =>
        {
            var request = Authorized(HttpMethod.Post, url, token);
            request.Content = ConnectorHttp.Form(form);
            return request;
        }, timeout, platform, ct);

    public static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = ConnectorHttp.Bearer(token);
        return request;
    }

    /// <summary>Gửi 1 lần; trả lỗi (không ném) để caller quyết định làm mới token.</summary>
    public static async Task<(JsonNode? Json, ConnectorException? Error)> TryCallAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout,
        string platform, CancellationToken ct)
    {
        using var response = await PlatformHttp.SendOnceAsync(http, request, timeout, platform, ct);
        var json = await ConnectorHttp.ReadJsonAsync(response, ct);
        if (response.IsSuccessStatusCode && json?["error"] is null) return (json, null);
        return (null, MapError(response, json, platform));
    }

    /// <summary>
    /// Map lỗi Graph theo error.code (type thường là OAuthException cho cả lỗi không phải xác thực — không map theo type).
    /// https://developers.facebook.com/docs/graph-api/guides/error-handling · .../overview/rate-limiting
    /// </summary>
    public static ConnectorException MapError(HttpResponseMessage response, JsonNode? json, string platform)
    {
        var status = (int)response.StatusCode;
        var error = json?["error"];
        var code = (int?)PlatformHttp.Long(error?["code"]);
        var sub = (int?)PlatformHttp.Long(error?["error_subcode"]);
        var transient = PlatformHttp.Bool(error?["is_transient"]) == true;
        var detail = PlatformHttp.Truncate(ConnectorHttp.Str(error?["error_user_msg"]) ?? ConnectorHttp.Str(error?["message"]), 200);
        var reason = code is null ? null : sub is null ? code.ToString() : $"{code}/{sub}";
        var message = $"{platform} trả HTTP {status}{(reason is null ? "" : " (mã " + reason + ")")}{(detail.Length == 0 ? "" : ": " + detail)}";

        var kind = code switch
        {
            // Bảng Authentication Error Subcodes: 459 user checkpointed, 464 unconfirmed user — người dùng phải tự đăng nhập
            // facebook.com để xử lý; làm mới token/thử lại không giúp gì.
            _ when sub is 459 or 464 => ConnectorErrorKind.VerificationRequired,
            190 or 102 => ConnectorErrorKind.Auth,
            { } c when IsRateLimitCode(c) => ConnectorErrorKind.RateLimited,
            368 => ConnectorErrorKind.Policy,
            // Instagram: vượt giới hạn đăng 24h (code 9 / subcode 2207042).
            9 when sub == 2207042 => ConnectorErrorKind.QuotaExceeded,
            10 or (>= 200 and <= 299) => ConnectorErrorKind.Permission,
            506 => ConnectorErrorKind.Validation,
            100 when sub == 33 => ConnectorErrorKind.NotFound,
            1 or 2 => ConnectorErrorKind.Transient,
            _ when transient => ConnectorErrorKind.Transient,
            100 => ConnectorErrorKind.Validation,
            _ => status switch
            {
                401 => ConnectorErrorKind.Auth,
                403 => ConnectorErrorKind.Permission,
                404 => ConnectorErrorKind.NotFound,
                429 => ConnectorErrorKind.RateLimited,
                >= 500 => ConnectorErrorKind.Transient,
                400 => ConnectorErrorKind.Validation,
                _ => ConnectorErrorKind.BadResponse
            }
        };
        TimeSpan? retryAfter = kind switch
        {
            ConnectorErrorKind.RateLimited => ConnectorHttp.RetryAfter(response) ?? RegainAccess(response) ?? DefaultRateLimitBackoff,
            ConnectorErrorKind.QuotaExceeded => ConnectorHttp.RetryAfter(response) ?? TimeSpan.FromHours(1),
            ConnectorErrorKind.Transient => ConnectorHttp.RetryAfter(response),
            _ => null
        };
        return new ConnectorException(kind, message, retryAfter, status, reason);
    }

    /// <summary>X-Business-Use-Case-Usage: estimated_time_to_regain_access (phút) lớn nhất; 0/không có → null.</summary>
    public static TimeSpan? RegainAccess(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Business-Use-Case-Usage", out var values)) return null;
        long max = 0;
        foreach (var value in values)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(value); }
            catch (System.Text.Json.JsonException) { continue; }
            if (root is not JsonObject obj) continue;
            foreach (var (_, entries) in obj)
                foreach (var entry in entries as JsonArray ?? [])
                    max = Math.Max(max, PlatformHttp.Long(entry?["estimated_time_to_regain_access"]) ?? 0);
        }
        return max > 0 ? TimeSpan.FromMinutes(max) : null;
    }

    /// <summary>URL phân trang paging.next do Meta trả: chỉ theo nếu là HTTPS graph.facebook.com (không gửi token tới host lạ).</summary>
    public static string? NextPage(JsonNode? json)
    {
        var next = ConnectorHttp.Str(json?["paging"]?["next"]);
        if (next is null) return null;
        return Uri.TryCreate(next, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
               && u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)
            ? next
            : throw new ConnectorException(ConnectorErrorKind.BadResponse, "Meta trả địa chỉ phân trang không hợp lệ.");
    }

    public static bool IsNotFound(ConnectorException e) => e.Kind == ConnectorErrorKind.NotFound || e.HttpStatus == (int)HttpStatusCode.NotFound;
}
