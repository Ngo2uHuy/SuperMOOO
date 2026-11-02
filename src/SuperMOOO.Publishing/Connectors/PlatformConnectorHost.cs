using SuperMOOO.Publishing.Publications;
using IDestinationRepository = SuperMOOO.Core.Persistence.IDestinationRepository;
using IQuotaLedger = SuperMOOO.Core.Persistence.IQuotaLedger;
using SuperMOOO.Publishing.Connectors.MediaHost;
using SuperMOOO.Publishing.Connectors.Meta;
using SuperMOOO.Publishing.Connectors.TikTok;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Http;

namespace SuperMOOO.Publishing.Connectors;

/// <summary>
/// Giữ connector Instagram/TikTok (và OAuth provider TikTok) dựng theo cấu hình người dùng đổi lúc chạy:
/// media host, cờ audit Direct Post, scope video.publish. Registry thấy bản bọc <see cref="DelegatingConnector"/> nên luôn gọi bản mới nhất.
/// </summary>
[Service(As = [typeof(IPlatformRuntime)])]
public sealed class PlatformConnectorHost : IPlatformRuntime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);
    private readonly IPublishSettingsStore _settings;
    private readonly MediaHostStore _store;
    private readonly OAuthClientStore _clients;
    private readonly PublishHttpClients _http;
    private readonly InstagramConnectorOptions _instagramDefaults;
    private readonly TikTokConnectorOptions _tikTokDefaults;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile InstagramConnector _instagram;
    private volatile TikTokConnector _tikTok;
    private volatile TikTokOAuthProvider _tikTokOAuth;

    private readonly TimeProvider _time;
    private readonly OptionalScopeFlags _optionalScopes;

    public PlatformConnectorHost(IPublishSettingsStore settings, MediaHostStore store, OAuthClientStore clients, PublishHttpClients http,
        InstagramConnectorOptions instagramDefaults, TikTokConnectorOptions tikTokDefaults, IClock clock, OptionalScopeFlags optionalScopes)
    {
        _optionalScopes = optionalScopes;
        _settings = settings;
        _store = store;
        _clients = clients;
        _http = http;
        _instagramDefaults = instagramDefaults;
        _tikTokDefaults = tikTokDefaults;
        _time = new ClockTimeProvider(clock);
        // Trước lần nạp đầu (hoặc khi chưa có DB): không media host, chưa audit, không xin video.publish.
        _instagram = new InstagramConnector(instagramDefaults, null, _time);
        _tikTok = new TikTokConnector(Copy(tikTokDefaults, tikTokDefaults.DirectPostAudited), new VariantTikTokSettings(), _time);
        _tikTokOAuth = NewTikTokOAuth(false, false);
    }

    public ISocialConnector Instagram => _instagram;
    public ISocialConnector TikTok => _tikTok;
    public IOAuthProvider TikTokOAuth => _tikTokOAuth;

    public async Task ReloadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var audited = await PlatformFlags.TikTokAuditedAsync(_settings, ct);
            _tikTokOAuth = NewTikTokOAuth(await PlatformFlags.TikTokRequestPublishScopeAsync(_settings, ct),
                await PlatformFlags.TikTokRequestStatsScopesAsync(_settings, ct));
            // Provider Google/Meta đọc cờ mỗi lần dựng URL đăng nhập: đổi cờ có hiệu lực ở lần kết nối kế tiếp.
            _optionalScopes.YouTubeAnalytics = await PlatformFlags.YouTubeAnalyticsEnabledAsync(_settings, ct);
            _optionalScopes.MetaInsights = await PlatformFlags.MetaRequestInsightsScopesAsync(_settings, ct);

            var config = await _store.LoadAsync(ct);
            IPublicMediaHost? mediaHost = config is not null && ValidateMediaHost(config) is null ? new DynamicMediaHost(_store, _http, _time) : null;
            _tikTok = new TikTokConnector(Copy(_tikTokDefaults, audited), new VariantTikTokSettings(), _time, mediaHost);
            var options = new InstagramConnectorOptions
            {
                ApiTimeout = _instagramDefaults.ApiTimeout, ContainerPollInterval = _instagramDefaults.ContainerPollInterval,
                ContainerMaxPolls = _instagramDefaults.ContainerMaxPolls, FallbackPublishLimit = _instagramDefaults.FallbackPublishLimit,
                ShareToFeed = _instagramDefaults.ShareToFeed, ReconcileSlack = _instagramDefaults.ReconcileSlack,
                MediaUrlLifetime = config is null ? _instagramDefaults.MediaUrlLifetime : TimeSpan.FromHours(config.UrlLifetimeHours)
            };
            _instagram = config is not null && ValidateMediaHost(config) is null
                ? new InstagramConnector(options, new DynamicMediaHost(_store, _http, _time), _time)
                : new InstagramConnector(options, null, _time);
        }
        finally { _gate.Release(); }
    }

    public string? ValidateMediaHost(MediaHostConfig config)
    {
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out _)) return "Endpoint của media host phải là URL HTTPS đầy đủ (vd https://s3.ap-southeast-1.amazonaws.com).";
        return ToOptions(config).Validate();
    }

    public async Task<MediaHostTestDto> TestMediaHostAsync(MediaHostConfig config, CancellationToken ct)
    {
        var steps = new List<string>();
        var file = Path.Combine(Path.GetTempPath(), "centerpost-mediahost-" + Guid.NewGuid().ToString("N") + ".txt");
        var key = "centerpost-test-" + Guid.NewGuid().ToString("N") + ".txt";
        var content = "CenterPost media host test " + DateTimeOffset.UtcNow.ToString("O");
        S3CompatibleMediaHost? host = null;
        var uploaded = false;
        try
        {
            await File.WriteAllTextAsync(file, content, ct);
            var http = await _http.GetAsync(null, ct);
            host = new S3CompatibleMediaHost(http, ToOptions(config));
            var lease = await host.PublishAsync(key, new MediaFile(file, new FileInfo(file).Length), "text/plain", TimeSpan.FromMinutes(5), ct);
            uploaded = true;
            steps.Add($"PUT {config.KeyPrefix}{key}: thành công.");

            // URL có chữ ký: không ghi log, không trả về UI.
            using var response = await PlatformHttp.SendOnceAsync(http, new HttpRequestMessage(HttpMethod.Get, lease.Url), TestTimeout, "media host", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new MediaHostTestDto(false, steps, $"Đọc qua URL có chữ ký thất bại (HTTP {(int)response.StatusCode}): Instagram sẽ không tải được video.");
            if (body != content) return new MediaHostTestDto(false, steps, "Nội dung đọc về khác nội dung đã đưa lên (proxy/CDN sửa nội dung?).");
            steps.Add("GET qua URL có chữ ký (HTTPS công khai): đọc đúng nội dung.");

            await host.DeleteAsync(key, ct);
            uploaded = false;
            steps.Add("DELETE: đã xóa file thử.");
            return new MediaHostTestDto(true, steps, null);
        }
        catch (Exception e) when (e is ConnectorException or ArgumentException or IOException or NetworkRequestBlockedException or AccountRouteUnavailableException)
        {
            return new MediaHostTestDto(false, steps, e.Message);
        }
        finally
        {
            if (uploaded && host is not null)
                try { await host.DeleteAsync(key, CancellationToken.None); }
                catch (ConnectorException) { }
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }

    public async Task<TikTokCreatorInfoDto> QueryTikTokCreatorInfoAsync(ConnectorSession session, CancellationToken ct)
    {
        var info = await _tikTok.QueryCreatorInfoAsync(session, ct);
        return new TikTokCreatorInfoDto(info.Nickname, info.Username, info.AvatarUrl, info.PrivacyLevelOptions.ToList(), info.CommentDisabled,
            info.DuetDisabled, info.StitchDisabled, info.MaxVideoPostDurationSec, false);
    }

    internal static S3MediaHostOptions ToOptions(MediaHostConfig c) => new()
    {
        Endpoint = Uri.TryCreate(c.Endpoint, UriKind.Absolute, out var e) ? e : null, Region = c.Region, Bucket = c.Bucket, PathStyle = c.PathStyle,
        AccessKeyId = c.AccessKeyId, SecretAccessKey = c.SecretAccessKey, KeyPrefix = c.KeyPrefix
    };

    private TikTokOAuthProvider NewTikTokOAuth(bool requestPublishScope, bool requestStatsScopes) =>
        new(ct => _clients.GetAsync(SocialPlatform.TikTok, ct), requestPublishScope, requestStatsScopes);

    private static TikTokConnectorOptions Copy(TikTokConnectorOptions o, bool audited) => new()
    {
        DirectPostAudited = audited, ChunkSizeBytes = o.ChunkSizeBytes, ApiTimeout = o.ApiTimeout, ChunkTimeout = o.ChunkTimeout,
        InboxCompletionWindow = o.InboxCompletionWindow, InboxMaxDurationSeconds = o.InboxMaxDurationSeconds
    };
}

/// <summary>Đồng hồ của ứng dụng cho connector nền tảng (hạn 24h hộp thư TikTok tính cùng mốc với upload_completed_utc).</summary>
public sealed class ClockTimeProvider(IClock clock) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => clock.UtcNow;
}

/// <summary>Media host đọc cấu hình + khóa (DPAPI) mỗi lần dùng; HttpClient theo network profile mặc định.</summary>
public sealed class DynamicMediaHost(MediaHostStore store, PublishHttpClients http, TimeProvider? time = null) : IPublicMediaHost
{
    public async Task<PublicMediaLease> PublishAsync(string objectKey, MediaFile media, string contentType, TimeSpan lifetime, CancellationToken ct) =>
        await (await HostAsync(ct)).PublishAsync(objectKey, media, contentType, lifetime, ct);

    public async Task DeleteAsync(string objectKey, CancellationToken ct) => await (await HostAsync(ct)).DeleteAsync(objectKey, ct);

    private async Task<S3CompatibleMediaHost> HostAsync(CancellationToken ct)
    {
        var config = await store.LoadAsync(ct)
                     ?? throw new ConnectorException(ConnectorErrorKind.Permission, InstagramConnector.MissingHostMessage, reason: "media_host_missing");
        return new S3CompatibleMediaHost(await http.GetAsync(null, ct), PlatformConnectorHost.ToOptions(config), time);
    }
}

/// <summary>Đọc lựa chọn Direct Post từ JSON của publication; Inbox/không có → null (gửi vào hộp thư).</summary>
public sealed class VariantTikTokSettings : ITikTokPostSettingsProvider
{
    public Task<TikTokDirectPostSettings?> GetAsync(string connectionId, PublishVariant variant, CancellationToken ct)
    {
        var s = PlatformPostSettings.TikTokOf(variant.PlatformSettingsJson);
        return Task.FromResult(TikTokPostRules.IsDirectPost(s)
            ? new TikTokDirectPostSettings(s!.PrivacyLevel ?? "", s.AllowComment, s.AllowDuet, s.AllowStitch, s.DiscloseContent, s.YourBrand,
                s.BrandedContent, s.UserConsented)
            : null);
    }
}

/// <summary>Bọc connector dựng lại được lúc chạy: registry giữ bản bọc, mọi lời gọi tới bản hiện hành.</summary>
public sealed class DelegatingConnector(string platform, Func<ISocialConnector> current) : ISocialConnector, IMetricsConnector
{
    public string Platform => platform;
    public ISocialConnector Current => current();
    public IReadOnlyList<CapabilityInfo> GetCapabilities(string? grantedScopes) => current().GetCapabilities(grantedScopes);
    public Task<IReadOnlyList<CapabilityInfo>> GetCapabilitiesAsync(string connectionId, CancellationToken ct) => current().GetCapabilitiesAsync(connectionId, ct);
    public IReadOnlyList<ValidationIssue> Validate(PublishVariant variant, MediaFile media, DestinationInfo? destination) =>
        current().Validate(variant, media, destination);
    public Task<IReadOnlyList<DestinationInfo>> ListDestinationsAsync(ConnectorSession session, CancellationToken ct) =>
        current().ListDestinationsAsync(session, ct);
    public Task<UploadOutcome> UploadAsync(ConnectorSession session, PublishVariant variant, MediaFile media, UploadState state,
        IUploadCheckpoint checkpoint, CancellationToken ct) => current().UploadAsync(session, variant, media, state, checkpoint, ct);
    public Task<RemoteStatus> GetStatusAsync(ConnectorSession session, string remoteId, CancellationToken ct) =>
        current().GetStatusAsync(session, remoteId, ct);
    public Task<RemoteStatus> GetStatusAsync(ConnectorSession session, string remoteId, DateTimeOffset? uploadCompletedUtc, CancellationToken ct) =>
        current().GetStatusAsync(session, remoteId, uploadCompletedUtc, ct);
    public Task<ReconcileResult> ReconcileAsync(ConnectorSession session, DestinationInfo destination, ReconcileQuery query, CancellationToken ct) =>
        current().ReconcileAsync(session, destination, query, ct);
    public string? ParseRemoteId(string url) => current().ParseRemoteId(url);
    public string RemoteUrl(string remoteId) => current().RemoteUrl(remoteId);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<MetricValue>>> ReadPostMetricsAsync(ConnectorSession session, DestinationInfo destination,
        IReadOnlyList<PostMetricTarget> posts, MetricContext context, DateTimeOffset nowUtc, CancellationToken ct) =>
        Metrics().ReadPostMetricsAsync(session, destination, posts, context, nowUtc, ct);

    public Task<IReadOnlyList<MetricValue>> ReadChannelMetricsAsync(ConnectorSession session, DestinationInfo destination, MetricContext context,
        DateTimeOffset nowUtc, CancellationToken ct) => Metrics().ReadChannelMetricsAsync(session, destination, context, nowUtc, ct);

    private IMetricsConnector Metrics() => current() as IMetricsConnector
        ?? throw new ConnectorException(ConnectorErrorKind.Validation, $"Connector {platform} không hỗ trợ đọc thống kê.");
}

/// <summary>Bọc OAuth provider dựng lại được lúc chạy (TikTok: có/không xin video.publish).</summary>
public sealed class DelegatingOAuthProvider(string platform, string provider, Func<IOAuthProvider> current) : IOAuthProvider
{
    public string Platform => platform;
    public string Provider => provider;
    public IReadOnlyList<string> Scopes => current().Scopes;
    public string? ValidateClient(OAuthClientCredentials client) => current().ValidateClient(client);
    public Uri BuildAuthorizeUrl(OAuthClientCredentials client, Uri redirectUri, string state, string codeChallenge) =>
        current().BuildAuthorizeUrl(client, redirectUri, state, codeChallenge);
    public Task<OAuthTokenSet> ExchangeCodeAsync(HttpClient http, OAuthClientCredentials client, string code, Uri redirectUri, string codeVerifier,
        DateTimeOffset now, CancellationToken ct) => current().ExchangeCodeAsync(http, client, code, redirectUri, codeVerifier, now, ct);
    public Task<OAuthTokenSet> RefreshAsync(HttpClient http, OAuthClientCredentials client, string refreshToken, DateTimeOffset now, CancellationToken ct) =>
        current().RefreshAsync(http, client, refreshToken, now, ct);
    public Task RevokeAsync(HttpClient http, string token, CancellationToken ct) => current().RevokeAsync(http, token, ct);
}
