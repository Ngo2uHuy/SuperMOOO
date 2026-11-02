using SuperMOOO.Social.Crawlers;
using SuperMOOO.Social.Platforms;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace SuperMOOO.Social.Listening;

public sealed record WatchCheckResult(long TargetId, int ItemsFound, string? Warning);

/// <summary>Danh sách theo dõi kênh/hashtag/từ khoá; mỗi lần kiểm tra quét lại và ghi chuỗi chỉ số.</summary>
[Service]
public sealed class WatchlistService(IWatchTargetStore store, CrawlService crawl, MetricService metrics, IServiceProvider services)
{
    // IJobQueue phụ thuộc mọi IJobHandler (trong đó có WatchCheckJobHandler → service này) nên phải lấy lười để tránh vòng DI.
    private IJobQueue Queue => services.GetRequiredService<IJobQueue>();

    private const int TopItemsToRecord = 3;

    public Task<IReadOnlyList<WatchTarget>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    public Task<long> AddAsync(string name, string url, string platform, string targetType = "channel", int frequencyHours = 24, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Cần nhập liên kết hoặc từ khoá cần theo dõi.");
        var id = PlatformRegistry.NormalizeId(platform) ?? throw new ArgumentException($"Nền tảng \"{platform}\" không được hỗ trợ.");
        if (frequencyHours < 1) throw new ArgumentException("Tần suất kiểm tra tối thiểu 1 giờ.");
        return store.AddAsync(new WatchTarget(string.IsNullOrWhiteSpace(name) ? url.Trim() : name.Trim(), url.Trim(), id, targetType.Trim().ToLowerInvariant(),
            frequencyHours, "active", null, null, DateTimeOffset.UtcNow), ct);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken ct = default) => store.DeleteAsync(id, ct);

    /// <summary>Xếp một job kiểm tra ngay; trả id job.</summary>
    public async Task<string> CheckNowAsync(long id, CancellationToken ct = default)
    {
        var target = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException("Không tìm thấy mục theo dõi.");
        return await Queue.EnqueueAsync(new JobRequest(JobTypes.WatchCheck, $"Theo dõi: {target.Name}", new JsonObject { ["id"] = id }, JobLane.Network,
            Owner: $"watch:{id}"), ct);
    }

    public async Task<IReadOnlyList<WatchTarget>> ListDueAsync(DateTimeOffset now, CancellationToken ct = default) =>
        (await store.ListAsync(ct)).Where(t => t.Status == "active" && (t.LastCheckedAt is null || now - t.LastCheckedAt >= TimeSpan.FromHours(t.FrequencyHours))).ToList();

    /// <summary>Xếp job cho mọi mục đến hạn (bỏ qua mục đang có job chờ/chạy); trả số job đã xếp.</summary>
    public async Task<int> EnqueueDueAsync(CancellationToken ct = default)
    {
        var count = 0;
        foreach (var target in await ListDueAsync(DateTimeOffset.UtcNow, ct))
        {
            var pending = await Queue.ListAsync(new SuperMOOO.Core.Jobs.JobQuery(JobTypes.WatchCheck, [JobStatus.Queued, JobStatus.Running], $"watch:{target.Id}", 1), ct);
            if (pending.Count > 0) continue;
            await CheckNowAsync(target.Id, ct);
            count++;
        }

        return count;
    }

    public async Task<WatchCheckResult> RunCheckAsync(long id, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var target = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException("Không tìm thấy mục theo dõi.");
        var kind = target.TargetType switch { "hashtag" => CrawlKind.Hashtag, "search" or "keyword" => CrawlKind.Search, "profile" => CrawlKind.Profile, _ => CrawlKind.Channel };
        var result = await crawl.CrawlAsync(new CrawlRequest(kind, target.Url, 10), new CrawlOptions(target.Platform, "watch"), progress, ct);

        if (result.Owner is not null) await metrics.RecordItemAsync(result.Owner, ct);
        foreach (var item in result.Items.Take(TopItemsToRecord)) await metrics.RecordItemAsync(item, ct);
        await store.MarkCheckedAsync(id, DateTimeOffset.UtcNow, result.Owner is { Id: > 0 } o ? o.Id : null, ct);
        return new WatchCheckResult(id, result.Items.Count, result.Warning);
    }
}

[Service(As = [typeof(IJobHandler)])]
public sealed class WatchCheckJobHandler(WatchlistService watchlist) : IJobHandler
{
    public string Type => JobTypes.WatchCheck;

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        var id = context.Payload["id"]?.GetValue<long>() ?? throw new ArgumentException("Payload thiếu id.");
        var result = await watchlist.RunCheckAsync(id, context.Progress, ct);
        return new JsonObject { ["itemsFound"] = result.ItemsFound, ["warning"] = result.Warning };
    }
}

/// <summary>Mỗi 30 phút xếp job kiểm tra cho các mục theo dõi đã đến hạn.</summary>
[ScheduledJob("0 0/30 * * * ?")]
public sealed class WatchlistCheckJob(WatchlistService watchlist) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => new(watchlist.EnqueueDueAsync(cancellationToken));
}

/// <summary>Chạy một lần quét bất kỳ dưới dạng job nền (payload: target, kind, platform, maxItems, runType, saveToLibrary, saveEntities, filters).</summary>
[Service(As = [typeof(IJobHandler)])]
public sealed class CrawlJobHandler(CrawlService crawl) : IJobHandler
{
    public string Type => JobTypes.Crawl;

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        var p = context.Payload;
        var filters = (p["filters"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
        var request = new CrawlRequest(Enum.Parse<CrawlKind>(p["kind"]?.GetValue<string>() ?? nameof(CrawlKind.Channel), true),
            p["target"]?.GetValue<string>() ?? throw new ArgumentException("Payload thiếu target."), p["maxItems"]?.GetValue<int>() ?? 50,
            Filters: filters, SaveEntities: p["saveEntities"]?.GetValue<bool>() ?? false);
        var options = new CrawlOptions(p["platform"]?.GetValue<string>(), p["runType"]?.GetValue<string>() ?? "scan_channel",
            p["saveToLibrary"]?.GetValue<bool>() ?? false, context.JobId);
        var result = await crawl.CrawlAsync(request, options, context.Progress, ct);
        return new JsonObject { ["platform"] = result.Platform, ["items"] = result.Items.Count, ["warning"] = result.Warning };
    }
}
