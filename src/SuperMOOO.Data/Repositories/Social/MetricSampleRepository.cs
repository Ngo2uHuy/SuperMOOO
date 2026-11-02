namespace SuperMOOO.Data.Repositories.Social;

[Service(As = [typeof(IMetricStore)])]
public sealed class MetricSampleRepository(IDbConnectionFactory factory) : IMetricStore
{
    private sealed class Row
    {
        public long Id { get; set; }
        public long? ContentItemId { get; set; }
        public string Platform { get; set; } = "";
        public string EntityKey { get; set; } = "";
        public string? EntityTitle { get; set; }
        public string? Url { get; set; }
        public string Metric { get; set; } = "";
        public double Value { get; set; }
        public long RecordedAt { get; set; }
    }

    private sealed class EntityRow
    {
        public string Platform { get; set; } = "";
        public string EntityKey { get; set; } = "";
        public string? EntityTitle { get; set; }
    }

    public async Task RecordAsync(MetricSample sample, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO metric_sample(content_item_id, platform, entity_key, entity_title, url, metric, value, recorded_at)
            VALUES (@ContentItemId, @Platform, @EntityKey, @EntityTitle, @Url, @Metric, @Value, @at)
            """, new
            {
                sample.ContentItemId, sample.Platform, sample.EntityKey, sample.EntityTitle, sample.Url, sample.Metric, sample.Value,
                at = sample.RecordedAt.ToUnixTimeMilliseconds()
            }, cancellationToken: ct));
    }

    /// <summary>Mới nhất trước; biểu đồ tự đảo lại theo thời gian.</summary>
    public async Task<IReadOnlyList<MetricSample>> HistoryAsync(string entityKey, string? metric = null, int limit = 50,
        CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<Row>(new CommandDefinition("""
            SELECT id, content_item_id, platform, entity_key, entity_title, url, metric, value, recorded_at
            FROM metric_sample WHERE entity_key = @entityKey AND (@metric IS NULL OR metric = @metric)
            ORDER BY recorded_at DESC, id DESC LIMIT @limit
            """, new { entityKey, metric, limit }, cancellationToken: ct));
        return rows.Select(r => new MetricSample(r.Platform, r.EntityKey, r.EntityTitle, r.Url, r.Metric, r.Value,
            DateTimeOffset.FromUnixTimeMilliseconds(r.RecordedAt), r.ContentItemId, r.Id)).ToList();
    }

    public async Task<IReadOnlyList<MetricEntity>> ListEntitiesAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<EntityRow>(new CommandDefinition("""
            SELECT platform, entity_key, MAX(entity_title) AS entity_title
            FROM metric_sample GROUP BY platform, entity_key ORDER BY MAX(recorded_at) DESC LIMIT @limit
            """, new { limit }, cancellationToken: ct));
        return rows.Select(r => new MetricEntity(r.Platform, r.EntityKey, r.EntityTitle)).ToList();
    }
}
