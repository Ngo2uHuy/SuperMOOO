using System.Globalization;
using System.Text.Json;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Http;

namespace SuperMOOO.Publishing.Publications;

/// <summary>Khả năng thật của một đích: quyền đã cấp + cờ audit + media host (connector tính) + quyền trên Page (thuộc tính lúc đồng bộ).</summary>
public static class DestinationCapabilities
{
    /// <summary>Thuộc tính đích: khóa token riêng trong vault (Meta page id).</summary>
    public const string TokenRefAttribute = "tokenRef";
    /// <summary>Thuộc tính đích: "false" khi người dùng không có quyền tạo nội dung trên Page.</summary>
    public const string CanPublishAttribute = "canPublish";

    public static IReadOnlyList<CapabilityInfo> For(ISocialConnector connector, SocialConnection connection, SocialDestination destination)
    {
        var caps = connector.GetCapabilities(connection.Scopes).ToList();
        if (Attribute(destination, CanPublishAttribute) == "false")
        {
            var i = caps.FindIndex(c => c.Capability == Capability.UploadVideo);
            var denied = new CapabilityInfo(Capability.UploadVideo, CapabilityStatus.MissingPermission,
                "Bạn không có quyền tạo nội dung trên Page này (cần vai trò có quyền CREATE_CONTENT).");
            if (i >= 0) caps[i] = denied;
            else caps.Add(denied);
        }
        return caps;
    }

    /// <summary>
    /// Lý do không đăng tự động được (null = được). TikTok "cần thao tác" là luồng hộp thư hợp lệ (người dùng hoàn tất trong app);
    /// nền tảng khác "cần thao tác" (vd Instagram chưa có media host) thì không đăng tự động.
    /// </summary>
    public static string? AutoPublishBlockedReason(SocialPlatform platform, IReadOnlyList<CapabilityInfo> caps)
    {
        var upload = caps.FirstOrDefault(c => c.Capability == Capability.UploadVideo);
        return upload?.Status switch
        {
            null => "Nền tảng chưa có connector.",
            CapabilityStatus.Supported => null,
            CapabilityStatus.RequiresManualStep when platform == SocialPlatform.TikTok => null,
            _ => upload.Note ?? "Tài khoản chưa cấp quyền upload video."
        };
    }

    public static string? Attribute(SocialDestination destination, string key) =>
        PublicationService.ToInfo(destination).Attributes.GetValueOrDefault(key);

    public static string? TokenRef(SocialDestination destination) => Attribute(destination, TokenRefAttribute);

    public static bool HasScope(string? scopes, string scope) =>
        (scopes ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);
}

/// <summary>JSON lựa chọn riêng theo nền tảng lưu ở publication/chiến dịch.</summary>
public static class PlatformPostSettings
{
    public static string? Serialize(TikTokPostSettingsDto? tikTok) =>
        tikTok is null ? null : JsonSerializer.Serialize(new PlatformPostSettingsDto(tikTok), PublishingJson.Options);

    public static TikTokPostSettingsDto? TikTokOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PlatformPostSettingsDto>(json, PublishingJson.Options)?.TikTok; }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Điều kiện của TikTok cho Direct Post (content-sharing-guidelines + audit): chọn privacy (không mặc định), đồng ý Music Usage,
/// công bố nội dung thương mại hợp lệ, chưa audit thì chỉ SELF_ONLY. Inbox không cần form (người dùng hoàn tất trong app).
/// </summary>
public static class TikTokPostRules
{
    public const string PublishScope = "video.publish";
    public const string UnauditedWarning =
        "App TikTok chưa qua audit Direct Post: bài chỉ hiển thị với chính bạn (SELF_ONLY), tối đa 5 người dùng/24h.";

    private static readonly string[] Levels =
    [
        TikTokPrivacyLevels.PublicToEveryone, TikTokPrivacyLevels.MutualFollowFriends, TikTokPrivacyLevels.FollowerOfCreator, TikTokPrivacyLevels.SelfOnly
    ];

    public static bool IsDirectPost(TikTokPostSettingsDto? s) => s?.Mode == TikTokPostModes.DirectPost;

    /// <returns>Danh sách lỗi; rỗng = hợp lệ.</returns>
    public static List<string> Validate(TikTokPostSettingsDto? s, bool audited, string? grantedScopes)
    {
        var errors = new List<string>();
        if (s is null || s.Mode == TikTokPostModes.Inbox) return errors;
        if (s.Mode != TikTokPostModes.DirectPost)
        {
            errors.Add("Chế độ đăng TikTok không hợp lệ.");
            return errors;
        }
        if (!DestinationCapabilities.HasScope(grantedScopes, PublishScope))
            errors.Add("Tài khoản TikTok chưa cấp quyền video.publish (Direct Post): bật \"Xin quyền Direct Post\" rồi kết nối lại, hoặc dùng chế độ Inbox.");
        if (string.IsNullOrWhiteSpace(s.PrivacyLevel)) errors.Add("Chọn ai được xem bài TikTok (không có lựa chọn mặc định).");
        else if (!Levels.Contains(s.PrivacyLevel)) errors.Add("Quyền riêng tư TikTok không hợp lệ.");
        else if (!audited && s.PrivacyLevel != TikTokPrivacyLevels.SelfOnly)
            errors.Add("App TikTok chưa qua audit Direct Post: chỉ được chọn Chỉ mình tôi (SELF_ONLY).");
        if (s.DiscloseContent && !s.YourBrand && !s.BrandedContent)
            errors.Add("Đã bật công bố nội dung: chọn Your brand và/hoặc Branded content.");
        if (s.DiscloseContent && s.BrandedContent && s.PrivacyLevel == TikTokPrivacyLevels.SelfOnly)
            errors.Add("Branded content không được để chế độ Chỉ mình tôi.");
        if (!s.UserConsented) errors.Add("Cần đồng ý Music Usage Confirmation" + (s.DiscloseContent && s.BrandedContent ? " và Branded Content Policy" : "") + " trước khi đăng.");
        return errors;
    }

    /// <summary>Bỏ các cờ thương mại khi đã tắt công bố để JSON lưu đúng lựa chọn thật.</summary>
    public static TikTokPostSettingsDto Normalize(TikTokPostSettingsDto s) =>
        s.Mode == TikTokPostModes.DirectPost
            ? s with { PrivacyLevel = string.IsNullOrWhiteSpace(s.PrivacyLevel) ? null : s.PrivacyLevel.Trim(),
                YourBrand = s.DiscloseContent && s.YourBrand, BrandedContent = s.DiscloseContent && s.BrandedContent }
            : new TikTokPostSettingsDto(TikTokPostModes.Inbox);
}

/// <summary>Cờ cài đặt nền tảng đọc từ bảng settings.</summary>
public static class PlatformFlags
{
    public static async Task<bool> TikTokAuditedAsync(IPublishSettingsStore settings, CancellationToken ct) =>
        IsTrue(await settings.GetAsync(SettingKeys.TikTokDirectPostAudited, ct));

    public static async Task<bool> TikTokRequestPublishScopeAsync(IPublishSettingsStore settings, CancellationToken ct) =>
        IsTrue(await settings.GetAsync(SettingKeys.TikTokRequestPublishScope, ct));

    public static async Task<bool> YouTubeAnalyticsEnabledAsync(IPublishSettingsStore settings, CancellationToken ct) =>
        IsTrue(await settings.GetAsync(SettingKeys.YouTubeAnalyticsEnabled, ct));

    public static async Task<bool> TikTokRequestStatsScopesAsync(IPublishSettingsStore settings, CancellationToken ct) =>
        IsTrue(await settings.GetAsync(SettingKeys.TikTokRequestStatsScopes, ct));

    public static async Task<bool> MetaRequestInsightsScopesAsync(IPublishSettingsStore settings, CancellationToken ct) =>
        IsTrue(await settings.GetAsync(SettingKeys.MetaRequestInsightsScopes, ct));

    public static async Task<string> MetaLoginFlowAsync(IPublishSettingsStore settings, CancellationToken ct) =>
        await settings.GetAsync(SettingKeys.MetaLoginFlow, ct) is OAuthFlows.WebView ? OAuthFlows.WebView : OAuthFlows.Loopback;

    public static async Task<string> MetaLoopbackHostAsync(IPublishSettingsStore settings, CancellationToken ct) =>
        await settings.GetAsync(SettingKeys.MetaLoopbackHost, ct) is MetaLoopbackHosts.Ip ? MetaLoopbackHosts.Ip : MetaLoopbackHosts.Localhost;

    public static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
