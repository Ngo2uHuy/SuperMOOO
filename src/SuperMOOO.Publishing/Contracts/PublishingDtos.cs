namespace SuperMOOO.Publishing.Contracts;

// --- OAuth: client id/secret đi một chiều vào Worker (DPAPI); token không bao giờ đi qua IPC ---

public sealed record GetOAuthClientStatusRequest(string Platform);

public sealed record SetOAuthClientRequest(string Platform, string ClientId, string ClientSecret);

/// <param name="ClientIdHint">Client ID đã che bớt để người dùng nhận ra; secret không bao giờ trả về.</param>
public sealed record OAuthClientStatusDto(string Platform, bool Configured, string? ClientIdHint);

public static class OAuthFlows
{
    /// <summary>Trình duyệt hệ thống + redirect về loopback trên máy.</summary>
    public const string Loopback = "Loopback";
    /// <summary>Cửa sổ WebView2 của chính tài khoản (profile + proxy riêng); App bắt điều hướng tới <see cref="OAuthStartDto.CaptureUrlPrefix"/>.</summary>
    public const string WebView = "WebView";
}

/// <param name="Flow">null = theo cài đặt nền tảng (Meta: meta_login_flow; nền tảng khác luôn Loopback).</param>
public sealed record StartOAuthConnectRequest(string ConnectionId, string? Flow = null);

/// <summary>
/// Loopback: App mở <see cref="AuthorizeUrl"/> bằng trình duyệt hệ thống rồi poll GetOAuthConnectStatus.
/// WebView: App mở URL trong cửa sổ WebView2 của tài khoản, khi điều hướng bắt đầu bằng <see cref="CaptureUrlPrefix"/>
/// thì hủy điều hướng và gửi nguyên URL qua CompleteOAuthDesktop.
/// </summary>
public sealed record OAuthStartDto(string SessionId, string AuthorizeUrl, DateTimeOffset ExpiresUtc, string Flow = OAuthFlows.Loopback,
    string? CaptureUrlPrefix = null);

/// <param name="RedirectUrl">URL WebView2 vừa bị chặn (có token ở fragment); Worker kiểm tra state trước khi nhận.</param>
public sealed record CompleteOAuthDesktopRequest(string SessionId, string RedirectUrl);

public sealed record OAuthSessionRequest(string SessionId);

public static class OAuthSessionStates
{
    public const string Waiting = "Waiting";
    public const string Exchanging = "Exchanging";
    public const string Connected = "Connected";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";

    public static bool IsFinal(string state) => state is Connected or Failed or Expired or Cancelled;
}

public sealed record OAuthSessionDto(string SessionId, string ConnectionId, string State, string? Message);

/// <param name="RevokeAtPlatform">true = thu hồi quyền tại nền tảng rồi xóa token; false = chỉ xóa token trên máy.</param>
public sealed record DisconnectConnectionRequest(string ConnectionId, bool RevokeAtPlatform);

public sealed record SyncDestinationsRequest(string ConnectionId);

public sealed record ListDestinationsRequest(string? ConnectionId);

public sealed record CapabilityDto(string Capability, string Status, string? Note);

/// <param name="Capabilities">Tính lại lúc đọc theo quyền đã cấp + cờ audit + media host (không phải snapshot cũ).</param>
/// <param name="SupportsDirectPost">TikTok: đã cấp video.publish nên chọn được Direct Post.</param>
/// <param name="AutoPublishBlockedReason">Lý do không đăng tự động được (vd Instagram chưa có media host); null = được.</param>
public sealed record DestinationDto(
    string Id, string ConnectionId, string ConnectionName, string ConnectionApiStatus, string Platform, string RemoteId,
    string Title, string? Url, bool Enabled, bool Removed, DateTimeOffset SyncedUtc, List<CapabilityDto> Capabilities,
    bool SupportsDirectPost = false, string? AutoPublishBlockedReason = null);

public static class CapabilityNames
{
    public const string UploadVideo = "UploadVideo";
    public const string UploadImages = "UploadImages";
}

public static class CapabilityStatuses
{
    public const string Supported = "Supported";
    public const string MissingPermission = "MissingPermission";
    public const string RequiresManualStep = "RequiresManualStep";
    public const string NotSupported = "NotSupported";
}

// --- Cài đặt nền tảng (Meta/TikTok/media host) ---

public static class MetaLoopbackHosts
{
    public const string Localhost = "localhost";
    public const string Ip = "127.0.0.1";
}

/// <param name="TikTokDirectPostAudited">App TikTok đã qua audit Direct Post (mặc định tắt: Direct Post chỉ SELF_ONLY).</param>
/// <param name="TikTokRequestPublishScope">Xin thêm scope video.publish khi kết nối (chỉ bật khi app đã có sản phẩm Direct Post).</param>
/// <param name="MetaLoginFlow"><see cref="OAuthFlows"/>.</param>
/// <param name="MetaLoopbackHost"><see cref="MetaLoopbackHosts"/>.</param>
/// <param name="YouTubeAnalyticsEnabled">Xin yt-analytics.readonly ở lần kết nối kế tiếp (phải kết nối lại YouTube để có quyền).</param>
/// <param name="TikTokRequestStatsScopes">Xin video.list + user.info.stats ở lần kết nối kế tiếp.</param>
/// <param name="MetaRequestInsightsScopes">Xin read_insights + instagram_manage_insights ở lần kết nối kế tiếp.</param>
public sealed record PlatformSettingsDto(bool TikTokDirectPostAudited, bool TikTokRequestPublishScope, string MetaLoginFlow, string MetaLoopbackHost,
    bool YouTubeAnalyticsEnabled = false, bool TikTokRequestStatsScopes = false, bool MetaRequestInsightsScopes = false);

/// <param name="AccessKeyHint">Access key đã che; secret không bao giờ trả về.</param>
/// <param name="UrlLifetimeHours">Hạn URL đọc có chữ ký đưa cho Instagram.</param>
public sealed record MediaHostDto(
    bool Configured, string Endpoint, string Region, string Bucket, bool PathStyle, string? AccessKeyHint, bool HasSecret, string KeyPrefix,
    int UrlLifetimeHours);

/// <param name="AccessKeyId">Rỗng = giữ access key đã lưu.</param>
/// <param name="SecretAccessKey">Rỗng = giữ secret đã lưu.</param>
public sealed record SetMediaHostRequest(
    string Endpoint, string Region, string Bucket, bool PathStyle, string? AccessKeyId, string? SecretAccessKey, string KeyPrefix,
    int UrlLifetimeHours);

public sealed record MediaHostTestDto(bool Ok, List<string> Steps, string? Error);

// --- TikTok ---

public static class TikTokPostModes
{
    /// <summary>Gửi vào hộp thư TikTok; người dùng nhập caption và đăng trong app (hạn 24h).</summary>
    public const string Inbox = "Inbox";
    /// <summary>Đăng trực tiếp (scope video.publish); bắt buộc form theo hướng dẫn của TikTok.</summary>
    public const string DirectPost = "DirectPost";
}

public static class TikTokPrivacyLevels
{
    public const string PublicToEveryone = "PUBLIC_TO_EVERYONE";
    public const string MutualFollowFriends = "MUTUAL_FOLLOW_FRIENDS";
    public const string FollowerOfCreator = "FOLLOWER_OF_CREATOR";
    public const string SelfOnly = "SELF_ONLY";
}

/// <param name="PrivacyLevel">Một trong privacy_level_options của creator_info; người dùng tự chọn (không chọn sẵn).</param>
/// <param name="DiscloseContent">Công tắc "Disclose video content"; bật thì phải chọn YourBrand và/hoặc BrandedContent.</param>
/// <param name="UserConsented">Người dùng đã đồng ý Music Usage Confirmation (+ Branded Content Policy khi có).</param>
public sealed record TikTokPostSettingsDto(
    string Mode, string? PrivacyLevel = null, bool AllowComment = false, bool AllowDuet = false, bool AllowStitch = false,
    bool DiscloseContent = false, bool YourBrand = false, bool BrandedContent = false, bool UserConsented = false);

/// <summary>JSON lưu ở publication/chiến dịch: lựa chọn riêng theo nền tảng.</summary>
public sealed record PlatformPostSettingsDto(TikTokPostSettingsDto? TikTok = null);

public sealed record GetTikTokCreatorInfoRequest(string DestinationId);

/// <param name="DirectPostAudited">Cờ audit hiện tại: chưa audit thì chỉ được SELF_ONLY.</param>
public sealed record TikTokCreatorInfoDto(
    string? Nickname, string? Username, string? AvatarUrl, List<string> PrivacyLevelOptions, bool CommentDisabled, bool DuetDisabled,
    bool StitchDisabled, int? MaxVideoPostDurationSec, bool DirectPostAudited);

/// <summary>Hoàn tất/sửa form TikTok của bài chưa upload (nháp hoặc chờ duyệt).</summary>
public sealed record UpdatePublicationTikTokRequest(string Id, TikTokPostSettingsDto Settings);

// --- Publication ---

public static class PublishModes
{
    public const string Draft = "Draft";
    public const string Now = "Now";
    public const string Schedule = "Schedule";
}

/// <param name="ScheduledUtc">Giờ riêng của đích này (null = dùng giờ chung của yêu cầu).</param>
/// <param name="TikTok">Chỉ cho đích TikTok; null = Inbox.</param>
public sealed record PublicationTargetRequest(
    string ContentItemId, string DestinationId, string Title, string? Description, List<string>? Tags,
    string Privacy, bool MadeForKids, bool ContainsSyntheticMedia, DateTimeOffset? ScheduledUtc = null, TikTokPostSettingsDto? TikTok = null,
    string? PlanKey = null);

/// <param name="UseNativeSchedule">Upload sớm ở chế độ riêng tư và để nền tảng tự công khai đúng giờ (YouTube publishAt).</param>
/// <param name="AcknowledgeWarnings">Người dùng đã xem và chấp nhận cảnh báo rủi ro (Block thì vẫn không tạo).</param>
/// <param name="AllowRepost">Chủ ý đăng lại video đã có publication trên cùng kênh.</param>
/// <param name="Origin">Nơi tạo: manual | auto_workflow (S07) | auto_reup (S08) | campaign (<see cref="PublicationOrigins"/>).</param>
/// <param name="OriginRef">Định danh phía nguồn (vd id workflow), dùng để liệt kê lại bài của nguồn.</param>
public sealed record CreatePublicationsRequest(
    List<PublicationTargetRequest> Items, string Mode, DateTimeOffset? ScheduledUtc, bool UseNativeSchedule,
    bool AcknowledgeWarnings, bool AllowRepost, string Origin = PublicationOrigins.Manual, string? OriginRef = null,
    bool AllowRepeatedSchedule = false);

public static class RiskLevels
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Block = "Block";
}

public sealed record PublishWarningDto(string? ContentItemId, string? DestinationId, string Level, string Code, string Message);

/// <param name="NeedsConfirmation">Có cảnh báo/đăng lại cần người dùng xác nhận: gửi lại với AcknowledgeWarnings/AllowRepost.</param>
public sealed record CreatePublicationsResultDto(
    bool Created, bool Blocked, bool NeedsConfirmation, List<string> PublicationIds, List<PublishWarningDto> Warnings, List<string> Errors);

/// <summary>Lên lịch/đăng ngay một bài nháp; cùng kiểm tra rủi ro như khi tạo.</summary>
public sealed record SchedulePublicationRequest(
    string Id, string Mode, DateTimeOffset? ScheduledUtc, bool UseNativeSchedule, bool AcknowledgeWarnings, bool AllowRepost = false);

public sealed record ListPublicationsRequest(
    string? Status, string? DestinationId, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, int Page = 1, int PageSize = 50,
    string? CampaignId = null);

public sealed record PublicationDto(
    string Id, string ContentItemId, string ContentTitle, string DestinationId, string DestinationTitle,
    string ConnectionId, string ConnectionName, string Platform, string Title, string Privacy, string Status,
    DateTimeOffset? ScheduledUtc, bool UseNativeSchedule, long UploadBytesConfirmed, long? UploadBytesTotal, int Attempts,
    string? RemoteId, string? RemoteUrl, string? LastError, DateTimeOffset? PublishedUtc,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, bool CanCancel, bool CanRetry,
    string? CampaignId = null, string? CampaignName = null, bool CanReschedule = false,
    DateTimeOffset? AwaitingUserUntilUtc = null, string? TikTokMode = null, bool NeedsTikTokForm = false);

public sealed record PublishAttemptDto(DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc, string? Outcome, string? Error);

public sealed record PublicationDetailDto(
    PublicationDto Summary, string Description, List<string> Tags, bool MadeForKids, bool ContainsSyntheticMedia,
    bool IntentionalRepost, List<PublishAttemptDto> Attempts);

public sealed record PublicationIdRequest(string Id);

public static class UnknownResolutions
{
    /// <summary>Người dùng xác nhận video đã lên và nhập URL: gắn remote id rồi kiểm tra trạng thái qua API.</summary>
    public const string MarkPublished = "MarkPublished";
    /// <summary>Chủ ý upload lại (có thể tạo video trùng).</summary>
    public const string Repost = "Repost";
    public const string Cancel = "Cancel";
}

public sealed record ResolveUnknownPublicationRequest(string Id, string Resolution, string? RemoteUrl, bool ConfirmRepost);

/// <summary>Khoảng "hôm nay" theo giờ máy người dùng, App tự tính.</summary>
public sealed record GetPublishSummaryRequest(DateTimeOffset DayStartUtc, DateTimeOffset DayEndUtc);

/// <param name="QuotaLimit">0 = chưa cấu hình OAuth client YouTube.</param>
public sealed record PublishSummaryDto(
    int Today, int TodayPublished, int Failed, int NeedsAction, int Unknown, int InProgress, int QuotaUsed, int QuotaLimit);

public static class PublishingJson
{
    /// <summary>JSON lưu trong DB (tags, lựa chọn nền tảng): giữ đúng định dạng CenterPost (camelCase, enum dạng chữ).</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public const int MaxPageSize = 200;
}

public static class PublishingErrors
{
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";
    public const string VpnDown = "vpn_down";
}

/// <summary>Lỗi do thao tác của người dùng (dữ liệu nhập sai, không tìm thấy, VPN rớt); UI hiện <see cref="Exception.Message"/>.</summary>
public sealed class PublishingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record PagedDto<T>(List<T> Items, int Total, int Page, int PageSize);
