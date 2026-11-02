using System.Text.Json;

namespace SuperMOOO.Social.Listening;

public sealed class TikTokSettings
{
    public string ProviderUrl { get; set; } = "https://api.tikapi.io/public/check";
    public string EngineMode { get; set; } = "auto";      // auto | provider | local
    public string? ExploreQuery { get; set; }
}

public sealed record MetaRateLimit(double CallCountPct, double TotalCpuTimePct, double TotalTimePct)
{
    public bool IsNearLimit => Math.Max(CallCountPct, Math.Max(TotalCpuTimePct, TotalTimePct)) >= 80;
}

public static class QuotaCost
{
    public const int YoutubeSearch = 100;
    public const int YoutubeList = 1;
    public const int MetaCall = 1;
    public const int TikTokProviderCall = 1;
}

/// <summary>Ghi nhận lượt dùng API; giới hạn thực tế do nhà cung cấp quyết định.</summary>
[Service]
public sealed class QuotaService(IQuotaUsageStore store)
{
    public static string Normalize(string platform) => platform.Equals("facebook", StringComparison.OrdinalIgnoreCase) ? "meta" : platform.ToLowerInvariant();

    public Task RecordAsync(string platform, string endpoint, int cost, string caller = "system", CancellationToken ct = default) =>
        store.AddAsync(Normalize(platform), endpoint, cost, caller, ct);

    /// <summary>Header X-App-Usage của Meta: {"call_count":28,"total_cputime":25,"total_time":20}.</summary>
    public static MetaRateLimit ParseMetaRateLimit(string? appUsageHeader)
    {
        if (string.IsNullOrWhiteSpace(appUsageHeader)) return new MetaRateLimit(0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(appUsageHeader);
            double Read(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
            return new MetaRateLimit(Read("call_count"), Read("total_cputime"), Read("total_time"));
        }
        catch (JsonException)
        {
            return new MetaRateLimit(0, 0, 0);
        }
    }
}
