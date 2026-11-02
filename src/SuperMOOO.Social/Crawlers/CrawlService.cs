using System.Text.Json;
using SuperMOOO.Social.Listening;
using SuperMOOO.Social.Platforms;

namespace SuperMOOO.Social.Crawlers;

public sealed record CrawlOptions(string? Platform = null, string RunType = "scan_channel", bool SaveToLibrary = false, string? JobId = null);

/// <summary>Điểm vào duy nhất của mọi crawler: chọn crawler, ghi crawl_run + content_item + nhật ký, tuỳ chọn ghi chỉ số.</summary>
[Service]
public sealed class CrawlService(IEnumerable<IPlatformCrawler> crawlers, IContentStore content, ICrawlRunStore runs, IAuditLog audit, MetricService metrics)
{
    private readonly IReadOnlyList<IPlatformCrawler> _crawlers = crawlers.ToList();

    public IReadOnlyList<IPlatformCrawler> Crawlers => _crawlers;

    public IPlatformCrawler Resolve(string target, string? platform = null)
    {
        var id = PlatformRegistry.NormalizeId(platform);
        if (id is null && !string.IsNullOrWhiteSpace(platform)) throw new ArgumentException($"Nền tảng \"{platform}\" không được hỗ trợ.");
        if (id is null && Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri)) id = PlatformDetector.Detect(uri.ToString());
        if (id is null && target.TrimStart().StartsWith('@')) id = "tiktok";
        if (id is null) throw new ArgumentException("Không nhận ra nền tảng từ liên kết; hãy chọn nền tảng.");

        return _crawlers.FirstOrDefault(c => c.Platform == id) ?? throw new NotSupportedException($"Chưa có bộ quét cho {id}.");
    }

    public async Task<CrawlResult> CrawlAsync(CrawlRequest request, CrawlOptions? options = null, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ContentFilters.Validate(request.Filters);
        options ??= new CrawlOptions();
        var crawler = Resolve(request.Target, options.Platform);
        var runId = await runs.StartAsync(crawler.Platform, options.RunType, request.Target,
            JsonSerializer.Serialize(request.Filters ?? new Dictionary<string, string>()), options.JobId, ct);

        CrawlResult result;
        try
        {
            result = await crawler.CrawlAsync(request, progress, ct);
            result = result with { Items = ContentFilters.Apply(result.Items, request.Filters) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await runs.CompleteAsync(runId, "failed", 0, ex.Message, CancellationToken.None);
            await audit.AddAsync(crawler.Platform, request.Kind.ToString(), request.Target, "failed", 0, CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            await runs.CompleteAsync(runId, "cancelled", 0, null, CancellationToken.None);
            throw;
        }

        var saved = await PersistAsync(runId, result, options, request.SaveEntities, ct);
        await runs.CompleteAsync(runId, "finished", saved.Items.Count, result.Warning, ct);
        await audit.AddAsync(crawler.Platform, request.Kind.ToString(), request.Target, result.Warning is null ? "success" : "success_fallback", saved.Items.Count, ct);
        return saved;
    }

    private async Task<CrawlResult> PersistAsync(string runId, CrawlResult result, CrawlOptions options, bool saveEntities, CancellationToken ct)
    {
        var owner = result.Owner;
        if (owner is not null) owner = owner with { Id = await content.UpsertAsync(owner, ct) };

        var items = new List<ContentItem>(result.Items.Count);
        for (var rank = 0; rank < result.Items.Count; rank++)
        {
            var id = await content.UpsertAsync(result.Items[rank], ct);
            await runs.AddItemAsync(runId, id, rank + 1, ct);
            if (options.SaveToLibrary) await content.SetInLibraryAsync(id, true, ct);
            items.Add(result.Items[rank] with { Id = id });
        }

        if (saveEntities)
        {
            if (owner is not null) await metrics.RecordItemAsync(owner, ct);
            foreach (var item in items) await metrics.RecordItemAsync(item, ct);
        }

        return result with { Owner = owner, Items = items };
    }
}
