namespace SuperMOOO.Data.Legacy;

public enum LegacyTargetKind { Secret, Setting, Network, Skip, Unknown }

/// <summary>Đích của một khoá bảng settings cũ. Rank cao hơn thắng khi nhiều khoá cũ cùng trỏ một đích (F35).</summary>
public sealed record LegacyTarget(LegacyTargetKind Kind, string Scope = "", string Name = "", int Rank = 0);

/// <summary>Bảng khoá `settings` cũ → secret/app_setting theo §3.4, kèm bảng alias (xhs → xiaohongshu).</summary>
public static class LegacySettingsMap
{
    private static readonly string[] CookiePlatforms =
        ["tiktok", "facebook", "instagram", "youtube", "douyin", "xiaohongshu", "xhs", "weibo", "bilibili", "kuaishou", "twitter"];

    private static readonly string[] TokenPlatforms =
        ["tiktok", "youtube", "weibo", "douyin", "bilibili", "kuaishou", "xiaohongshu", "xhs", "twitter", "meta", "facebook", "instagram"];

    private static readonly Dictionary<string, LegacyTarget> Fixed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bilibili_sessdata"] = Secret("bilibili", "sessdata"),
        ["twitter_bearer_token"] = Secret("twitter", "bearer_token"),
        ["youtube_api_key"] = Secret("youtube", "api_key"),
        ["meta_ads_token"] = Secret("meta", "ads_token"),
        ["tiktok_provider_api_key"] = Secret("tiktok", "provider_api_key"),
        ["telegram_bot_token"] = new(LegacyTargetKind.Secret, "notify:telegram", "bot_token"),
        ["telegram_chat_id"] = new(LegacyTargetKind.Setting, "notify.telegram", "ChatId"),
        ["youtube_daily_quota"] = new(LegacyTargetKind.Setting, "quota", "YoutubeDailyQuota"),
        ["meta_daily_quota"] = new(LegacyTargetKind.Setting, "quota", "MetaDailyQuota"),
        ["tiktok_daily_credits"] = new(LegacyTargetKind.Setting, "quota", "TiktokDailyCredits"),
        ["tiktok_provider_url"] = new(LegacyTargetKind.Setting, "social.tiktok", "ProviderUrl"),
        ["tiktok_engine_mode"] = new(LegacyTargetKind.Setting, "social.tiktok", "EngineMode"),
        ["tiktok_explore_query"] = new(LegacyTargetKind.Setting, "social.tiktok", "ExploreQuery"),
        ["proxy"] = new(LegacyTargetKind.Network),
        ["proxy_profile_name"] = new(LegacyTargetKind.Network),
        ["proxy_region"] = new(LegacyTargetKind.Network),
        ["crawler_network_v1"] = new(LegacyTargetKind.Network),
        ["test_key"] = new(LegacyTargetKind.Skip),
        ["master_test_key"] = new(LegacyTargetKind.Skip),
        ["gemini_api_key"] = new(LegacyTargetKind.Skip)
    };

    public static string CanonicalPlatform(string platform) => platform == "xhs" ? "xiaohongshu" : platform;

    public static LegacyTarget Resolve(string key)
    {
        if (Fixed.TryGetValue(key, out var fixedTarget)) return fixedTarget;

        foreach (var platform in CookiePlatforms)
            if (key.Equals(platform + "_cookie", StringComparison.OrdinalIgnoreCase))
                return Secret(CanonicalPlatform(platform), "cookie");

        foreach (var platform in TokenPlatforms)
        {
            if (key.Equals(platform + "_access_token", StringComparison.OrdinalIgnoreCase)) return Secret(CanonicalPlatform(platform), "token", 2);
            if (key.Equals(platform + "_token", StringComparison.OrdinalIgnoreCase)) return Secret(CanonicalPlatform(platform), "token", 1);
        }

        return new LegacyTarget(LegacyTargetKind.Unknown, "legacy", key);
    }

    private static LegacyTarget Secret(string platform, string name, int rank = 0) =>
        new(LegacyTargetKind.Secret, $"platform:{platform}", name, rank);
}
