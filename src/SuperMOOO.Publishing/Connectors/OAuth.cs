using System.Security.Cryptography;
using System.Text;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors;

/// <summary>Client OAuth do người dùng nhập (lưu DPAPI); không bao giờ hardcode hay đóng gói trong app.</summary>
public sealed record OAuthClientCredentials(string ClientId, string ClientSecret);

/// <param name="RefreshToken">null khi nền tảng không cấp lại (vd kết nối lại không có prompt=consent): KHÔNG được ghi đè refresh token cũ.</param>
/// <param name="RefreshExpiresUtc">Hạn refresh token nếu nền tảng có (TikTok 365 ngày); null = không biết/không hết hạn.</param>
public sealed record OAuthTokenSet(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresUtc, string? Scope,
    DateTimeOffset? RefreshExpiresUtc = null)
{
    // Không để token lọt vào log khi record bị in ra.
    public override string ToString() => $"OAuthTokenSet {{ ExpiresUtc = {ExpiresUtc:O}, Scope = {Scope} }}";
}

public interface IOAuthProvider
{
    string Platform { get; }
    /// <summary>Tên nhà cấp lưu ở social_connections.provider.</summary>
    string Provider { get; }
    IReadOnlyList<string> Scopes { get; }

    /// <summary>Kiểm tra định dạng client id/secret người dùng nhập; null = hợp lệ.</summary>
    string? ValidateClient(OAuthClientCredentials client);

    Uri BuildAuthorizeUrl(OAuthClientCredentials client, Uri redirectUri, string state, string codeChallenge);

    Task<OAuthTokenSet> ExchangeCodeAsync(HttpClient http, OAuthClientCredentials client, string code, Uri redirectUri,
        string codeVerifier, DateTimeOffset now, CancellationToken ct);

    Task<OAuthTokenSet> RefreshAsync(HttpClient http, OAuthClientCredentials client, string refreshToken, DateTimeOffset now, CancellationToken ct);

    /// <summary>Thu hồi quyền tại nền tảng. Token đã hết hiệu lực coi như thành công.</summary>
    Task RevokeAsync(HttpClient http, string token, CancellationToken ct);
}

/// <summary>Một đích được cấp quyền trong lần đăng nhập, có token riêng (Meta: Page token cho Page và Instagram liên kết).</summary>
/// <param name="TokenRef">Khóa của token trong vault (Meta: page id); nhiều đích có thể dùng chung một token.</param>
/// <param name="CanPublish">false = người dùng thiếu quyền tạo nội dung trên đích (vd thiếu task CREATE_CONTENT của Page).</param>
public sealed record GrantedDestination(
    SocialPlatform Platform, string RemoteId, string Title, string? Url, string TokenRef, string AccessToken, bool CanPublish,
    IReadOnlyDictionary<string, string> Attributes)
{
    public override string ToString() => $"GrantedDestination {{ Platform = {Platform}, RemoteId = {RemoteId}, Title = {Title} }}";
}

/// <param name="DataAccessExpiresUtc">Meta: hạn truy cập dữ liệu (~90 ngày từ lần dùng app gần nhất).</param>
public sealed record TokenInspection(bool IsValid, DateTimeOffset? ExpiresUtc, DateTimeOffset? DataAccessExpiresUtc)
{
    /// <summary>Mốc hết hiệu lực sớm nhất (null = không hết hạn).</summary>
    public DateTimeOffset? EffectiveExpiry =>
        ExpiresUtc is { } e ? DataAccessExpiresUtc is { } d && d < e ? d : e : DataAccessExpiresUtc;
}

/// <summary>
/// Một lần đăng nhập quản lý nhiều đích, mỗi đích có token riêng không làm mới được (Meta Page token).
/// Tầng ứng dụng lưu token đích vào vault và theo dõi hạn bằng <see cref="InspectAsync"/>.
/// </summary>
public interface IMultiDestinationOAuthProvider : IOAuthProvider
{
    /// <summary>Định danh tài khoản người dùng phía nền tảng (chặn kết nối lại nhầm tài khoản).</summary>
    Task<string> GetAccountIdAsync(HttpClient http, string userToken, CancellationToken ct);

    Task<IReadOnlyList<GrantedDestination>> ListGrantedDestinationsAsync(HttpClient http, string userToken, CancellationToken ct);

    Task<TokenInspection> InspectAsync(HttpClient http, OAuthClientCredentials client, string token, CancellationToken ct);
}

/// <param name="Error">Mã lỗi nền tảng (access_denied, state_mismatch...) khi không có token.</param>
public sealed record DesktopTokenRedirect(string? AccessToken, string? Error)
{
    public override string ToString() => $"DesktopTokenRedirect {{ HasToken = {AccessToken is not null}, Error = {Error ?? "-"} }}";
}

/// <summary>
/// Luồng đăng nhập desktop trong cửa sổ trình duyệt nhúng của chính tài khoản: nền tảng chuyển hướng tới trang cố định
/// (<see cref="DesktopRedirectUri"/>) kèm token ở fragment; app bắt điều hướng đó, không cần loopback.
/// </summary>
public interface IDesktopTokenFlow
{
    Uri DesktopRedirectUri { get; }
    Uri BuildDesktopTokenUrl(OAuthClientCredentials client, string state);
    /// <summary>null = chưa phải URL đích (tiếp tục chờ). State sai → Error.</summary>
    DesktopTokenRedirect? ParseDesktopRedirect(Uri uri, string expectedState);
    /// <summary>Đổi token ngắn hạn từ redirect sang token dài hạn.</summary>
    Task<OAuthTokenSet> ExchangeDesktopTokenAsync(HttpClient http, OAuthClientCredentials client, string accessToken, DateTimeOffset now,
        CancellationToken ct);
}

public static class OAuthPkce
{
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>64 ký tự base64url (RFC 7636: 43..128).</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string ChallengeS256(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>So sánh state hằng thời gian (không lộ qua thời gian phản hồi phần nào đã khớp).</summary>
    public static bool StateEquals(string? expected, string? actual) =>
        expected is not null && actual is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
