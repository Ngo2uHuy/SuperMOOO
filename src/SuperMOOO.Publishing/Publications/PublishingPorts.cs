using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Http;
using SuperMOOO.Publishing.Connectors.Meta;
using SuperMOOO.Publishing.Connectors.YouTube;

namespace SuperMOOO.Publishing.Publications;

/// <summary>Cổng nhận callback OAuth trên loopback (dùng chung LoopbackOAuthListener của app, cổng 8765).</summary>
public interface IOAuthCallbackListener : IAsyncDisposable
{
    Uri RedirectUri { get; }

    /// <summary>Chờ callback đúng path và đúng state (so sánh hằng thời gian); request sai bị từ chối và tiếp tục chờ.</summary>
    Task<OAuthCallback> WaitAsync(string expectedState, CancellationToken ct);
}

public sealed record OAuthCallback(string? Code, string? Error);

public interface IOAuthCallbackListenerFactory
{
    /// <param name="provider">youtube | meta | tiktok: đường callback /callback/{provider} (redirect URI người dùng đã đăng ký giữ nguyên).</param>
    /// <param name="host">Host trong redirect URI: 127.0.0.1 (mặc định) hoặc localhost (Meta ở chế độ Development chỉ tự cho phép localhost).</param>
    IOAuthCallbackListener Start(string provider, string host = "127.0.0.1");
}

/// <summary>
/// Chọn connector/OAuth provider theo nền tảng. Instagram/TikTok qua bản bọc của <see cref="PlatformConnectorHost"/> vì dựng lại theo cấu hình
/// người dùng đổi lúc chạy (media host, cờ audit Direct Post, scope).
/// </summary>
[Service]
public sealed class ConnectorRegistry
{
    private readonly IReadOnlyList<ISocialConnector> _connectors;
    private readonly IReadOnlyList<IOAuthProvider> _providers;

    public ConnectorRegistry(PlatformConnectorHost host, OptionalScopeFlags optionalScopes, YouTubeConnectorOptions youTube,
        FacebookPageConnectorOptions facebook, IClock clock)
    {
        _connectors =
        [
            new YouTubeConnector(youTube),
            new FacebookPageConnector(facebook, new ClockTimeProvider(clock)),
            new DelegatingConnector(nameof(SocialPlatform.Instagram), () => host.Instagram),
            new DelegatingConnector(nameof(SocialPlatform.TikTok), () => host.TikTok)
        ];
        _providers =
        [
            new GoogleOAuthProvider(optionalScopes),
            new MetaOAuthProvider(SocialPlatform.Facebook, optionalScopes),
            new MetaOAuthProvider(SocialPlatform.Instagram, optionalScopes),
            new DelegatingOAuthProvider(nameof(SocialPlatform.TikTok), "TikTok", () => host.TikTokOAuth)
        ];
    }

    public ISocialConnector? Find(SocialPlatform platform) =>
        _connectors.FirstOrDefault(c => string.Equals(c.Platform, platform.ToString(), StringComparison.OrdinalIgnoreCase));

    public IOAuthProvider? FindOAuth(SocialPlatform platform) =>
        _providers.FirstOrDefault(p => string.Equals(p.Platform, platform.ToString(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Bị hủy trong lúc upload: dừng gửi byte, không đổi trạng thái thêm.</summary>
public sealed class PublicationCancelledException() : Exception("Publication đã bị hủy.");

/// <param name="DeferUntil">Có giá trị: dời job tới mốc này (không tính lượt thử). Null khi không Proceed: job kết thúc, không đăng.</param>
public sealed record CampaignGateResult(bool Proceed, DateTimeOffset? DeferUntil, string? Message)
{
    public static CampaignGateResult Continue { get; } = new(true, null, null);
    public static CampaignGateResult Stop { get; } = new(false, null, null);
}

/// <summary>Cổng của chiến dịch trước khi job đăng theo lịch bắt đầu upload: chiến dịch tạm dừng → chờ; bài trễ → chính sách trễ lịch.</summary>
public interface ICampaignPublishGate
{
    Task<CampaignGateResult> CheckAsync(Publication publication, string jobId, CancellationToken ct);
}
