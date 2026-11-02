using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Analytics;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;

namespace SuperMOOO.App.Features.S16;

public sealed record OAuthPlatformOption(string Platform, string Label, string Callback);

[Service(ServiceLifetime.Transient)]
public sealed partial class PublishingSettingsViewModel(OAuthConnectService oauth, PlatformSettingsService platform, IPublishSettingsStore settings,
    PublishingRuntimeSettings runtime, IDialogService dialogs) : ObservableObject
{
    public IReadOnlyList<OAuthPlatformOption> Platforms { get; } =
    [
        new("YouTube", "YouTube", "youtube"), new("Facebook", "Meta (Facebook + Instagram)", "meta"), new("TikTok", "TikTok", "tiktok")
    ];

    public IReadOnlyList<string> MetaFlows { get; } = [OAuthFlows.Loopback, OAuthFlows.WebView];
    public IReadOnlyList<string> LoopbackHosts { get; } = [MetaLoopbackHosts.Localhost, MetaLoopbackHosts.Ip];
    public ObservableCollection<string> TestSteps { get; } = [];

    [ObservableProperty]
    public partial OAuthPlatformOption? SelectedPlatform { get; set; }

    [ObservableProperty]
    public partial string ClientId { get; set; } = "";

    [ObservableProperty]
    public partial string ClientStatus { get; set; } = "";

    [ObservableProperty]
    public partial string RedirectUri { get; set; } = "";

    [ObservableProperty]
    public partial string Endpoint { get; set; } = "";

    [ObservableProperty]
    public partial string Region { get; set; } = "auto";

    [ObservableProperty]
    public partial string Bucket { get; set; } = "";

    [ObservableProperty]
    public partial bool PathStyle { get; set; } = true;

    [ObservableProperty]
    public partial string AccessKeyId { get; set; } = "";

    [ObservableProperty]
    public partial string KeyPrefix { get; set; } = MediaHostConfig.DefaultPrefix;

    [ObservableProperty]
    public partial int UrlHours { get; set; } = MediaHostConfig.DefaultUrlLifetimeHours;

    [ObservableProperty]
    public partial string MediaHostStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool TikTokAudited { get; set; }

    [ObservableProperty]
    public partial bool TikTokPublishScope { get; set; }

    [ObservableProperty]
    public partial bool TikTokStatsScopes { get; set; }

    [ObservableProperty]
    public partial bool YouTubeAnalytics { get; set; }

    [ObservableProperty]
    public partial bool MetaInsights { get; set; }

    [ObservableProperty]
    public partial string MetaLoginFlow { get; set; } = OAuthFlows.Loopback;

    [ObservableProperty]
    public partial string MetaLoopbackHost { get; set; } = MetaLoopbackHosts.Localhost;

    [ObservableProperty]
    public partial int YouTubeDailyQuota { get; set; } = QuotaGuard.DefaultDailyQuota;

    [ObservableProperty]
    public partial int YouTubeInsertCost { get; set; } = QuotaGuard.DefaultInsertCost;

    [ObservableProperty]
    public partial int MaxPerDay { get; set; }

    [ObservableProperty]
    public partial int MinGapMinutes { get; set; }

    [ObservableProperty]
    public partial int BandwidthKbps { get; set; }

    [ObservableProperty]
    public partial int PublishConcurrency { get; set; }

    [ObservableProperty]
    public partial int BudgetYouTube { get; set; }

    [ObservableProperty]
    public partial int BudgetFacebook { get; set; }

    [ObservableProperty]
    public partial int BudgetInstagram { get; set; }

    [ObservableProperty]
    public partial int BudgetTikTok { get; set; }

    [ObservableProperty]
    public partial int RetentionDays { get; set; } = 30;

    [ObservableProperty]
    public partial bool HealthAutoPause { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    partial void OnSelectedPlatformChanged(OAuthPlatformOption? value)
    {
        if (value is null) return;
        RedirectUri = $"http://localhost:{LoopbackOAuthListener.DefaultPort}/callback/{value.Callback}";
        _ = LoadClientAsync(value);
    }

    public async Task LoadAsync()
    {
        SelectedPlatform = Platforms[0];
        try
        {
            var p = await platform.GetAsync(default);
            (TikTokAudited, TikTokPublishScope, TikTokStatsScopes, YouTubeAnalytics, MetaInsights, MetaLoginFlow, MetaLoopbackHost) =
                (p.TikTokDirectPostAudited, p.TikTokRequestPublishScope, p.TikTokRequestStatsScopes, p.YouTubeAnalyticsEnabled, p.MetaRequestInsightsScopes,
                p.MetaLoginFlow, p.MetaLoopbackHost);
            var host = await platform.GetMediaHostAsync(default);
            (Endpoint, Region, Bucket, PathStyle, KeyPrefix, UrlHours) = (host.Endpoint, host.Region, host.Bucket, host.PathStyle, host.KeyPrefix, host.UrlLifetimeHours);
            MediaHostStatus = host.Configured ? $"Đã cấu hình (access key {host.AccessKeyHint}). Để trống ô khoá để giữ khoá cũ." : "Chưa cấu hình (chỉ cần cho Instagram).";
            YouTubeDailyQuota = await IntAsync(SettingKeys.YouTubeDailyQuota, QuotaGuard.DefaultDailyQuota);
            YouTubeInsertCost = await IntAsync(SettingKeys.YouTubeInsertCost, QuotaGuard.DefaultInsertCost);
            MaxPerDay = await IntAsync(SettingKeys.PublishMaxPerDay, 0);
            MinGapMinutes = await IntAsync(SettingKeys.PublishMinGapMinutes, 0);
            BandwidthKbps = await IntAsync(SettingKeys.UploadBandwidthKbps, 0);
            PublishConcurrency = await IntAsync(SettingKeys.ConcurrencyPublish, 0);
            BudgetYouTube = await IntAsync(AnalyticsSettingsStore.BudgetKey(SocialPlatform.YouTube), 0);
            BudgetFacebook = await IntAsync(AnalyticsSettingsStore.BudgetKey(SocialPlatform.Facebook), 0);
            BudgetInstagram = await IntAsync(AnalyticsSettingsStore.BudgetKey(SocialPlatform.Instagram), 0);
            BudgetTikTok = await IntAsync(AnalyticsSettingsStore.BudgetKey(SocialPlatform.TikTok), 0);
            RetentionDays = await IntAsync(SettingKeys.AnalyticsRetentionDays, 30);
            HealthAutoPause = AnalyticsSettingsStore.AutoPause(await settings.GetAsync(SettingKeys.HealthAutoPauseCampaigns, default));
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    private async Task LoadClientAsync(OAuthPlatformOption option)
    {
        try
        {
            var status = await oauth.GetClientStatusAsync(new GetOAuthClientStatusRequest(option.Platform), default);
            ClientId = "";
            ClientStatus = status.Configured ? $"Đã lưu (Client ID {status.ClientIdHint}). Nhập lại để thay." : "Chưa cấu hình.";
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    /// <summary>Client secret và secret key nhập qua PasswordBox nên truyền vào đây thay vì binding.</summary>
    public async Task SaveClientAsync(string clientSecret)
    {
        if (SelectedPlatform is null) return;
        try
        {
            var status = await oauth.SetClientAsync(new SetOAuthClientRequest(SelectedPlatform.Platform, ClientId, clientSecret), default);
            ClientId = "";
            ClientStatus = $"Đã lưu (Client ID {status.ClientIdHint}).";
            dialogs.Info("Đăng bài", "Đã lưu OAuth client (mã hoá DPAPI).");
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    public async Task SaveMediaHostAsync(string secret)
    {
        try
        {
            var dto = await platform.SetMediaHostAsync(Request(secret), default);
            MediaHostStatus = $"Đã lưu (access key {dto.AccessKeyHint}).";
            dialogs.Info("Đăng bài", "Đã lưu media host.");
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    public async Task TestMediaHostAsync(string secret)
    {
        TestSteps.Clear();
        try
        {
            var result = await platform.TestMediaHostAsync(Request(secret), default);
            foreach (var step in result.Steps) TestSteps.Add(step);
            if (result.Ok) dialogs.Info("Media host", "Kết nối thành công.");
            else dialogs.Error(result.Error ?? "Kiểm tra media host thất bại.");
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    private SetMediaHostRequest Request(string secret) =>
        new(Endpoint, Region, Bucket, PathStyle, AccessKeyId, secret, KeyPrefix, UrlHours);

    [RelayCommand]
    private async Task ClearMediaHostAsync()
    {
        if (!await dialogs.ConfirmAsync("Xoá media host", "Xoá cấu hình và khoá truy cập media host? Instagram sẽ không đăng được cho tới khi cấu hình lại.")) return;
        await platform.ClearMediaHostAsync(default);
        MediaHostStatus = "Chưa cấu hình (chỉ cần cho Instagram).";
        Endpoint = Bucket = AccessKeyId = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await platform.SetAsync(new PlatformSettingsDto(TikTokAudited, TikTokPublishScope, MetaLoginFlow, MetaLoopbackHost, YouTubeAnalytics, TikTokStatsScopes,
                MetaInsights), default);
            await settings.SetAsync(SettingKeys.YouTubeDailyQuota, Math.Max(1, YouTubeDailyQuota).ToString(), default);
            await settings.SetAsync(SettingKeys.YouTubeInsertCost, Math.Max(1, YouTubeInsertCost).ToString(), default);
            await settings.SetAsync(SettingKeys.PublishMaxPerDay, Math.Max(0, MaxPerDay).ToString(), default);
            await settings.SetAsync(SettingKeys.PublishMinGapMinutes, Math.Max(0, MinGapMinutes).ToString(), default);
            await settings.SetAsync(SettingKeys.UploadBandwidthKbps, Math.Max(0, BandwidthKbps).ToString(), default);
            await settings.SetAsync(SettingKeys.ConcurrencyPublish, Math.Max(0, PublishConcurrency).ToString(), default);
            await settings.SetAsync(AnalyticsSettingsStore.BudgetKey(SocialPlatform.YouTube), Math.Max(0, BudgetYouTube).ToString(), default);
            await settings.SetAsync(AnalyticsSettingsStore.BudgetKey(SocialPlatform.Facebook), Math.Max(0, BudgetFacebook).ToString(), default);
            await settings.SetAsync(AnalyticsSettingsStore.BudgetKey(SocialPlatform.Instagram), Math.Max(0, BudgetInstagram).ToString(), default);
            await settings.SetAsync(AnalyticsSettingsStore.BudgetKey(SocialPlatform.TikTok), Math.Max(0, BudgetTikTok).ToString(), default);
            await settings.SetAsync(SettingKeys.AnalyticsRetentionDays, Math.Max(1, RetentionDays).ToString(), default);
            await settings.SetAsync(SettingKeys.HealthAutoPauseCampaigns, HealthAutoPause ? "true" : "false", default);
            await runtime.ApplyAsync();
            Status = "Đã lưu.";
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    private async Task<int> IntAsync(string key, int fallback) =>
        int.TryParse(await settings.GetAsync(key, default), out var value) ? value : fallback;
}
