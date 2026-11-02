using System.Globalization;
using System.Text.Json;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Publications;
using IDestinationRepository = SuperMOOO.Core.Persistence.IDestinationRepository;
using IQuotaLedger = SuperMOOO.Core.Persistence.IQuotaLedger;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Http;

namespace SuperMOOO.Publishing.Connectors;

/// <summary>Cấu hình media host S3-compatible (S3/R2/MinIO) cho Instagram. Access key/secret chỉ nằm trong ITokenVault (DPAPI).</summary>
public sealed record MediaHostConfig(
    string Endpoint, string Region, string Bucket, bool PathStyle, string AccessKeyId, string SecretAccessKey, string KeyPrefix, int UrlLifetimeHours)
{
    public const string DefaultPrefix = "centerpost-tmp/";
    public const int DefaultUrlLifetimeHours = 2;

    public override string ToString() => $"MediaHostConfig {{ Endpoint = {Endpoint}, Bucket = {Bucket}, Prefix = {KeyPrefix} }}";
}

[Service]
public sealed class MediaHostStore(IPublishSettingsStore settings, ITokenVault vault)
{
    public const string VaultKey = "media-host-credentials";

    private sealed record Credentials(string AccessKeyId, string SecretAccessKey);

    /// <summary>null = chưa cấu hình (thiếu endpoint/bucket hoặc khóa).</summary>
    public async Task<MediaHostConfig?> LoadAsync(CancellationToken ct)
    {
        var (endpoint, region, bucket, pathStyle, prefix, hours) = await ReadSettingsAsync(ct);
        var credentials = await LoadCredentialsAsync(ct);
        if (endpoint.Length == 0 || bucket.Length == 0 || credentials is null) return null;
        return new MediaHostConfig(endpoint, region, bucket, pathStyle, credentials.AccessKeyId, credentials.SecretAccessKey, prefix, hours);
    }

    public async Task<MediaHostDto> GetDtoAsync(CancellationToken ct)
    {
        var (endpoint, region, bucket, pathStyle, prefix, hours) = await ReadSettingsAsync(ct);
        var credentials = await LoadCredentialsAsync(ct);
        return new MediaHostDto(endpoint.Length > 0 && bucket.Length > 0 && credentials is not null, endpoint, region, bucket, pathStyle,
            credentials is null ? null : OAuthClientStore.Hint(credentials.AccessKeyId), credentials is { SecretAccessKey.Length: > 0 },
            prefix, hours);
    }

    /// <summary>Ghép form người dùng với khóa đã lưu (ô khóa để trống = giữ khóa cũ).</summary>
    public async Task<MediaHostConfig> MergeAsync(SetMediaHostRequest r, CancellationToken ct)
    {
        var saved = await LoadCredentialsAsync(ct);
        var access = string.IsNullOrWhiteSpace(r.AccessKeyId) ? saved?.AccessKeyId ?? "" : r.AccessKeyId.Trim();
        var secret = string.IsNullOrWhiteSpace(r.SecretAccessKey) ? saved?.SecretAccessKey ?? "" : r.SecretAccessKey.Trim();
        var prefix = string.IsNullOrWhiteSpace(r.KeyPrefix) ? MediaHostConfig.DefaultPrefix : r.KeyPrefix.Trim();
        return new MediaHostConfig((r.Endpoint ?? "").Trim().TrimEnd('/'), string.IsNullOrWhiteSpace(r.Region) ? "auto" : r.Region.Trim(),
            (r.Bucket ?? "").Trim(), r.PathStyle, access, secret, prefix, r.UrlLifetimeHours);
    }

    public async Task SaveAsync(MediaHostConfig c, CancellationToken ct)
    {
        await vault.SaveAsync(VaultKey, JsonSerializer.Serialize(new Credentials(c.AccessKeyId, c.SecretAccessKey)), ct);
        await settings.SetAsync(SettingKeys.MediaHostEndpoint, c.Endpoint, ct);
        await settings.SetAsync(SettingKeys.MediaHostRegion, c.Region, ct);
        await settings.SetAsync(SettingKeys.MediaHostBucket, c.Bucket, ct);
        await settings.SetAsync(SettingKeys.MediaHostPathStyle, PlatformFlags.Bool(c.PathStyle), ct);
        await settings.SetAsync(SettingKeys.MediaHostPrefix, c.KeyPrefix, ct);
        await settings.SetAsync(SettingKeys.MediaHostUrlHours, PlatformFlags.Int(c.UrlLifetimeHours), ct);
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await vault.DeleteAsync(VaultKey, ct);
        await settings.SetAsync(SettingKeys.MediaHostEndpoint, "", ct);
        await settings.SetAsync(SettingKeys.MediaHostBucket, "", ct);
    }

    private async Task<Credentials?> LoadCredentialsAsync(CancellationToken ct)
    {
        if (await vault.TryGetAsync(VaultKey, ct) is not { } raw) return null;
        try { return JsonSerializer.Deserialize<Credentials>(raw) is { AccessKeyId.Length: > 0 } c ? c : null; }
        catch (JsonException) { return null; }
    }

    private async Task<(string Endpoint, string Region, string Bucket, bool PathStyle, string Prefix, int Hours)> ReadSettingsAsync(CancellationToken ct)
    {
        var hours = int.TryParse(await settings.GetAsync(SettingKeys.MediaHostUrlHours, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h > 0
            ? h : MediaHostConfig.DefaultUrlLifetimeHours;
        var pathStyle = await settings.GetAsync(SettingKeys.MediaHostPathStyle, ct);
        return ((await settings.GetAsync(SettingKeys.MediaHostEndpoint, ct) ?? "").Trim(),
            await settings.GetAsync(SettingKeys.MediaHostRegion, ct) is { Length: > 0 } region ? region : "auto",
            (await settings.GetAsync(SettingKeys.MediaHostBucket, ct) ?? "").Trim(),
            pathStyle is null || PlatformFlags.IsTrue(pathStyle),
            await settings.GetAsync(SettingKeys.MediaHostPrefix, ct) is { Length: > 0 } prefix ? prefix : MediaHostConfig.DefaultPrefix,
            hours);
    }
}

/// <summary>Phần phụ thuộc thư viện connector nền tảng (Infrastructure): nạp lại cấu hình vào connector, kiểm tra media host, creator_info.</summary>
public interface IPlatformRuntime
{
    /// <summary>Đọc lại cờ audit TikTok, scope Direct Post, media host và dựng lại connector đang dùng.</summary>
    Task ReloadAsync(CancellationToken ct);

    /// <summary>null = hợp lệ.</summary>
    string? ValidateMediaHost(MediaHostConfig config);

    /// <summary>PUT một file nhỏ → GET qua URL có chữ ký → DELETE. Không lưu gì.</summary>
    Task<MediaHostTestDto> TestMediaHostAsync(MediaHostConfig config, CancellationToken ct);

    Task<TikTokCreatorInfoDto> QueryTikTokCreatorInfoAsync(ConnectorSession session, CancellationToken ct);
}

/// <summary>Use case IPC: cài đặt Meta/TikTok, media host cho Instagram, creator_info của TikTok.</summary>
[Service]
public sealed class PlatformSettingsService(
    IPublishSettingsStore settings,
    MediaHostStore mediaHost,
    IPlatformRuntime runtime,
    IConnectionRepository connections,
    IDestinationRepository destinations,
    OAuthTokenService tokens,
    PublishHttpClients http,
    IAuditLog audit)
{
    public const int MaxUrlLifetimeHours = 24;

    public async Task<PlatformSettingsDto> GetAsync(CancellationToken ct) => new(
        await PlatformFlags.TikTokAuditedAsync(settings, ct), await PlatformFlags.TikTokRequestPublishScopeAsync(settings, ct),
        await PlatformFlags.MetaLoginFlowAsync(settings, ct), await PlatformFlags.MetaLoopbackHostAsync(settings, ct),
        await PlatformFlags.YouTubeAnalyticsEnabledAsync(settings, ct), await PlatformFlags.TikTokRequestStatsScopesAsync(settings, ct),
        await PlatformFlags.MetaRequestInsightsScopesAsync(settings, ct));

    public async Task<PlatformSettingsDto> SetAsync(PlatformSettingsDto r, CancellationToken ct)
    {
        if (r.MetaLoginFlow is not (OAuthFlows.Loopback or OAuthFlows.WebView)) throw Invalid("Cách đăng nhập Meta không hợp lệ.");
        if (r.MetaLoopbackHost is not (MetaLoopbackHosts.Localhost or MetaLoopbackHosts.Ip)) throw Invalid("Host loopback phải là localhost hoặc 127.0.0.1.");
        await settings.SetAsync(SettingKeys.TikTokDirectPostAudited, PlatformFlags.Bool(r.TikTokDirectPostAudited), ct);
        await settings.SetAsync(SettingKeys.TikTokRequestPublishScope, PlatformFlags.Bool(r.TikTokRequestPublishScope), ct);
        await settings.SetAsync(SettingKeys.MetaLoginFlow, r.MetaLoginFlow, ct);
        await settings.SetAsync(SettingKeys.MetaLoopbackHost, r.MetaLoopbackHost, ct);
        await settings.SetAsync(SettingKeys.YouTubeAnalyticsEnabled, PlatformFlags.Bool(r.YouTubeAnalyticsEnabled), ct);
        await settings.SetAsync(SettingKeys.TikTokRequestStatsScopes, PlatformFlags.Bool(r.TikTokRequestStatsScopes), ct);
        await settings.SetAsync(SettingKeys.MetaRequestInsightsScopes, PlatformFlags.Bool(r.MetaRequestInsightsScopes), ct);
        await runtime.ReloadAsync(ct);
        await audit.WriteAsync("ipc", "platform.settings", "platform",
            $"tiktokAudited={r.TikTokDirectPostAudited}; tiktokPublishScope={r.TikTokRequestPublishScope}; metaFlow={r.MetaLoginFlow}; "
            + $"ytAnalytics={r.YouTubeAnalyticsEnabled}; tiktokStats={r.TikTokRequestStatsScopes}; metaInsights={r.MetaRequestInsightsScopes}", ct);
        return await GetAsync(ct);
    }

    // --- Media host ---

    public Task<MediaHostDto> GetMediaHostAsync(CancellationToken ct) => mediaHost.GetDtoAsync(ct);

    public async Task<MediaHostDto> SetMediaHostAsync(SetMediaHostRequest r, CancellationToken ct)
    {
        var config = await ValidatedAsync(r, ct);
        await mediaHost.SaveAsync(config, ct);
        await runtime.ReloadAsync(ct);
        // Không ghi khóa vào audit.
        await audit.WriteAsync("ipc", "mediahost.set", config.Endpoint, config.Bucket, ct);
        return await mediaHost.GetDtoAsync(ct);
    }

    public async Task<object?> ClearMediaHostAsync(CancellationToken ct)
    {
        await mediaHost.ClearAsync(ct);
        await runtime.ReloadAsync(ct);
        await audit.WriteAsync("ipc", "mediahost.clear", "mediahost", null, ct);
        return null;
    }

    public async Task<MediaHostTestDto> TestMediaHostAsync(SetMediaHostRequest r, CancellationToken ct)
    {
        var config = await ValidatedAsync(r, ct);
        return await runtime.TestMediaHostAsync(config, ct);
    }

    private async Task<MediaHostConfig> ValidatedAsync(SetMediaHostRequest r, CancellationToken ct)
    {
        if (r.UrlLifetimeHours is < 1 or > MaxUrlLifetimeHours) throw Invalid($"Thời hạn URL phải trong 1..{MaxUrlLifetimeHours} giờ.");
        var config = await mediaHost.MergeAsync(r, ct);
        if (config.AccessKeyId.Length == 0 || config.SecretAccessKey.Length == 0) throw Invalid("Nhập access key và secret key.");
        if (!config.KeyPrefix.EndsWith('/')) throw Invalid("Tiền tố phải kết thúc bằng \"/\" (vd centerpost-tmp/) để đặt lifecycle rule theo thư mục.");
        return runtime.ValidateMediaHost(config) is { } error ? throw Invalid(error) : config;
    }

    // --- TikTok ---

    /// <summary>creator_info/query: gọi mỗi lần mở form Direct Post (yêu cầu của TikTok), không cache.</summary>
    public async Task<TikTokCreatorInfoDto> GetTikTokCreatorInfoAsync(GetTikTokCreatorInfoRequest r, CancellationToken ct)
    {
        var destination = await destinations.GetAsync(r.DestinationId, ct) ?? throw NotFound("Không tìm thấy kênh đích.");
        if (destination.Platform != SocialPlatform.TikTok) throw Invalid("Kênh không phải TikTok.");
        var connection = await connections.GetAsync(destination.ConnectionId, ct) ?? throw NotFound("Không tìm thấy tài khoản.");
        if (connection.ApiStatus != ApiStatus.Connected) throw Invalid("Tài khoản TikTok chưa kết nối API hoặc cần kết nối lại.");
        await http.EnsureRouteAsync(connection.Id, ct);
        try
        {
            var session = new ConnectorSession(connection.Id, await http.GetForConnectionAsync(connection.Id, ct), tokens.SourceFor(connection, destination));
            var info = await runtime.QueryTikTokCreatorInfoAsync(session, ct);
            return info with { DirectPostAudited = await PlatformFlags.TikTokAuditedAsync(settings, ct) };
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Auth)
        {
            await tokens.MarkNeedsReauthAsync(connection, e.Message, ct);
            throw Invalid(e.Message);
        }
        catch (Exception e) when (e is ConnectorException or NetworkRequestBlockedException)
        {
            throw Invalid($"Không lấy được thông tin tài khoản TikTok: {e.Message}");
        }
    }

    private static PublishingException Invalid(string message) => new(PublishingErrors.InvalidRequest, message);
    private static PublishingException NotFound(string message) => new(PublishingErrors.NotFound, message);
}
