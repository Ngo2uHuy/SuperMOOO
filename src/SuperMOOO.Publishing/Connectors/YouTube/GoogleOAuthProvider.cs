using System.Net;
using System.Text.Json.Nodes;

namespace SuperMOOO.Publishing.Connectors.YouTube;

/// <summary>
/// OAuth 2.0 cho ứng dụng desktop của Google: Authorization Code + PKCE S256, redirect loopback 127.0.0.1 cổng động.
/// Đăng nhập phải ở trình duyệt hệ thống (Google chặn OAuth trong embedded webview).
/// </summary>
public sealed class GoogleOAuthProvider(OptionalScopeFlags? optional = null) : IOAuthProvider
{
    public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string UploadScope = "https://www.googleapis.com/auth/youtube.upload";
    public const string ReadonlyScope = "https://www.googleapis.com/auth/youtube.readonly";
    /// <summary>YouTube Analytics API (watch time, chia sẻ); chỉ xin khi người dùng bật "YouTube Analytics" rồi kết nối lại.</summary>
    public const string AnalyticsScope = "https://www.googleapis.com/auth/yt-analytics.readonly";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Platform => "YouTube";
    public string Provider => "Google";
    public IReadOnlyList<string> Scopes => optional is { YouTubeAnalytics: true } ? [UploadScope, ReadonlyScope, AnalyticsScope] : [UploadScope, ReadonlyScope];

    public string? ValidateClient(OAuthClientCredentials client)
    {
        if (client.ClientId.Length is < 20 or > 300 || client.ClientId.Any(char.IsWhiteSpace)
            || !client.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            return "Client ID của Google phải có dạng ...apps.googleusercontent.com.";
        if (client.ClientSecret.Length is < 8 or > 300 || client.ClientSecret.Any(char.IsWhiteSpace))
            return "Client Secret không hợp lệ.";
        return null;
    }

    public Uri BuildAuthorizeUrl(OAuthClientCredentials client, Uri redirectUri, string state, string codeChallenge) =>
        new(AuthorizeEndpoint + "?" + ConnectorHttp.Query(
            ("client_id", client.ClientId), ("redirect_uri", redirectUri.AbsoluteUri), ("response_type", "code"),
            ("scope", string.Join(' ', Scopes)), ("access_type", "offline"),
            // consent: Google chỉ cấp refresh token ở lần đồng ý; thiếu thì kết nối lại sẽ không có refresh token mới.
            ("prompt", "consent"), ("include_granted_scopes", "true"), ("state", state),
            ("code_challenge", codeChallenge), ("code_challenge_method", "S256")));

    public Task<OAuthTokenSet> ExchangeCodeAsync(HttpClient http, OAuthClientCredentials client, string code, Uri redirectUri,
        string codeVerifier, DateTimeOffset now, CancellationToken ct) =>
        TokenRequestAsync(http, now, ct,
            ("client_id", client.ClientId), ("client_secret", client.ClientSecret), ("code", code),
            ("code_verifier", codeVerifier), ("grant_type", "authorization_code"), ("redirect_uri", redirectUri.AbsoluteUri));

    public Task<OAuthTokenSet> RefreshAsync(HttpClient http, OAuthClientCredentials client, string refreshToken, DateTimeOffset now, CancellationToken ct) =>
        TokenRequestAsync(http, now, ct,
            ("client_id", client.ClientId), ("client_secret", client.ClientSecret),
            ("refresh_token", refreshToken), ("grant_type", "refresh_token"));

    public async Task RevokeAsync(HttpClient http, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint) { Content = ConnectorHttp.Form(("token", token)) };
        using var response = await SendAsync(http, request, ct);
        if (response.IsSuccessStatusCode) return;
        var json = await ConnectorHttp.ReadJsonAsync(response, ct);
        // invalid_token: token đã bị thu hồi/hết hạn — mục tiêu thu hồi đã đạt.
        if (response.StatusCode == HttpStatusCode.BadRequest && ConnectorHttp.Str(json?["error"]) == "invalid_token") return;
        throw Map(response, json, "thu hồi quyền");
    }

    private static async Task<OAuthTokenSet> TokenRequestAsync(HttpClient http, DateTimeOffset now, CancellationToken ct, params (string, string)[] form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = ConnectorHttp.Form(form) };
        using var response = await SendAsync(http, request, ct);
        var json = await ConnectorHttp.ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode) throw Map(response, json, "lấy token");

        var access = ConnectorHttp.Str(json?["access_token"]);
        if (string.IsNullOrEmpty(access)) throw new ConnectorException(ConnectorErrorKind.BadResponse, "Google không trả access token.");
        var refresh = ConnectorHttp.Str(json?["refresh_token"]);
        var expiresIn = json?["expires_in"] is JsonValue e && e.TryGetValue<long>(out var seconds) ? seconds : 0;
        return new OAuthTokenSet(access, string.IsNullOrEmpty(refresh) ? null : refresh,
            expiresIn > 0 ? now + TimeSpan.FromSeconds(expiresIn) : null, ConnectorHttp.Str(json?["scope"]));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        request.Options.Set(ConnectorHttp.CallerHandlesAuth, true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try { return await http.SendAsync(request, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ConnectorErrorKind.Timeout, "Hết thời gian chờ máy chủ token của Google.");
        }
        catch (HttpRequestException)
        {
            throw new ConnectorException(ConnectorErrorKind.Transient, "Không kết nối được tới máy chủ token của Google.");
        }
    }

    private static ConnectorException Map(HttpResponseMessage response, JsonNode? json, string action)
    {
        var code = (int)response.StatusCode;
        var error = ConnectorHttp.Str(json?["error"]);
        var description = ConnectorHttp.Str(json?["error_description"]);
        var detail = description is { Length: > 200 } ? description[..200] : description;
        var message = $"Google từ chối {action} (HTTP {code}{(error is null ? "" : ", " + error)}){(detail is null ? "" : ": " + detail)}.";
        return error switch
        {
            // Refresh token bị thu hồi/hết hạn (vd app ở chế độ Testing: 7 ngày) hoặc client sai: người dùng phải kết nối lại/sửa client.
            "invalid_grant" or "invalid_client" or "unauthorized_client" => new ConnectorException(ConnectorErrorKind.Auth, message, null, code, error),
            _ when code == 429 => new ConnectorException(ConnectorErrorKind.RateLimited, message, ConnectorHttp.RetryAfter(response), code, error),
            _ when code >= 500 => new ConnectorException(ConnectorErrorKind.Transient, message, ConnectorHttp.RetryAfter(response), code, error),
            _ when code is 401 or 403 => new ConnectorException(ConnectorErrorKind.Auth, message, null, code, error),
            _ => new ConnectorException(ConnectorErrorKind.BadResponse, message, null, code, error)
        };
    }
}
