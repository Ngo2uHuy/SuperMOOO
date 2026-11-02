namespace SuperMOOO.Social.Listening;

[Service]
public sealed class AuditLogService(IAuditLog audit)
{
    public Task AddAsync(string source, string action, string? target, string status, int? records = null, CancellationToken ct = default) =>
        audit.AddAsync(source, action, target, status, records, ct);

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(int limit = 50, CancellationToken ct = default) => audit.ListRecentAsync(limit, ct);
}

/// <summary>Chuỗi thời gian chỉ số (views, likes, subscribers…) của video/kênh/trang.</summary>
[Service]
public sealed class MetricService(IMetricStore store, IContentStore content)
{
    public async Task RecordAsync(string platform, string entityKey, string? title, string? url, string metric, double value,
        CancellationToken ct = default)
    {
        var contentId = await content.FindIdAsync(platform, entityKey, ct);
        await store.RecordAsync(new MetricSample(platform, entityKey, title, url, metric, value, DateTimeOffset.UtcNow, contentId), ct);
    }

    /// <summary>Ghi các chỉ số &gt; 0 của một mục nội dung (lượt xem/thích/bình luận/người theo dõi).</summary>
    public async Task RecordItemAsync(ContentItem item, CancellationToken ct = default)
    {
        foreach (var (metric, value) in new[]
                 {
                     (item.Kind is "channel" or "page" or "profile" ? "followers" : "views", item.ViewCount),
                     ("likes", item.LikeCount), ("comments", item.CommentCount)
                 })
            if (value is > 0)
                await RecordAsync(item.Platform, item.ExternalId, item.Title, item.Url, metric, value.Value, ct);
    }

    public Task<IReadOnlyList<MetricSample>> GetHistoryAsync(string entityKey, string? metric = null, int limit = 50, CancellationToken ct = default) =>
        store.HistoryAsync(entityKey, metric, limit, ct);

    public Task<IReadOnlyList<MetricEntity>> ListEntitiesAsync(int limit = 200, CancellationToken ct = default) => store.ListEntitiesAsync(limit, ct);
}

/// <summary>Thực thể được theo dõi: kênh/trang/hồ sơ lưu trong content_item.</summary>
[Service]
public sealed class EntityService(IContentStore content)
{
    private static readonly string[] EntityKinds = ["channel", "page", "profile"];

    public Task<IReadOnlyList<ContentItem>> ListAsync(string? platform = null, string? entityType = null, int limit = 50, CancellationToken ct = default) =>
        content.ListByKindsAsync(platform, entityType is { Length: > 0 } ? [entityType] : EntityKinds, limit, ct);

    public async Task<ContentItem?> GetAsync(string platform, string externalId, CancellationToken ct = default)
    {
        foreach (var kind in EntityKinds)
            if (await content.FindAsync(platform, kind, externalId, ct) is { } item) return item;
        return null;
    }

    public Task<bool> DeleteAsync(long id, CancellationToken ct = default) => content.DeleteAsync(id, ct);
}
