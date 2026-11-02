using System.Text.Json.Nodes;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.TikTok;

/// <summary>
/// TikTok Login Kit cho desktop: Authorization Code + PKCE, redirect http://127.0.0.1:{port}/... (đăng ký dạng 127.0.0.1:* trong portal).
/// TikTok desktop yêu cầu code_challenge = SHA-256(verifier) dạng HEX (không phải base64url như RFC 7636).
/// Access token 24h, refresh token 365 ngày (có thể được cấp mới mỗi lần refresh).
/// https://developers.tiktok.com/doc/login-kit-desktop · https://developers.tiktok.com/doc/oauth-user-access-token-management
/// </summary>
/// <param name="clientSource">Nguồn client key/secret (OAuthClientStore) — thu hồi quyền của TikTok cần cả client, khác Google.</param>
public sealed class TikTokOAuthProvider(Func<CancellationToken, Task<OAuthClientCredentials?>>? clientSource = null, bool requestPublishScope = false,
    bool requestStatsScopes = false)
    : IOAuthProvider
{
    public const string AuthorizeEndpoint = "https://www.tiktok.com/v2/auth/authorize/";
    public const string TokenEndpoint = TikTokApi.Base + "/oauth/token/";
    public const string RevokeEndpoint = TikTokApi.Base + "/oauth/revoke/";
    public const string UserInfoBasic = "user.info.basic";
    public const string VideoUpload = "video.upload";
    public const string VideoPublish = "video.publish";
    /// <summary>Danh sách video + view/like/comment/share của từng video.</summary>
    public const string VideoList = "video.list";
    /// <summary>follower_count, likes_count, video_count của user/info.</summary>
    public const string UserInfoStats = "user.info.stats";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    /// <summary>Dùng khi phản hồi không có refresh_expires_in.</summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(365);

    public string Platform => nameof(SocialPlatform.TikTok);
    public string Provider => "TikTok";

    /// <summary>video.publish chỉ xin khi app đã được duyệt sản phẩm Direct Post (xin scope chưa duyệt → TikTok báo lỗi trang đăng nhập).</summary>
    public IReadOnlyList<string> Scopes { get; } =
    [
        UserInfoBasic, VideoUpload, .. requestPublishScope ? (string[])[VideoPublish] : [],
        .. requestStatsScopes ? (string[])[VideoList, UserInfoStats] : []
    ];

    public string? ValidateClient(OAuthClientCredentials client)
    {
        if (client.ClientId.Length is < 8 or > 64 || !client.ClientId.All(char.IsAsciiLetterOrDigit)) return "Client key của TikTok không hợp lệ.";
        if (client.ClientSecret.Length is < 8 or > 128 || client.ClientSecret.Any(char.IsWhiteSpace)) return "Client secret của TikTok không hợp lệ.";
        return null;
    }

    /// <param name="codeChallenge">Challenge S256 base64url (như mọi provider); đổi lại sang hex vì cùng là SHA-256(verifier).</param>
    public Uri BuildAuthorizeUrl(OAuthClientCredentials client, Uri redirectUri, string state, string codeChallenge) =>
        new(AuthorizeEndpoint + "?" + ConnectorHttp.Query(
            ("client_key", client.ClientId), ("scope", string.Join(',', Scopes)), ("response_type", "code"),
            ("redirect_uri", redirectUri.AbsoluteUri), ("state", state),
            ("code_challenge", HexChallenge(codeChallenge)), ("code_challenge_method", "S256")));

    /// <summary>base64url(SHA-256) → hex thường của cùng 32 byte.</summary>
    public static string HexChallenge(string base64UrlChallenge)
    {
        var s = base64UrlChallenge.Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(s); }
        catch (FormatException) { throw new ArgumentException("code_challenge không phải base64url.", nameof(base64UrlChallenge)); }
        return bytes.Length == 32 ? Convert.ToHexStringLower(bytes) : throw new ArgumentException("code_challenge phải là SHA-256.", nameof(base64UrlChallenge));
    }

    public Task<OAuthTokenSet> ExchangeCodeAsync(HttpClient http, OAuthClientCredentials client, string code, Uri redirectUri,
        string codeVerifier, DateTimeOffset now, CancellationToken ct) =>
        TokenRequestAsync(http, now, ct,
            ("client_key", client.ClientId), ("client_secret", client.ClientSecret), ("code", code), ("grant_type", "authorization_code"),
            ("redirect_uri", redirectUri.AbsoluteUri), ("code_verifier", codeVerifier));

    public Task<OAuthTokenSet> RefreshAsync(HttpClient http, OAuthClientCredentials client, string refreshToken, DateTimeOffset now, CancellationToken ct) =>
        TokenRequestAsync(http, now, ct,
            ("client_key", client.ClientId), ("client_secret", client.ClientSecret), ("grant_type", "refresh_token"), ("refresh_token", refreshToken));

    /// <summary>Tham số token của TikTok là ACCESS token (không phải refresh token).</summary>
    public async Task RevokeAsync(HttpClient http, string token, CancellationToken ct)
    {
        var client = clientSource is null ? null : await clientSource(ct);
        if (client is null) throw new ConnectorException(ConnectorErrorKind.Validation, "Chưa có client key/secret TikTok để thu hồi quyền.");
        await RevokeAsync(http, client, token, ct);
    }

    public async Task RevokeAsync(HttpClient http, OAuthClientCredentials client, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint)
        {
            Content = ConnectorHttp.Form(("client_key", client.ClientId), ("client_secret", client.ClientSecret), ("token", accessToken))
        };
        using var response = await PlatformHttp.SendOnceAsync(http, request, Timeout, TikTokApi.Name, ct);
        if (response.IsSuccessStatusCode)
        {
            var json = await ConnectorHttp.ReadJsonAsync(response, ct);
            if (ConnectorHttp.Str(json?["error"]) is not { Length: > 0 } || ConnectorHttp.Str(json?["error"]) == "invalid_token") return;
            throw Map(response, json, "thu hồi quyền");
        }
        throw Map(response, await ConnectorHttp.ReadJsonAsync(response, ct), "thu hồi quyền");
    }

    private static async Task<OAuthTokenSet> TokenRequestAsync(HttpClient http, DateTimeOffset now, CancellationToken ct, params (string, string)[] form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = ConnectorHttp.Form(form) };
        using var response = await PlatformHttp.SendOnceAsync(http, request, Timeout, TikTokApi.Name, ct);
        var json = await ConnectorHttp.ReadJsonAsync(response, ct);
        // Lỗi OAuth có thể đi kèm HTTP 200 với {"error": "...", "error_description": ...}.
        if (!response.IsSuccessStatusCode || ConnectorHttp.Str(json?["error"]) is { Length: > 0 }) throw Map(response, json, "lấy token");

        var access = ConnectorHttp.Str(json?["access_token"]);
        if (string.IsNullOrEmpty(access)) throw new ConnectorException(ConnectorErrorKind.BadResponse, "TikTok không trả access token.");
        var refresh = ConnectorHttp.Str(json?["refresh_token"]);
        var expiresIn = PlatformHttp.Long(json?["expires_in"]) ?? 0;
        var refreshIn = PlatformHttp.Long(json?["refresh_expires_in"]) is { } r and > 0 ? TimeSpan.FromSeconds(r) : RefreshTokenLifetime;
        // TikTok ngăn cách scope bằng dấu phẩy; lưu dạng cách trắng như các provider khác.
        var scope = string.Join(' ', PlatformHttp.ParseScopes(ConnectorHttp.Str(json?["scope"])).Order(StringComparer.Ordinal));
        return new OAuthTokenSet(access, string.IsNullOrEmpty(refresh) ? null : refresh,
            expiresIn > 0 ? now + TimeSpan.FromSeconds(expiresIn) : null, scope.Length == 0 ? null : scope,
            string.IsNullOrEmpty(refresh) ? null : now + refreshIn);
    }

    private static ConnectorException Map(HttpResponseMessage response, JsonNode? json, string action)
    {
        var code = (int)response.StatusCode;
        var error = ConnectorHttp.Str(json?["error"]);
        var description = PlatformHttp.Truncate(ConnectorHttp.Str(json?["error_description"]), 200);
        var message = $"TikTok từ chối {action} (HTTP {code}{(error is null ? "" : ", " + error)}){(description.Length == 0 ? "" : ": " + description)}.";
        return error switch
        {
            "invalid_grant" or "invalid_client" or "unauthorized_client" or "access_denied" or "invalid_request"
                => new ConnectorException(ConnectorErrorKind.Auth, message, null, code, error),
            _ when code == 429 => new ConnectorException(ConnectorErrorKind.RateLimited, message, ConnectorHttp.RetryAfter(response) ?? TikTokApi.MinuteRateRetryAfter, code, error),
            _ when code >= 500 => new ConnectorException(ConnectorErrorKind.Transient, message, ConnectorHttp.RetryAfter(response), code, error),
            _ when code is 401 or 403 => new ConnectorException(ConnectorErrorKind.Auth, message, null, code, error),
            _ => new ConnectorException(ConnectorErrorKind.BadResponse, message, null, code, error)
        };
    }
}
