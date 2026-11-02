using SuperMOOO.Core.Publishing;
using static SuperMOOO.Data.Repositories.Publishing.PubSql;

namespace SuperMOOO.Data.Repositories.Publishing;

[Service(As = [typeof(IAnalyticsRepository)])]
public sealed class AnalyticsRepository(IDbConnectionFactory factory) : IAnalyticsRepository
{
    private const int IdChunk = 500;

    private sealed class CandidateRow
    {
        public string PublicationId { get; set; } = "";
        public string DestinationId { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public SocialPlatform Platform { get; set; }
        public string RemoteId { get; set; } = "";
        public string? RemoteUrl { get; set; }
        public long PublishedAt { get; set; }
        public long? LastSyncedAt { get; set; }
    }

    private sealed class DestinationRow
    {
        public string DestinationId { get; set; } = "";
        public string Title { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public SocialPlatform Platform { get; set; }
        public bool Removed { get; set; }
    }

    private sealed class PostRow
    {
        public string PublicationId { get; set; } = "";
        public string DestinationId { get; set; } = "";
        public string DestinationTitle { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public SocialPlatform Platform { get; set; }
        public string Title { get; set; } = "";
        public long PublishedAt { get; set; }
        public string? CampaignId { get; set; }
        public string? CampaignName { get; set; }
        public string? SourceId { get; set; }
        public string? SourceName { get; set; }
    }

    private sealed class PointRow
    {
        public string DestinationId { get; set; } = "";
        public string? PublicationId { get; set; }
        public SocialPlatform Platform { get; set; }
        public string MetricKey { get; set; } = "";
        public double? Value { get; set; }
        public long CapturedAt { get; set; }
        public string Source { get; set; } = "";
        public MetricStatus Status { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class ChannelSyncRow
    {
        public string DestinationId { get; set; } = "";
        public long LastAt { get; set; }
    }

    private sealed class OutcomeRow
    {
        public string Day { get; set; } = "";
        public int Failed { get; set; }
        public int Published { get; set; }
    }

    private sealed class ViewRow
    {
        public string PublicationId { get; set; } = "";
        public long PublishedAt { get; set; }
        public double? Views { get; set; }
    }

    private sealed class HealthRow
    {
        public string Id { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public string? DestinationId { get; set; }
        public string Kind { get; set; } = "";
        public string Detail { get; set; } = "";
        public double? Observed { get; set; }
        public double? Baseline { get; set; }
        public double? ZScore { get; set; }
        public int CampaignsPaused { get; set; }
        public long CreatedAt { get; set; }
        public string? DestinationTitle { get; set; }
    }

    public async Task<IReadOnlyList<MetricDefinition>> ListDefinitionsAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<MetricDefinition>(new CommandDefinition("""
            SELECT platform, key, scope, display_name, unit, description, source_endpoint, required_scope, aggregation, verified
            FROM pub_metric_definitions ORDER BY rowid
            """, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<SyncCandidate>> ListSyncCandidatesAsync(DateTimeOffset maxPublishedUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<CandidateRow>(new CommandDefinition("""
            SELECT p.id AS publication_id, p.destination_id, d.connection_id, d.platform, p.remote_id, p.remote_url,
              COALESCE(p.published_at, p.updated_at) AS published_at,
              (SELECT MAX(t) FROM (
                 SELECT MAX(s.captured_at) AS t FROM pub_metric_snapshots s WHERE s.publication_id = p.id
                 UNION ALL
                 SELECT MAX(a.last_captured_at) FROM pub_daily_aggregates a WHERE a.publication_id = p.id)) AS last_synced_at
            FROM pub_publications p
            JOIN pub_destinations d ON d.id = p.destination_id
            WHERE p.status = 'Published' AND p.remote_id IS NOT NULL AND d.removed_at IS NULL
              AND COALESCE(p.published_at, p.updated_at) <= @max
            """, new { max = ToMs(maxPublishedUtc) }, cancellationToken: ct));
        return [.. rows.Select(r => new SyncCandidate(r.PublicationId, r.DestinationId, r.ConnectionId, r.Platform, r.RemoteId, r.RemoteUrl,
            FromMs(r.PublishedAt), FromMs(r.LastSyncedAt)))];
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastChannelSyncAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<ChannelSyncRow>(new CommandDefinition("""
            SELECT destination_id, MAX(t) AS last_at FROM (
              SELECT destination_id, MAX(captured_at) AS t FROM pub_metric_snapshots WHERE publication_id IS NULL GROUP BY destination_id
              UNION ALL
              SELECT destination_id, MAX(last_captured_at) FROM pub_daily_aggregates WHERE publication_id = '' GROUP BY destination_id)
            GROUP BY destination_id
            """, cancellationToken: ct));
        return rows.ToDictionary(r => r.DestinationId, r => FromMs(r.LastAt));
    }

    public async Task<IReadOnlyList<DestinationInfoRow>> ListDestinationsAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<DestinationRow>(new CommandDefinition("""
            SELECT id AS destination_id, title, connection_id, platform, (removed_at IS NOT NULL) AS removed
            FROM pub_destinations ORDER BY title COLLATE NOCASE, id
            """, cancellationToken: ct));
        return [.. rows.Select(r => new DestinationInfoRow(r.DestinationId, r.Title, r.ConnectionId, r.Platform, r.Removed))];
    }

    public async Task<IReadOnlySet<string>> ListDestinationsWithPendingSyncAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        var ids = await db.QueryAsync<string?>(new CommandDefinition("""
            SELECT DISTINCT json_extract(payload_json, '$.destinationId') FROM job
            WHERE type = @type AND status IN ('Queued', 'Running')
            """, new { type = PublishJobTypes.AnalyticsSync }, cancellationToken: ct));
        return ids.OfType<string>().ToHashSet();
    }

    public async Task AddSnapshotsAsync(IReadOnlyList<MetricSnapshot> snapshots, CancellationToken ct)
    {
        if (snapshots.Count == 0) return;
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var s in snapshots)
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO pub_metric_snapshots(id, platform, destination_id, publication_id, remote_post_id, metric_key, value, captured_at, source,
                  status, detail)
                VALUES (@id, @platform, @destinationId, @publicationId, @remotePostId, @metricKey, @value, @capturedAt, @source, @status, @detail)
                """,
                new
                {
                    id = s.Id, platform = s.Platform.ToString(), destinationId = s.DestinationId, publicationId = s.PublicationId,
                    remotePostId = s.RemotePostId, metricKey = s.MetricKey, value = s.Value, capturedAt = ToMs(s.CapturedUtc), source = s.Source,
                    status = s.Status.ToString(), detail = s.Detail
                }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<CompactionResult> CompactAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var cutoff = ToMs(cutoffUtc);
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        // Mỗi (ngày UTC, kênh/bài, metric): giá trị cuối + nhỏ nhất/lớn nhất + số mẫu; gộp tiếp vào dòng đã có nên chạy lại hay chạy nhiều đợt đều đúng.
        var aggregated = await db.ExecuteAsync(new CommandDefinition("""
            WITH src AS (
              SELECT date(captured_at / 1000, 'unixepoch') AS day, destination_id, COALESCE(publication_id, '') AS pub, metric_key, platform, value,
                captured_at, source,
                ROW_NUMBER() OVER (PARTITION BY date(captured_at / 1000, 'unixepoch'), destination_id, COALESCE(publication_id, ''), metric_key
                                   ORDER BY captured_at DESC) AS rn
              FROM pub_metric_snapshots WHERE captured_at < @cutoff AND status = 'Ok')
            INSERT INTO pub_daily_aggregates(day, destination_id, publication_id, metric_key, platform, value_last, value_min, value_max, samples,
              last_captured_at, source)
            SELECT day, destination_id, pub, metric_key, platform,
              MAX(CASE WHEN rn = 1 THEN value END), MIN(value), MAX(value), COUNT(*),
              MAX(CASE WHEN rn = 1 THEN captured_at END), MAX(CASE WHEN rn = 1 THEN source END)
            FROM src WHERE 1 GROUP BY day, destination_id, pub, metric_key, platform
            ON CONFLICT(day, destination_id, publication_id, metric_key) DO UPDATE SET
              value_last = CASE WHEN excluded.last_captured_at >= pub_daily_aggregates.last_captured_at THEN excluded.value_last
                           ELSE pub_daily_aggregates.value_last END,
              source = CASE WHEN excluded.last_captured_at >= pub_daily_aggregates.last_captured_at THEN excluded.source
                       ELSE pub_daily_aggregates.source END,
              value_min = MIN(pub_daily_aggregates.value_min, excluded.value_min),
              value_max = MAX(pub_daily_aggregates.value_max, excluded.value_max),
              samples = pub_daily_aggregates.samples + excluded.samples,
              last_captured_at = MAX(pub_daily_aggregates.last_captured_at, excluded.last_captured_at)
            """, new { cutoff }, tx, cancellationToken: ct));
        var deleted = await db.ExecuteAsync(new CommandDefinition("DELETE FROM pub_metric_snapshots WHERE captured_at < @cutoff",
            new { cutoff }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new CompactionResult(aggregated, deleted);
    }

    public async Task<IReadOnlyList<PostInfo>> ListPostsAsync(PostFilter f, CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<PostRow>(new CommandDefinition("""
            SELECT p.id AS publication_id, p.destination_id, d.title AS destination_title, d.connection_id, d.platform, p.title,
              COALESCE(p.published_at, p.updated_at) AS published_at, p.campaign_id, cp.name AS campaign_name,
              ci.source_id, cs.name AS source_name
            FROM pub_publications p
            JOIN pub_destinations d ON d.id = p.destination_id
            JOIN pub_content_items ci ON ci.id = p.content_item_id
            LEFT JOIN pub_content_sources cs ON cs.id = ci.source_id
            LEFT JOIN pub_campaigns cp ON cp.id = p.campaign_id
            WHERE p.status = 'Published' AND p.remote_id IS NOT NULL AND COALESCE(p.published_at, p.updated_at) <= @before
              AND (@platform IS NULL OR d.platform = @platform) AND (@destinationId IS NULL OR p.destination_id = @destinationId)
              AND (@campaignId IS NULL OR p.campaign_id = @campaignId) AND (@sourceId IS NULL OR ci.source_id = @sourceId)
              AND (@publicationId IS NULL OR p.id = @publicationId)
            ORDER BY COALESCE(p.published_at, p.updated_at), p.id
            """, new
            {
                before = ToMs(f.PublishedBeforeUtc), platform = f.Platform?.ToString(), destinationId = f.DestinationId, campaignId = f.CampaignId,
                sourceId = f.SourceId, publicationId = f.PublicationId
            }, cancellationToken: ct));
        return [.. rows.Select(r => new PostInfo(r.PublicationId, r.DestinationId, r.DestinationTitle, r.ConnectionId, r.Platform, r.Title,
            FromMs(r.PublishedAt), r.CampaignId, r.CampaignName, r.SourceId, r.SourceName))];
    }

    public async Task<IReadOnlyList<MetricPoint>> ListPointsAsync(IReadOnlyCollection<string>? destinationIds,
        IReadOnlyCollection<string>? publicationIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        var result = new List<MetricPoint>();
        foreach (var chunk in (destinationIds ?? []).Chunk(IdChunk))
            result.AddRange(await QueryAsync(db, """
                SELECT destination_id, NULL AS publication_id, platform, metric_key, value, captured_at, source, status, detail
                FROM pub_metric_snapshots WHERE publication_id IS NULL AND destination_id IN @ids AND captured_at >= @from AND captured_at <= @to
                UNION ALL
                SELECT destination_id, NULL, platform, metric_key, value_last, last_captured_at, source, 'Ok', NULL
                FROM pub_daily_aggregates
                WHERE publication_id = '' AND destination_id IN @ids AND last_captured_at >= @from AND last_captured_at <= @to
                """, chunk, fromUtc, toUtc, ct));
        foreach (var chunk in (publicationIds ?? []).Chunk(IdChunk))
            result.AddRange(await QueryAsync(db, """
                SELECT destination_id, publication_id, platform, metric_key, value, captured_at, source, status, detail
                FROM pub_metric_snapshots WHERE publication_id IN @ids AND captured_at >= @from AND captured_at <= @to
                UNION ALL
                SELECT destination_id, publication_id, platform, metric_key, value_last, last_captured_at, source, 'Ok', NULL
                FROM pub_daily_aggregates WHERE publication_id IN @ids AND last_captured_at >= @from AND last_captured_at <= @to
                """, chunk, fromUtc, toUtc, ct));
        return result;
    }

    private static async Task<IEnumerable<MetricPoint>> QueryAsync(DbConnection db, string sql, string[] ids, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, CancellationToken ct)
    {
        var rows = await db.QueryAsync<PointRow>(new CommandDefinition(sql, new { ids, from = ToMs(fromUtc), to = ToMs(toUtc) }, cancellationToken: ct));
        return rows.Select(r => new MetricPoint(r.DestinationId, r.PublicationId, r.Platform, r.MetricKey, r.Value, FromMs(r.CapturedAt), r.Source,
            r.Status, r.Detail));
    }

    public async Task<IReadOnlyList<DailyPublishOutcome>> ListPublishOutcomesAsync(string destinationId, DateTimeOffset fromUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<OutcomeRow>(new CommandDefinition("""
            SELECT date((CASE WHEN status = 'Published' THEN COALESCE(published_at, updated_at) ELSE updated_at END) / 1000, 'unixepoch') AS day,
              SUM(CASE WHEN status IN ('Failed', 'NeedsAction', 'Unknown') THEN 1 ELSE 0 END) AS failed,
              SUM(CASE WHEN status = 'Published' THEN 1 ELSE 0 END) AS published
            FROM pub_publications
            WHERE destination_id = @destinationId AND updated_at >= @from AND status IN ('Failed', 'NeedsAction', 'Unknown', 'Published')
            GROUP BY day ORDER BY day
            """, new { destinationId, from = ToMs(fromUtc) }, cancellationToken: ct));
        return [.. rows.Select(r => new DailyPublishOutcome(r.Day, r.Failed, r.Published))];
    }

    public async Task<IReadOnlyList<PostViewSample>> ListPostViewSamplesAsync(string destinationId, string metricKey, DateTimeOffset publishedBeforeUtc,
        CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<ViewRow>(new CommandDefinition("""
            SELECT p.id AS publication_id, COALESCE(p.published_at, p.updated_at) AS published_at,
              (SELECT value FROM (
                 SELECT s.value AS value, s.captured_at AS captured_at FROM pub_metric_snapshots s
                   WHERE s.publication_id = p.id AND s.metric_key = @metricKey AND s.status = 'Ok'
                 UNION ALL
                 SELECT a.value_last, a.last_captured_at FROM pub_daily_aggregates a WHERE a.publication_id = p.id AND a.metric_key = @metricKey)
               ORDER BY captured_at DESC LIMIT 1) AS views
            FROM pub_publications p
            WHERE p.destination_id = @destinationId AND p.status = 'Published' AND p.remote_id IS NOT NULL
              AND COALESCE(p.published_at, p.updated_at) <= @before
            ORDER BY COALESCE(p.published_at, p.updated_at), p.id
            """, new { destinationId, metricKey, before = ToMs(publishedBeforeUtc) }, cancellationToken: ct));
        return [.. rows.Where(r => r.Views is not null).Select(r => new PostViewSample(r.PublicationId, FromMs(r.PublishedAt), r.Views!.Value))];
    }

    public async Task<bool> TryAddHealthEventAsync(AccountHealthEvent ev, string day, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO pub_account_health_events(id, connection_id, destination_id, kind, day, detail, observed, baseline, z_score,
              campaigns_paused, created_at)
            VALUES (@Id, @ConnectionId, @DestinationId, @Kind, @day, @Detail, @Observed, @Baseline, @ZScore, @CampaignsPaused, @created)
            """, new
            {
                ev.Id, ev.ConnectionId, ev.DestinationId, ev.Kind, day, ev.Detail, ev.Observed, ev.Baseline, ev.ZScore, ev.CampaignsPaused,
                created = ToMs(ev.CreatedUtc)
            }, cancellationToken: ct)) > 0;
    }

    public async Task SetHealthEventCampaignsPausedAsync(string id, int paused, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("UPDATE pub_account_health_events SET campaigns_paused = @paused WHERE id = @id",
            new { id, paused }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<(AccountHealthEvent Event, string? DestinationTitle)>> ListHealthEventsAsync(int take, CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<HealthRow>(new CommandDefinition("""
            SELECT e.id, e.connection_id, e.destination_id, e.kind, e.detail, e.observed, e.baseline, e.z_score, e.campaigns_paused, e.created_at,
              d.title AS destination_title
            FROM pub_account_health_events e LEFT JOIN pub_destinations d ON d.id = e.destination_id
            ORDER BY e.created_at DESC, e.id LIMIT @take
            """, new { take }, cancellationToken: ct));
        return [.. rows.Select(r => (new AccountHealthEvent
        {
            Id = r.Id, ConnectionId = r.ConnectionId, DestinationId = r.DestinationId, Kind = r.Kind, Detail = r.Detail, Observed = r.Observed,
            Baseline = r.Baseline, ZScore = r.ZScore, CampaignsPaused = r.CampaignsPaused, CreatedUtc = FromMs(r.CreatedAt)
        }, r.DestinationTitle))];
    }
}
