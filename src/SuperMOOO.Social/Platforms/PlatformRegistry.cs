namespace SuperMOOO.Social.Platforms;

/// <summary>Bí mật của một nền tảng lưu ở ISecretStore: scope `platform:&lt;id&gt;`, tên trong <see cref="PlatformInfo.SecretNames"/>.</summary>
public sealed record PlatformInfo(string Id, string Name, IReadOnlyList<string> Domains, IReadOnlySet<CrawlKind> Supports,
    IReadOnlyList<string> SecretNames);

public static class PlatformSecrets
{
    public const string Cookie = "cookie";
    public const string Token = "token";

    public static string Scope(string platform) => $"platform:{platform}";

    public static SecretKey Key(string platform, string name) => new(Scope(platform), name);
}

/// <summary>Danh sách nền tảng hỗ trợ (tải + crawl) cùng tên miền và loại bí mật; nguồn duy nhất cho detector/session/crawler.</summary>
public static class PlatformRegistry
{
    private static readonly HashSet<CrawlKind> None = [];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xhs"] = "xiaohongshu", ["x"] = "twitter", ["fb"] = "facebook", ["ig"] = "instagram"
    };

    public static IReadOnlyList<PlatformInfo> All { get; } =
    [
        new("tiktok", "TikTok", ["tiktok.com"],
            Kinds(CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Explore, CrawlKind.Hashtag, CrawlKind.Comments, CrawlKind.Trending),
            ["cookie", "token", "provider_api_key"]),
        new("youtube", "YouTube", ["youtube.com", "youtu.be"],
            Kinds(CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Search, CrawlKind.Comments),
            ["cookie", "token", "api_key"]),
        new("facebook", "Facebook", ["facebook.com", "fb.watch", "fb.gg"],
            Kinds(CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Comments), ["cookie", "token"]),
        new("instagram", "Instagram", ["instagram.com", "instagr.am"],
            Kinds(CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Hashtag, CrawlKind.Comments), ["cookie", "token"]),
        new("douyin", "Douyin", ["douyin.com", "iesdouyin.com"],
            Kinds(CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Search, CrawlKind.Explore, CrawlKind.Hashtag, CrawlKind.Trending),
            ["cookie", "token"]),
        new("xiaohongshu", "Xiaohongshu", ["xiaohongshu.com", "xhslink.com"],
            Kinds(CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Explore, CrawlKind.Hashtag, CrawlKind.Trending), ["cookie", "token"]),
        new("weibo", "Weibo", ["weibo.com", "weibo.cn"],
            Kinds(CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Hashtag, CrawlKind.Trending), ["cookie", "token"]),
        new("bilibili", "Bilibili", ["bilibili.com", "b23.tv"],
            Kinds(CrawlKind.Channel, CrawlKind.Profile, CrawlKind.Search, CrawlKind.Explore, CrawlKind.Trending),
            ["cookie", "token", "sessdata"]),
        new("kuaishou", "Kuaishou", ["kuaishou.com", "kwai.com", "gifshow.com"],
            Kinds(CrawlKind.Explore, CrawlKind.Trending), ["cookie", "token"]),
        new("twitter", "Twitter / X", ["twitter.com", "x.com"], None, ["cookie", "token", "bearer_token"]),
        new("threads", "Threads", ["threads.net", "threads.com"], None, ["cookie"]),
        new("meta", "Meta (Graph API)", [], None, ["token", "ads_token"])
    ];

    /// <summary>Chuẩn hoá tên (hoa/thường, alias như xhs) về id; null nếu không hỗ trợ.</summary>
    public static string? NormalizeId(string? platform)
    {
        var id = platform?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        if (Aliases.TryGetValue(id, out var canonical)) id = canonical;
        return All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    public static PlatformInfo? Find(string? platform) =>
        NormalizeId(platform) is { } id ? All.First(p => p.Id == id) : null;

    public static PlatformInfo Get(string? platform) =>
        Find(platform) ?? throw new ArgumentException($"Nền tảng \"{platform}\" không được hỗ trợ.", nameof(platform));

    private static HashSet<CrawlKind> Kinds(params CrawlKind[] kinds) => [.. kinds];
}
