using SuperMOOO.Publishing.Connectors;
using Microsoft.Extensions.Logging;
using SuperMOOO.Publishing.Publications;
using IDestinationRepository = SuperMOOO.Core.Persistence.IDestinationRepository;
using IQuotaLedger = SuperMOOO.Core.Persistence.IQuotaLedger;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Http;

namespace SuperMOOO.Publishing.OAuth;

/// <summary>
/// Theo dõi hạn token không tự làm mới được: Meta (user token ~60 ngày, hạn truy cập dữ liệu ~90 ngày, Page token) qua debug_token;
/// TikTok refresh token (365 ngày). Sắp hết hạn trong <see cref="WarnBefore"/> → mục Cần xử lý; hết hạn/không hợp lệ → NeedsReauth.
/// </summary>
[Service]
public sealed class TokenHealthMonitor(
    IConnectionRepository connections,
    IDestinationRepository destinations,
    OAuthTokenService tokens,
    OAuthClientStore clients,
    ConnectorRegistry registry,
    PublishHttpClients http,
    IActionRequiredRepository actions,
    IClock clock,
    ILogger<TokenHealthMonitor> logger)
{
    public static readonly TimeSpan WarnBefore = TimeSpan.FromDays(7);

    /// <returns>Số kết nối bị cảnh báo hoặc chuyển NeedsReauth.</returns>
    public async Task<int> CheckAllAsync(CancellationToken ct)
    {
        var flagged = 0;
        foreach (var c in (await connections.ListAsync(ct)).Where(c => c.ApiStatus == ApiStatus.Connected))
        {
            try
            {
                if (await CheckAsync(c, ct)) flagged++;
            }
            catch (Exception e) when (e is ConnectorException or NetworkRequestBlockedException)
            {
                // Lỗi mạng/rate limit/app secret sai: không kết luận token hỏng, lượt sau kiểm tra lại.
                logger.LogWarning("Không kiểm tra được hạn token của {ConnectionId}: {Reason}", c.Id, e.Message);
            }
        }
        return flagged;
    }

    /// <returns>true = đã cảnh báo sắp hết hạn hoặc đánh dấu cần kết nối lại.</returns>
    public async Task<bool> CheckAsync(SocialConnection c, CancellationToken ct)
    {
        var provider = registry.FindOAuth(c.Platform);
        var token = await tokens.LoadAsync(c.Id, ct);
        if (provider is null || token is null) return false;

        DateTimeOffset? expiry;
        string what;
        if (provider is IMultiDestinationOAuthProvider multi)
        {
            if (await clients.GetAsync(c.Platform, ct) is not { } client) return false;
            var httpClient = await http.GetForConnectionAsync(c.Id, ct);
            var user = await multi.InspectAsync(httpClient, client, token.AccessToken, ct);
            if (!user.IsValid)
                return await ReauthAsync(c, "Token Meta không còn hiệu lực (đổi mật khẩu, gỡ quyền app hoặc hết hạn); đăng nhập lại.", ct);
            expiry = user.EffectiveExpiry;
            foreach (var d in (await destinations.ListAsync(c.Id, false, ct)).DistinctBy(DestinationCapabilities.TokenRef))
            {
                if (DestinationCapabilities.TokenRef(d) is not { } tokenRef
                    || await tokens.LoadDestinationTokenAsync(c.Id, tokenRef, ct) is not { } pageToken) continue;
                var page = await multi.InspectAsync(httpClient, client, pageToken, ct);
                if (!page.IsValid)
                    return await ReauthAsync(c, $"Token của Page \"{d.Title}\" không còn hiệu lực (mất quyền Page?); đăng nhập lại.", ct);
                if (page.EffectiveExpiry is { } pe && (expiry is null || pe < expiry)) expiry = pe;
            }
            what = "Đăng nhập Meta";
            await connections.SetTokenExpiryAsync(c.Id, expiry, clock.UtcNow, ct);
        }
        else if (token.RefreshToken is not null && token.RefreshExpiresUtc is { } refreshExpiry)
        {
            expiry = refreshExpiry;
            what = "Refresh token";
        }
        else return false;

        var now = clock.UtcNow;
        if (expiry is { } e && e <= now) return await ReauthAsync(c, $"{what} đã hết hạn ({e:yyyy-MM-dd HH:mm}Z); kết nối lại.", ct);
        if (expiry is { } soon && soon - now <= WarnBefore)
        {
            await actions.RaiseAsync(ActionKinds.ConnectionTokenExpiring, c.Id, $"Tài khoản \"{c.DisplayName}\" sắp hết hạn đăng nhập",
                $"{what} hết hạn lúc {soon:yyyy-MM-dd HH:mm}Z. Vào Tài khoản → Kết nối lại trước hạn để bài đã lên lịch không bị dừng.", ct);
            return true;
        }
        await actions.ResolveByRefAsync(ActionKinds.ConnectionTokenExpiring, c.Id, ct);
        return false;
    }

    private async Task<bool> ReauthAsync(SocialConnection c, string reason, CancellationToken ct)
    {
        await tokens.MarkNeedsReauthAsync(c, reason, ct);
        await actions.ResolveByRefAsync(ActionKinds.ConnectionTokenExpiring, c.Id, ct);
        return true;
    }
}
