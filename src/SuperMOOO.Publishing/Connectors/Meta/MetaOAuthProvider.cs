using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.Meta;

/// <summary>Page được cấp quyền: Page token (không hết hạn khi lấy từ user token dài hạn) và tài khoản Instagram business liên kết.</summary>
public sealed record MetaPageGrant(
    string PageId, string Name, string PageAccessToken, IReadOnlyList<string> Tasks, string? InstagramUserId, string? InstagramUsername)
{
    /// <summary>Có quyền tạo nội dung trên Page (yêu cầu của Reels API).</summary>
    public bool CanCreateContent => Tasks.Contains("CREATE_CONTENT") || Tasks.Contains("MANAGE");

    // Không để token lọt vào log khi record bị in ra.
    public override string ToString() => $"MetaPageGrant {{ PageId = {PageId}, Name = {Name}, Instagram = {InstagramUserId ?? "-"} }}";
}

public enum TokenHealth { Ok, ExpiringSoon, Expired, Invalid }

/// <param name="ExpiresUtc">null = không hết hạn (Page token từ user token dài hạn).</param>
/// <param name="DataAccessExpiresUtc">Hạn truy cập dữ liệu (~90 ngày từ lần người dùng dùng app gần nhất); hết thì phải đăng nhập lại.</param>
public sealed record MetaTokenInfo(bool IsValid, DateTimeOffset? ExpiresUtc, DateTimeOffset? DataAccessExpiresUtc, IReadOnlySet<string> Scopes, string? Type)
{
    public TokenHealth Evaluate(DateTimeOffset now, TimeSpan warnBefore)
    {
        if (!IsValid) return TokenHealth.Invalid;
        var soonest = new[] { ExpiresUtc, DataAccessExpiresUtc }.Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        return MetaOAuthProvider.Evaluate(soonest == DateTimeOffset.MaxValue ? null : soonest, now, warnBefore);
    }
}

/// <param name="Error">Mã lỗi Meta (vd access_denied) khi người dùng từ chối.</param>
public sealed record MetaDesktopRedirect(string? AccessToken, TimeSpan? ExpiresIn, string? Error)
{
    public override string ToString() => $"MetaDesktopRedirect {{ HasToken = {AccessToken is not null}, Error = {Error ?? "-"} }}";
}

/// <summary>
/// Facebook Login cho desktop. Hai cách nhận kết quả:
/// (1) <see cref="BuildAuthorizeUrl"/> + loopback http://localhost — Meta chỉ tự cho phép localhost khi app ở chế độ Development;
///     app Live bắt buộc HTTPS + redirect URI đăng ký chính xác (cổng động không đăng ký được).
/// (2) Luồng desktop chính thức: <see cref="BuildDesktopTokenUrl"/> trong WebView2, bắt điều hướng tới
///     <see cref="DesktopRedirectUri"/> và đọc token ở fragment bằng <see cref="ParseDesktopRedirect"/>, rồi <see cref="ExchangeLongLivedAsync"/>.
/// Token người dùng dài hạn ~60 ngày, KHÔNG có refresh token: theo dõi hạn bằng <see cref="InspectTokenAsync"/> và nhắc đăng nhập lại.
/// </summary>
public sealed partial class MetaOAuthProvider(SocialPlatform platform = SocialPlatform.Facebook, OptionalScopeFlags? optional = null)
    : IMultiDestinationOAuthProvider, IDesktopTokenFlow
{
    public const string DialogEndpoint = "https://www.facebook.com/" + MetaGraphApi.Version + "/dialog/oauth";
    public const string DesktopRedirectUri = "https://www.facebook.com/connect/login_success.html";
    public const string TokenEndpoint = MetaGraphApi.Api + "/oauth/access_token";
    public const string PagesShowList = "pages_show_list";
    public const string PagesReadEngagement = "pages_read_engagement";
    public const string PagesManagePosts = "pages_manage_posts";
    public const string InstagramBasic = "instagram_basic";
    public const string InstagramContentPublish = "instagram_content_publish";
    /// <summary>Insights video/Reels của Page (video_insights); chỉ xin khi bật thống kê Meta.</summary>
    public const string ReadInsights = "read_insights";
    public const string InstagramManageInsights = "instagram_manage_insights";
    public static readonly TimeSpan DefaultWarnBefore = TimeSpan.FromDays(7);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string Name = "Meta";

    public string Platform => platform.ToString();
    public string Provider => "Meta";

    public IReadOnlyList<string> Scopes => optional is { MetaInsights: true }
        ? [PagesShowList, PagesReadEngagement, PagesManagePosts, InstagramBasic, InstagramContentPublish, ReadInsights, InstagramManageInsights]
        : [PagesShowList, PagesReadEngagement, PagesManagePosts, InstagramBasic, InstagramContentPublish];

    [GeneratedRegex(@"^\d{5,20}$")]
    private static partial Regex AppIdPattern();

    public string? ValidateClient(OAuthClientCredentials client)
    {
        if (!AppIdPattern().IsMatch(client.ClientId)) return "App ID của Meta là dãy số (xem App Dashboard → App settings → Basic).";
        if (client.ClientSecret.Length is < 16 or > 128 || !client.ClientSecret.All(char.IsAsciiLetterOrDigit))
            return "App Secret của Meta không hợp lệ.";
        return null;
    }

    /// <remarks>
    /// Không gửi code_challenge: PKCE của Facebook Login chỉ được tài liệu hóa cho luồng OIDC (bắt buộc scope openid + nonce, đang thử nghiệm);
    /// luồng code thường dùng client_secret của chính người dùng + state chống CSRF.
    /// </remarks>
    public Uri BuildAuthorizeUrl(OAuthClientCredentials client, Uri redirectUri, string state, string codeChallenge) =>
        new(DialogEndpoint + "?" + ConnectorHttp.Query(
            ("client_id", client.ClientId), ("redirect_uri", redirectUri.AbsoluteUri), ("state", state),
            ("response_type", "code"), ("scope", string.Join(',', Scopes))));

    /// <summary>Luồng desktop chính thức (response_type=token, redirect login_success.html) để mở trong WebView2.</summary>
    public Uri BuildDesktopTokenUrl(OAuthClientCredentials client, string state) =>
        new(DialogEndpoint + "?" + ConnectorHttp.Query(
            ("client_id", client.ClientId), ("redirect_uri", DesktopRedirectUri), ("state", state),
            ("response_type", "token"), ("scope", string.Join(',', Scopes))));

    /// <summary>
    /// Đọc kết quả khi WebView2 điều hướng tới login_success.html. null = không phải URL đích (tiếp tục chờ).
    /// State sai → coi như lỗi (không nhận token).
    /// </summary>
    public static MetaDesktopRedirect? ParseDesktopRedirect(Uri uri, string expectedState)
    {
        if (!uri.GetLeftPart(UriPartial.Path).Equals(DesktopRedirectUri, StringComparison.OrdinalIgnoreCase)) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (uri.Query.TrimStart('?') + "&" + uri.Fragment.TrimStart('#')).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            values[Uri.UnescapeDataString(kv[0])] = kv.Length == 2 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        if (!OAuthPkce.StateEquals(expectedState, values.GetValueOrDefault("state")))
            return new MetaDesktopRedirect(null, null, "state_mismatch");
        if (values.GetValueOrDefault("error") is { Length: > 0 } error) return new MetaDesktopRedirect(null, null, error);
        if (values.GetValueOrDefault("access_token") is not { Length: > 0 } token) return new MetaDesktopRedirect(null, null, "missing_token");
        TimeSpan? expiresIn = long.TryParse(values.GetValueOrDefault("expires_in"), out var s) && s > 0 ? TimeSpan.FromSeconds(s) : null;
        return new MetaDesktopRedirect(token, expiresIn, null);
    }

    public async Task<OAuthTokenSet> ExchangeCodeAsync(HttpClient http, OAuthClientCredentials client, string code, Uri redirectUri,
        string codeVerifier, DateTimeOffset now, CancellationToken ct)
    {
        var json = await TokenCallAsync(http, TokenEndpoint + "?" + ConnectorHttp.Query(
            ("client_id", client.ClientId), ("redirect_uri", redirectUri.AbsoluteUri), ("client_secret", client.ClientSecret), ("code", code)),
            null, "đổi mã đăng nhập", ct);
        var shortLived = ConnectorHttp.Str(json?["access_token"])
                         ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Meta không trả access token.");
        return await ExchangeLongLivedAsync(http, client, shortLived, now, ct);
    }

    /// <summary>Đổi token ngắn hạn → token dài hạn (~60 ngày), kèm danh sách quyền đã cấp (granted) từ /me/permissions.</summary>
    public async Task<OAuthTokenSet> ExchangeLongLivedAsync(HttpClient http, OAuthClientCredentials client, string userToken, DateTimeOffset now,
        CancellationToken ct)
    {
        var json = await TokenCallAsync(http, TokenEndpoint + "?" + ConnectorHttp.Query(
            ("grant_type", "fb_exchange_token"), ("client_id", client.ClientId), ("client_secret", client.ClientSecret),
            ("fb_exchange_token", userToken)), null, "đổi token dài hạn", ct);
        var access = ConnectorHttp.Str(json?["access_token"])
                     ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Meta không trả token dài hạn.");
        var expiresIn = PlatformHttp.Long(json?["expires_in"]) ?? 0;
        var scopes = await GrantedScopesAsync(http, access, ct);
        return new OAuthTokenSet(access, null, expiresIn > 0 ? now + TimeSpan.FromSeconds(expiresIn) : null, string.Join(' ', scopes));
    }

    /// <summary>Meta không có refresh token: chỉ đổi lại được khi token dài hạn còn hiệu lực (hết hạn → đăng nhập lại).</summary>
    public Task<OAuthTokenSet> RefreshAsync(HttpClient http, OAuthClientCredentials client, string refreshToken, DateTimeOffset now, CancellationToken ct) =>
        ExchangeLongLivedAsync(http, client, refreshToken, now, ct);

    /// <summary>DELETE /me/permissions: thu hồi toàn bộ quyền app. Token đã hết hiệu lực (190) coi như đã thu hồi.</summary>
    public async Task RevokeAsync(HttpClient http, string token, CancellationToken ct)
    {
        var (_, error) = await MetaGraphApi.TryCallAsync(http, MetaGraphApi.Authorized(HttpMethod.Delete, MetaGraphApi.Api + "/me/permissions", token),
            Timeout, Name, ct);
        if (error is null || error.Kind == ConnectorErrorKind.Auth) return;
        throw error;
    }

    public static async Task<IReadOnlySet<string>> GrantedScopesAsync(HttpClient http, string token, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var (json, error) = await MetaGraphApi.TryCallAsync(http, MetaGraphApi.Authorized(HttpMethod.Get, MetaGraphApi.Api + "/me/permissions?limit=100", token),
            Timeout, Name, ct);
        if (error is not null) throw error;
        foreach (var item in json?["data"]?.AsArray() ?? [])
            if (ConnectorHttp.Str(item?["status"]) == "granted" && ConnectorHttp.Str(item?["permission"]) is { } p) result.Add(p);
        return result;
    }

    /// <summary>
    /// /me/accounts có phân trang: Page + Page token + Instagram business liên kết.
    /// Page token lấy từ user token dài hạn không hết hạn (vẫn mất hiệu lực khi đổi mật khẩu/mất quyền Page/hết hạn truy cập dữ liệu).
    /// </summary>
    public static async Task<IReadOnlyList<MetaPageGrant>> ListPagesAsync(HttpClient http, string userToken, CancellationToken ct)
    {
        var result = new List<MetaPageGrant>();
        string? next = MetaGraphApi.Api + "/me/accounts?" + ConnectorHttp.Query(
            ("fields", "id,name,access_token,tasks,instagram_business_account{id,username}"), ("limit", "100"));
        for (var guard = 0; next is not null && guard < 100; guard++)
        {
            var (json, error) = await MetaGraphApi.TryCallAsync(http, MetaGraphApi.Authorized(HttpMethod.Get, next, userToken), Timeout, Name, ct);
            if (error is not null) throw error;
            foreach (var item in json?["data"]?.AsArray() ?? [])
            {
                var id = ConnectorHttp.Str(item?["id"]);
                var pageToken = ConnectorHttp.Str(item?["access_token"]);
                if (id is null || pageToken is null) continue;
                var tasks = (item?["tasks"]?.AsArray() ?? []).Select(ConnectorHttp.Str).OfType<string>().ToList();
                var ig = item?["instagram_business_account"];
                result.Add(new MetaPageGrant(id, ConnectorHttp.Str(item?["name"]) ?? id, pageToken, tasks,
                    ConnectorHttp.Str(ig?["id"]), ConnectorHttp.Str(ig?["username"])));
            }
            next = MetaGraphApi.NextPage(json);
        }
        return result;
    }

    /// <summary>/debug_token với app token (app_id|app_secret ở header, không ở URL): hạn token, hạn truy cập dữ liệu, scope.</summary>
    public static async Task<MetaTokenInfo> InspectTokenAsync(HttpClient http, OAuthClientCredentials client, string token, CancellationToken ct)
    {
        var request = MetaGraphApi.Authorized(HttpMethod.Get, MetaGraphApi.Api + "/debug_token?" + ConnectorHttp.Query(("input_token", token)),
            client.ClientId + "|" + client.ClientSecret);
        var (json, error) = await MetaGraphApi.TryCallAsync(http, request, Timeout, Name, ct);
        if (error is not null) throw error;
        var data = json?["data"];
        var scopes = (data?["scopes"]?.AsArray() ?? []).Select(ConnectorHttp.Str).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return new MetaTokenInfo(PlatformHttp.Bool(data?["is_valid"]) == true, FromUnix(data?["expires_at"]), FromUnix(data?["data_access_expires_at"]),
            scopes, ConnectorHttp.Str(data?["type"]));
    }

    /// <summary>Cảnh báo sớm trước khi token (~60 ngày) hết hạn để người dùng đăng nhập lại kịp.</summary>
    public static TokenHealth Evaluate(DateTimeOffset? expiresUtc, DateTimeOffset now, TimeSpan warnBefore) =>
        expiresUtc switch
        {
            null => TokenHealth.Ok,
            { } e when e <= now => TokenHealth.Expired,
            { } e when e - now <= warnBefore => TokenHealth.ExpiringSoon,
            _ => TokenHealth.Ok
        };

    // --- Một lần đăng nhập → nhiều đích (Page + Instagram liên kết), luồng desktop trong WebView ---

    public async Task<string> GetAccountIdAsync(HttpClient http, string userToken, CancellationToken ct)
    {
        var (json, error) = await MetaGraphApi.TryCallAsync(http, MetaGraphApi.Authorized(HttpMethod.Get, MetaGraphApi.Api + "/me?fields=id", userToken),
            Timeout, Name, ct);
        if (error is not null) throw error;
        return ConnectorHttp.Str(json?["id"]) ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Meta không trả id tài khoản.");
    }

    /// <summary>Mỗi Page → một đích Facebook; Page có Instagram business/creator liên kết → thêm một đích Instagram dùng chung Page token.</summary>
    public async Task<IReadOnlyList<GrantedDestination>> ListGrantedDestinationsAsync(HttpClient http, string userToken, CancellationToken ct)
    {
        var result = new List<GrantedDestination>();
        foreach (var page in await ListPagesAsync(http, userToken, ct))
        {
            result.Add(new GrantedDestination(SocialPlatform.Facebook, page.PageId, page.Name, "https://www.facebook.com/" + page.PageId, page.PageId,
                page.PageAccessToken, page.CanCreateContent,
                new Dictionary<string, string> { ["pageId"] = page.PageId, ["tasks"] = string.Join(',', page.Tasks) }));
            if (page.InstagramUserId is not { } igId) continue;
            var username = page.InstagramUsername;
            result.Add(new GrantedDestination(SocialPlatform.Instagram, igId, username is null ? igId : "@" + username,
                username is null ? null : "https://www.instagram.com/" + username, page.PageId, page.PageAccessToken, page.CanCreateContent,
                new Dictionary<string, string> { ["pageId"] = page.PageId, ["pageName"] = page.Name }));
        }
        return result;
    }

    public async Task<TokenInspection> InspectAsync(HttpClient http, OAuthClientCredentials client, string token, CancellationToken ct)
    {
        var info = await InspectTokenAsync(http, client, token, ct);
        return new TokenInspection(info.IsValid, info.ExpiresUtc, info.DataAccessExpiresUtc);
    }

    Uri IDesktopTokenFlow.DesktopRedirectUri => new(DesktopRedirectUri);

    DesktopTokenRedirect? IDesktopTokenFlow.ParseDesktopRedirect(Uri uri, string expectedState) =>
        ParseDesktopRedirect(uri, expectedState) is { } r ? new DesktopTokenRedirect(r.AccessToken, r.Error) : null;

    public Task<OAuthTokenSet> ExchangeDesktopTokenAsync(HttpClient http, OAuthClientCredentials client, string accessToken, DateTimeOffset now,
        CancellationToken ct) => ExchangeLongLivedAsync(http, client, accessToken, now, ct);

    private static DateTimeOffset? FromUnix(JsonNode? node) =>
        PlatformHttp.Long(node) is { } s && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private static async Task<JsonNode?> TokenCallAsync(HttpClient http, string url, string? bearer, string action, CancellationToken ct)
    {
        var request = bearer is null ? new HttpRequestMessage(HttpMethod.Get, url) : MetaGraphApi.Authorized(HttpMethod.Get, url, bearer);
        var (json, error) = await MetaGraphApi.TryCallAsync(http, request, Timeout, Name, ct);
        if (error is null) return json;
        // Mã/khóa/secret sai ở endpoint token: người dùng phải đăng nhập lại hoặc sửa App ID/Secret — không retry mù.
        if (error.Kind is ConnectorErrorKind.Validation or ConnectorErrorKind.Permission or ConnectorErrorKind.BadResponse)
            throw new ConnectorException(ConnectorErrorKind.Auth, $"Meta từ chối {action}. {error.Message}", null, error.HttpStatus, error.Reason);
        throw error;
    }
}
