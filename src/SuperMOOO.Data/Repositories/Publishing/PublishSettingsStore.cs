using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Data.Repositories.Publishing;

public sealed class PublishSettings
{
    public Dictionary<string, string> Values { get; set; } = [];
}

[Service(As = [typeof(IPublishSettingsStore)])]
public sealed class PublishSettingsStore(ISettingsStore settings) : IPublishSettingsStore
{
    public const string Section = "publishing";

    // Mặc định lấy từ các dòng INSERT OR IGNORE INTO settings của migration CenterPost 0001–0008.
    private static readonly Dictionary<string, string> Defaults = new()
    {
        [SettingKeys.ProxyTestUrl] = SettingKeys.DefaultProxyTestUrl,
        [SettingKeys.YouTubeInsertCost] = "1600",
        [SettingKeys.YouTubeDailyQuota] = "10000",
        [SettingKeys.YouTubeChunkSizeKb] = "8192",
        [SettingKeys.FfmpegPath] = "",
        [SettingKeys.TikTokDirectPostAudited] = "false",
        [SettingKeys.TikTokRequestPublishScope] = "false",
        [SettingKeys.MetaLoginFlow] = "Loopback",
        [SettingKeys.MetaLoopbackHost] = "localhost",
        [SettingKeys.YouTubeAnalyticsEnabled] = "false",
        [SettingKeys.TikTokRequestStatsScopes] = "false",
        [SettingKeys.MetaRequestInsightsScopes] = "false",
        [SettingKeys.AnalyticsRetentionDays] = "30",
        [SettingKeys.HealthAutoPauseCampaigns] = "false",
        [SettingKeys.AnalyticsBudgetPrefix + "youtube"] = "400",
        [SettingKeys.AnalyticsBudgetPrefix + "facebook"] = "200",
        [SettingKeys.AnalyticsBudgetPrefix + "instagram"] = "200",
        [SettingKeys.AnalyticsBudgetPrefix + "tiktok"] = "200",
    };

    // Cả section là một bản ghi JSON: đọc-sửa-ghi song song không khoá sẽ làm mất khoá vừa ghi của bên kia.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var values = settings.Get<PublishSettings>(Section).Values;
        return Task.FromResult(values.TryGetValue(key, out var v) ? v : Defaults.GetValueOrDefault(key));
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = settings.Get<PublishSettings>(Section);
            current.Values[key] = value;
            await settings.SetAsync(Section, current, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
