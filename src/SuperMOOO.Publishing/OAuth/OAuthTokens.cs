using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Publications;
using IDestinationRepository = SuperMOOO.Core.Persistence.IDestinationRepository;
using IQuotaLedger = SuperMOOO.Core.Persistence.IQuotaLedger;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Http;

namespace SuperMOOO.Publishing.OAuth;

/// <summary>Client OAuth của từng nền tảng do người dùng nhập; chỉ nằm trong ITokenVault (DPAPI).</summary>
[Service]
public sealed class OAuthClientStore(ITokenVault vault)
{
    private sealed record Stored(string ClientId, string ClientSecret);

    /// <summary>Facebook và Instagram dùng chung một app Meta (một App ID/Secret).</summary>
    public static string KeyFor(SocialPlatform platform) => "oauth-client:" + (platform == SocialPlatform.Instagram ? SocialPlatform.Facebook : platform);

    public Task SaveAsync(SocialPlatform platform, OAuthClientCredentials client, CancellationToken ct) =>
        vault.SaveAsync(KeyFor(platform), JsonSerializer.Serialize(new Stored(client.ClientId, client.ClientSecret)), ct);

    public async Task<OAuthClientCredentials?> GetAsync(SocialPlatform platform, CancellationToken ct)
    {
        if (await vault.TryGetAsync(KeyFor(platform), ct) is not { } raw) return null;
        var stored = JsonSerializer.Deserialize<Stored>(raw);
        return stored is null ? null : new OAuthClientCredentials(stored.ClientId, stored.ClientSecret);
    }

    /// <summary>Khóa project cho sổ quota: hash client id (không lưu client id dạng rõ ngoài vault).</summary>
    public static string ProjectKey(OAuthClientCredentials client) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(client.ClientId)))[..16];

    public static string Hint(string clientId) => clientId.Length <= 12 ? "***" : clientId[..6] + "…" + clientId[^12..];
}

public sealed record StoredOAuthToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresUtc, string? Scope,
    DateTimeOffset? RefreshExpiresUtc = null)
{
    public override string ToString() => $"StoredOAuthToken {{ ExpiresUtc = {ExpiresUtc:O}, Scope = {Scope} }}";
}

/// <summary>
/// Token của từng kết nối trong vault. Làm mới chủ động trước hạn; refresh bị từ chối → NeedsReauth + action + breaker.
/// Không bao giờ ghi token vào log/audit/DTO.
/// </summary>
[Service]
public sealed class OAuthTokenService(
    ITokenVault vault,
    IConnectionRepository connections,
    OAuthClientStore clients,
    ConnectorRegistry registry,
    PublishHttpClients http,
    IActionRequiredRepository actions,
    ICircuitBreaker breaker,
    IClock clock)
{
    public static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public IAccessTokenSource SourceFor(SocialConnection connection) => new Source(this, connection);

    public async Task<StoredOAuthToken?> LoadAsync(string connectionId, CancellationToken ct) =>
        await vault.TryGetAsync(SocialConnection.TokenKeyFor(connectionId), ct) is { } raw
            ? JsonSerializer.Deserialize<StoredOAuthToken>(raw) : null;

    /// <summary>
    /// Lưu token mới; thiếu refresh token thì giữ refresh token cũ (kết nối lại/refresh thường không cấp lại refresh token —
    /// ghi đè sẽ làm kết nối hỏng khi access token hết hạn).
    /// </summary>
    public async Task<StoredOAuthToken> SaveMergedAsync(string connectionId, OAuthTokenSet fresh, CancellationToken ct)
    {
        var old = await LoadAsync(connectionId, ct);
        var merged = new StoredOAuthToken(fresh.AccessToken, fresh.RefreshToken ?? old?.RefreshToken, fresh.ExpiresUtc, fresh.Scope ?? old?.Scope,
            fresh.RefreshToken is not null ? fresh.RefreshExpiresUtc : old?.RefreshExpiresUtc);
        await vault.SaveAsync(SocialConnection.TokenKeyFor(connectionId), JsonSerializer.Serialize(merged), ct);
        return merged;
    }

    public Task DeleteAsync(string connectionId, CancellationToken ct) => vault.DeleteAsync(SocialConnection.TokenKeyFor(connectionId), ct);

    // --- Token riêng của đích (Meta Page token: không có refresh, không hết hạn khi lấy từ user token dài hạn) ---

    public Task SaveDestinationTokenAsync(string connectionId, string tokenRef, string token, CancellationToken ct) =>
        vault.SaveAsync(SocialConnection.DestinationTokenKeyFor(connectionId, tokenRef), token, ct);

    public Task<string?> LoadDestinationTokenAsync(string connectionId, string tokenRef, CancellationToken ct) =>
        vault.TryGetAsync(SocialConnection.DestinationTokenKeyFor(connectionId, tokenRef), ct);

    public Task DeleteDestinationTokenAsync(string connectionId, string tokenRef, CancellationToken ct) =>
        vault.DeleteAsync(SocialConnection.DestinationTokenKeyFor(connectionId, tokenRef), ct);

    /// <summary>Đích có token riêng (Meta Page/Instagram) dùng token đó; còn lại dùng token của kết nối.</summary>
    public IAccessTokenSource SourceFor(SocialConnection connection, SocialDestination destination) =>
        DestinationCapabilities.TokenRef(destination) is { Length: > 0 } tokenRef
            ? new DestinationSource(this, connection, tokenRef)
            : SourceFor(connection);

    private sealed class DestinationSource(OAuthTokenService owner, SocialConnection connection, string tokenRef) : IAccessTokenSource
    {
        public async Task<string> GetAsync(CancellationToken ct)
        {
            if (await owner.LoadDestinationTokenAsync(connection.Id, tokenRef, ct) is { Length: > 0 } token) return token;
            const string missing = "Chưa có token của Page trên máy này; đăng nhập lại tài khoản.";
            await owner.MarkNeedsReauthAsync(connection, missing, ct);
            throw new ConnectorException(ConnectorErrorKind.Auth, missing);
        }

        public Task<string> RefreshAsync(CancellationToken ct) =>
            throw new ConnectorException(ConnectorErrorKind.Auth, "Nền tảng từ chối token của Page (đổi mật khẩu, mất quyền Page hoặc hết hạn truy cập dữ liệu); đăng nhập lại.");
    }

    public async Task MarkNeedsReauthAsync(SocialConnection connection, string reason, CancellationToken ct)
    {
        await connections.SetApiStatusAsync(connection.Id, ApiStatus.NeedsReauth, reason, clock.UtcNow, ct);
        breaker.RecordFailure(connection.Platform.ToString(), connection.Id, FailureKind.Auth, reason);
        await actions.RaiseAsync(ActionKinds.ConnectionNeedsReauth, connection.Id,
            $"Tài khoản \"{connection.DisplayName}\" cần kết nối lại", reason, ct);
    }

    private async Task<string> GetAccessTokenAsync(SocialConnection connection, bool force, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var token = await LoadAsync(connection.Id, ct);
            if (token is null)
            {
                const string missing = "Tài khoản chưa có token trên máy này; cần kết nối lại.";
                await MarkNeedsReauthAsync(connection, missing, ct);
                throw new ConnectorException(ConnectorErrorKind.Auth, missing);
            }
            if (!force && (token.ExpiresUtc is null || token.ExpiresUtc > clock.UtcNow + RefreshAhead)) return token.AccessToken;

            if (token.RefreshToken is null)
            {
                const string noRefresh = "Không có refresh token để gia hạn quyền; cần kết nối lại.";
                await MarkNeedsReauthAsync(connection, noRefresh, ct);
                throw new ConnectorException(ConnectorErrorKind.Auth, noRefresh);
            }
            var provider = registry.FindOAuth(connection.Platform)
                           ?? throw new ConnectorException(ConnectorErrorKind.Permission, "Nền tảng chưa hỗ trợ OAuth.");
            var client = await clients.GetAsync(connection.Platform, ct)
                         ?? throw new ConnectorException(ConnectorErrorKind.Permission, "Chưa cấu hình OAuth client cho nền tảng này.");

            OAuthTokenSet fresh;
            try
            {
                fresh = await provider.RefreshAsync(await http.GetForConnectionAsync(connection.Id, ct), client, token.RefreshToken, clock.UtcNow, ct);
            }
            catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Auth)
            {
                await MarkNeedsReauthAsync(connection, e.Message, ct);
                throw;
            }
            var merged = await SaveMergedAsync(connection.Id, fresh, ct);
            await connections.SetTokenExpiryAsync(connection.Id, merged.ExpiresUtc, clock.UtcNow, ct);
            return merged.AccessToken;
        }
        finally { gate.Release(); }
    }

    private sealed class Source(OAuthTokenService owner, SocialConnection connection) : IAccessTokenSource
    {
        public Task<string> GetAsync(CancellationToken ct) => owner.GetAccessTokenAsync(connection, false, ct);
        public Task<string> RefreshAsync(CancellationToken ct) => owner.GetAccessTokenAsync(connection, true, ct);
    }
}

/// <summary>Token cố định (vừa đổi từ code, chưa lưu) để kiểm tra danh tính kênh trước khi ghi đè gì.</summary>
internal sealed class FixedTokenSource(string accessToken) : IAccessTokenSource
{
    public Task<string> GetAsync(CancellationToken ct) => Task.FromResult(accessToken);

    public Task<string> RefreshAsync(CancellationToken ct) =>
        throw new ConnectorException(ConnectorErrorKind.Auth, "Nền tảng từ chối token vừa cấp.");
}

/// <summary>
/// Sổ quota ngày theo project OAuth (YouTube: 10.000 units/ngày mặc định, reset nửa đêm Pacific). Hết quota → dời job tới lúc reset,
/// không tính lượt thử. Chi phí videos.insert cấu hình được (tài liệu nguồn mâu thuẫn 100 vs 1600; mặc định 1600, cần xác minh).
/// </summary>
[Service]
public sealed class QuotaGuard(IQuotaLedger ledger, OAuthClientStore clients, IPublishSettingsStore settings, IClock clock)
{
    public const int DefaultInsertCost = 1600;
    public const int DefaultDailyQuota = 10_000;

    public async Task<int> InsertCostAsync(CancellationToken ct) =>
        await ReadIntAsync(SettingKeys.YouTubeInsertCost, DefaultInsertCost, ct);

    public async Task ConsumeAsync(SocialConnection connection, int units, CancellationToken ct, string endpoint = "api")
    {
        if (connection.Platform != SocialPlatform.YouTube) return;
        var (key, limit) = await ProjectAsync(ct);
        if (key is null) return;
        var now = clock.UtcNow;
        if (!await ledger.TryConsumeAsync("youtube", key, endpoint, units, limit, PacificTime.DayStart(now), PacificTime.NextMidnight(now), ct))
            throw new NetworkRequestBlockedException(BlockReason.RateLimited, PacificTime.NextMidnight(now),
                $"Hết quota ngày của YouTube API (ước tính {limit} units); dời tới lúc quota reset (nửa đêm giờ Pacific).", connection.Id);
    }

    /// <summary>Máy chủ báo quotaExceeded: mọi job cùng project chờ tới nửa đêm Pacific.</summary>
    public async Task MarkExhaustedAsync(SocialConnection connection, CancellationToken ct)
    {
        if (connection.Platform != SocialPlatform.YouTube) return;
        var (key, limit) = await ProjectAsync(ct);
        var now = clock.UtcNow;
        if (key is not null) await ledger.MarkExhaustedAsync("youtube", key, limit, PacificTime.DayStart(now), PacificTime.NextMidnight(now), ct);
    }

    public async Task<(int Used, int Limit)> GetUsageAsync(CancellationToken ct)
    {
        var (key, limit) = await ProjectAsync(ct);
        var now = clock.UtcNow;
        return key is null ? (0, 0) : (await ledger.GetUsedAsync(key, PacificTime.DayStart(now), PacificTime.NextMidnight(now), ct), limit);
    }

    private async Task<(string? Key, int Limit)> ProjectAsync(CancellationToken ct)
    {
        var client = await clients.GetAsync(SocialPlatform.YouTube, ct);
        return (client is null ? null : OAuthClientStore.ProjectKey(client), await ReadIntAsync(SettingKeys.YouTubeDailyQuota, DefaultDailyQuota, ct));
    }

    private async Task<int> ReadIntAsync(string key, int fallback, CancellationToken ct) =>
        int.TryParse(await settings.GetAsync(key, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
}

internal static class PublishText
{
    public static string Truncate(string? s, int max = 500) => s is null ? "" : s.Length <= max ? s : s[..max];

    public static List<string> ParseTags(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json, PublishingJson.Options) ?? []; }
        catch (JsonException) { return []; }
    }
}
