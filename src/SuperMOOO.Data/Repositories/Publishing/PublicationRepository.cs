using SuperMOOO.Core.Publishing;
using SuperMOOO.Data.Sqlite;
using static SuperMOOO.Data.Repositories.Publishing.PubSql;

namespace SuperMOOO.Data.Repositories.Publishing;

[Service(As = [typeof(IPublicationRepository)])]
public sealed class PublicationRepository(IDbConnectionFactory factory) : IPublicationRepository
{
    internal const string Columns = """
        p.id, p.content_item_id, p.destination_id, p.title, p.description, p.tags_json, p.privacy, p.made_for_kids,
        p.contains_synthetic_media, p.scheduled_at, p.use_native_schedule, p.status, p.idempotency_key, p.job_id, p.upload_session_uri,
        p.upload_bytes_total, p.upload_bytes_confirmed, p.upload_started_at, p.final_chunk_sent_at, p.upload_completed_at,
        p.platform_settings_json, p.remote_id, p.remote_url, p.last_error, p.blocked_reason, p.intentional_repost, p.processing_polls,
        p.published_at, p.campaign_id, p.plan_key, p.late_resolved, p.origin, p.origin_ref, p.created_at, p.updated_at
        """;

    private const string RowSelect = $"""
        SELECT {Columns}, ci.title AS content_title, d.title AS destination_title, d.connection_id AS connection_id,
          c.display_name AS connection_name, d.platform AS platform,
          (SELECT COUNT(*) FROM pub_publish_attempts a WHERE a.publication_id = p.id
             AND (a.outcome IS NULL OR a.outcome NOT IN ('Deferred', 'Skipped'))) AS attempts,
          cp.name AS campaign_name
        FROM pub_publications p
        JOIN pub_content_items ci ON ci.id = p.content_item_id
        JOIN pub_destinations d ON d.id = p.destination_id
        JOIN pub_connections c ON c.id = d.connection_id
        LEFT JOIN pub_campaigns cp ON cp.id = p.campaign_id
        """;

    // Trạng thái còn video trên nền tảng cần hỏi lại trạng thái (sau upload).
    private const string CheckStatuses = "'Processing', 'AwaitingUser', 'Unknown'";

    internal sealed class Row
    {
        public string Id { get; set; } = "";
        public string ContentItemId { get; set; } = "";
        public string DestinationId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string TagsJson { get; set; } = "[]";
        public PublishPrivacy Privacy { get; set; }
        public bool MadeForKids { get; set; }
        public bool ContainsSyntheticMedia { get; set; }
        public long? ScheduledAt { get; set; }
        public bool UseNativeSchedule { get; set; }
        public PublicationStatus Status { get; set; }
        public string IdempotencyKey { get; set; } = "";
        public string? JobId { get; set; }
        public string? UploadSessionUri { get; set; }
        public long? UploadBytesTotal { get; set; }
        public long UploadBytesConfirmed { get; set; }
        public long? UploadStartedAt { get; set; }
        public long? FinalChunkSentAt { get; set; }
        public long? UploadCompletedAt { get; set; }
        public string? PlatformSettingsJson { get; set; }
        public string? RemoteId { get; set; }
        public string? RemoteUrl { get; set; }
        public string? LastError { get; set; }
        public string? BlockedReason { get; set; }
        public bool IntentionalRepost { get; set; }
        public int ProcessingPolls { get; set; }
        public long? PublishedAt { get; set; }
        public string? CampaignId { get; set; }
        public string? PlanKey { get; set; }
        public bool LateResolved { get; set; }
        public string Origin { get; set; } = PublicationOrigins.Manual;
        public string? OriginRef { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }

        public Publication ToPublication() => new()
        {
            Id = Id, ContentItemId = ContentItemId, DestinationId = DestinationId, Title = Title, Description = Description, TagsJson = TagsJson,
            Privacy = Privacy, MadeForKids = MadeForKids, ContainsSyntheticMedia = ContainsSyntheticMedia, ScheduledUtc = FromMs(ScheduledAt),
            UseNativeSchedule = UseNativeSchedule, Status = Status, IdempotencyKey = IdempotencyKey, JobId = JobId,
            UploadSessionUri = UploadSessionUri, UploadBytesTotal = UploadBytesTotal, UploadBytesConfirmed = UploadBytesConfirmed,
            UploadStartedUtc = FromMs(UploadStartedAt), FinalChunkSentUtc = FromMs(FinalChunkSentAt), UploadCompletedUtc = FromMs(UploadCompletedAt),
            PlatformSettingsJson = PlatformSettingsJson, RemoteId = RemoteId, RemoteUrl = RemoteUrl, LastError = LastError,
            BlockedReason = BlockedReason, IntentionalRepost = IntentionalRepost, ProcessingPolls = ProcessingPolls,
            PublishedUtc = FromMs(PublishedAt), CampaignId = CampaignId, PlanKey = PlanKey, LateResolved = LateResolved, Origin = Origin,
            OriginRef = OriginRef, CreatedUtc = FromMs(CreatedAt), UpdatedUtc = FromMs(UpdatedAt)
        };
    }

    private sealed class RowExtra
    {
        public string ContentTitle { get; set; } = "";
        public string DestinationTitle { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public string ConnectionName { get; set; } = "";
        public SocialPlatform Platform { get; set; }
        public int Attempts { get; set; }
        public string? CampaignName { get; set; }
    }

    private sealed class RiskRow
    {
        public string PublicationId { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public string DestinationId { get; set; } = "";
        public long AtMs { get; set; }
        public string ContentItemId { get; set; } = "";
        public string VideoSha256 { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private sealed class AttemptRow
    {
        public string Id { get; set; } = "";
        public string PublicationId { get; set; } = "";
        public string? JobId { get; set; }
        public long StartedAt { get; set; }
        public long? FinishedAt { get; set; }
        public string? Outcome { get; set; }
        public string? Error { get; set; }
        public string? RouteKind { get; set; }
        public string? RouteRef { get; set; }
        public string? EgressIp { get; set; }
        public string? EgressCountry { get; set; }
        public long? IpCheckedAt { get; set; }
    }

    private static PublicationRow Map(Row p, RowExtra x) =>
        new(p.ToPublication(), x.ContentTitle, x.DestinationTitle, x.ConnectionId, x.ConnectionName, x.Platform, x.Attempts, x.CampaignName);

    public async Task CreateBatchAsync(IReadOnlyList<Publication> publications, IReadOnlyDictionary<string, DateTimeOffset> dispatchUtc,
        CancellationToken ct)
    {
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var p in publications) await InsertAsync(db, tx, p, dispatchUtc, ct);
        await tx.CommitAsync(ct);
    }

    internal static Task InsertAsync(DbConnection db, DbTransaction tx, Publication p, IReadOnlyDictionary<string, DateTimeOffset> dispatchUtc,
        CancellationToken ct)
    {
        var now = DapperSetup.NowMs;
        // Chỉ bài Scheduled chờ bộ điều phối; đăng theo lịch của nền tảng thì upload sớm (nền tảng tự công khai đúng giờ).
        long? dispatch = p.Status != PublicationStatus.Scheduled ? null
            : dispatchUtc.TryGetValue(p.Id, out var d) ? ToMs(d)
            : p.UseNativeSchedule ? now
            : ToMs(p.ScheduledUtc) ?? now;
        return db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pub_publications(id, content_item_id, destination_id, title, description, tags_json, privacy, made_for_kids,
              contains_synthetic_media, scheduled_at, use_native_schedule, status, idempotency_key, job_id, dispatch_at, intentional_repost,
              campaign_id, plan_key, platform_settings_json, origin, origin_ref, created_at, updated_at)
            VALUES (@Id, @ContentItemId, @DestinationId, @Title, @Description, @TagsJson, @Privacy, @MadeForKids,
              @ContainsSyntheticMedia, @scheduled, @UseNativeSchedule, @Status, @IdempotencyKey, NULL, @dispatch, @IntentionalRepost,
              @CampaignId, @PlanKey, @PlatformSettingsJson, @Origin, @OriginRef, @created, @updated)
            """,
            new
            {
                p.Id, p.ContentItemId, p.DestinationId, p.Title, p.Description, p.TagsJson, Privacy = p.Privacy.ToString(), p.MadeForKids,
                p.ContainsSyntheticMedia, scheduled = ToMs(p.ScheduledUtc), p.UseNativeSchedule, Status = p.Status.ToString(), p.IdempotencyKey,
                dispatch, p.IntentionalRepost, p.CampaignId, p.PlanKey, p.PlatformSettingsJson, p.Origin, p.OriginRef,
                created = ToMs(p.CreatedUtc), updated = ToMs(p.UpdatedUtc)
            }, tx, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Publication>> ListDispatchableAsync(DateTimeOffset nowUtc, int limit, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM pub_publications p
            WHERE p.status IN ('Scheduled', 'RetryScheduled') AND p.job_id IS NULL AND p.dispatch_at IS NOT NULL AND p.dispatch_at <= @now
            ORDER BY p.dispatch_at, p.id LIMIT @limit
            """, new { now = ToMs(nowUtc), limit }, cancellationToken: ct))).Select(r => r.ToPublication()).ToList();
    }

    public async Task<bool> TryBindJobAsync(string id, string jobId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET job_id = @jobId
            WHERE id = @id AND ((job_id IS NULL AND status IN ('Scheduled', 'RetryScheduled')) OR job_id = @jobId)
            """, new { id, jobId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> ReleaseJobAsync(string id, string jobId, DateTimeOffset dispatchUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "UPDATE pub_publications SET job_id = NULL, dispatch_at = @dispatch WHERE id = @id AND job_id = @jobId",
            new { id, jobId, dispatch = ToMs(dispatchUtc) }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<Publication>> ListDueStatusChecksAsync(DateTimeOffset nowUtc, int limit, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM pub_publications p
            WHERE p.status IN ({CheckStatuses}) AND p.next_check_at IS NOT NULL AND p.next_check_at <= @now
            ORDER BY p.next_check_at, p.id LIMIT @limit
            """, new { now = ToMs(nowUtc), limit }, cancellationToken: ct))).Select(r => r.ToPublication()).ToList();
    }

    public async Task SetNextCheckAsync(string id, DateTimeOffset? nextCheckUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE pub_publications SET next_check_at = @next WHERE id = @id",
            new { id, next = ToMs(nextCheckUtc) }, cancellationToken: ct));
    }

    public async Task SetAttemptRouteAsync(string attemptId, string routeKind, string? routeRef, string? egressIp, string? egressCountry,
        DateTimeOffset? checkedUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publish_attempts SET route_kind = @routeKind, route_ref = @routeRef, egress_ip = @egressIp, egress_country = @egressCountry,
              ip_checked_at = @checkedAt
            WHERE id = @attemptId
            """, new { attemptId, routeKind, routeRef, egressIp, egressCountry, checkedAt = ToMs(checkedUtc) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Publication>> ListByOriginAsync(string origin, string? originRef, int limit, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM pub_publications p
            WHERE p.origin = @origin AND (@originRef IS NULL OR p.origin_ref = @originRef)
            ORDER BY p.created_at DESC, p.id LIMIT @limit
            """, new { origin, originRef, limit }, cancellationToken: ct))).Select(r => r.ToPublication()).ToList();
    }

    public async Task<Publication?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_publications p WHERE p.id = @id", new { id }, cancellationToken: ct));
        return row?.ToPublication();
    }

    public async Task<PublicationRow?> GetRowAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row, RowExtra, PublicationRow>(new CommandDefinition(
            RowSelect + " WHERE p.id = @id", new { id }, cancellationToken: ct), Map, splitOn: "content_title")).SingleOrDefault();
    }

    public async Task<(IReadOnlyList<PublicationRow> Items, int Total)> QueryAsync(PublicationQuery q, CancellationToken ct)
    {
        await using var db = factory.Open();
        const string where = """
             WHERE (@status IS NULL OR p.status = @status) AND (@destinationId IS NULL OR p.destination_id = @destinationId)
               AND (@from IS NULL OR p.scheduled_at >= @from) AND (@to IS NULL OR p.scheduled_at < @to)
               AND (@campaignId IS NULL OR p.campaign_id = @campaignId)
            """;
        var args = new
        {
            status = q.Status?.ToString(), destinationId = q.DestinationId, from = ToMs(q.FromUtc), to = ToMs(q.ToUtc), campaignId = q.CampaignId,
            size = q.PageSize, offset = (q.Page - 1) * q.PageSize
        };
        // Xem theo ngày (lịch) thì xếp theo giờ đăng tăng dần; danh sách chung thì mới nhất lên đầu.
        var order = q.FromUtc is not null || q.ToUtc is not null
            ? " ORDER BY p.scheduled_at, p.id"
            : " ORDER BY COALESCE(p.scheduled_at, p.created_at) DESC, p.id";
        var total = await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM pub_publications p" + where, args, cancellationToken: ct));
        var items = (await db.QueryAsync<Row, RowExtra, PublicationRow>(new CommandDefinition(
            RowSelect + where + order + " LIMIT @size OFFSET @offset", args, cancellationToken: ct), Map, splitOn: "content_title")).ToList();
        return (items, total);
    }

    public async Task<bool> TransitionAsync(string id, PublicationStatus to, string? lastError, string? blockedReason, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET status = @to, last_error = @lastError, blocked_reason = @blockedReason, updated_at = @now,
              next_check_at = CASE WHEN @to IN ('Published', 'Failed', 'Cancelled') THEN NULL ELSE next_check_at END
            WHERE id = @id AND status IN @from
            """,
            new { id, to = to.ToString(), lastError, blockedReason, now = DapperSetup.NowMs, from = Names(PublicationRules.SourcesOf(to)) },
            cancellationToken: ct)) > 0;
    }

    public async Task<bool> RequeueAsync(PublicationRequeue r, CancellationToken ct)
    {
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);

        // Không xếp job ở đây: bỏ job cũ (handler đang chạy tự bỏ qua vì job_id không còn là nó), bộ điều phối xếp lại khi tới mốc.
        var sets = new List<string>
        {
            "status = @to", "job_id = NULL", "dispatch_at = @dispatch", "last_error = NULL", "blocked_reason = NULL", "updated_at = @now",
            "intentional_repost = CASE WHEN @repost = 1 THEN 1 ELSE intentional_repost END",
            // Bài đã có video trên nền tảng quay lại hàng kiểm tra trạng thái (bộ điều phối chỉ xếp upload cho Scheduled/RetryScheduled).
            $"next_check_at = CASE WHEN @to IN ({CheckStatuses}) THEN @dispatch ELSE NULL END"
        };
        if (r.ResetUpload)
            sets.Add("upload_session_uri = NULL, upload_bytes_total = NULL, upload_bytes_confirmed = 0, upload_started_at = NULL, "
                     + "final_chunk_sent_at = NULL, processing_polls = 0");
        if (r.Attach is not null) sets.Add("remote_id = @remoteId, remote_url = @remoteUrl, processing_polls = 0, upload_session_uri = NULL");
        // Giờ hẹn mới thì phải xét lại chính sách trễ lịch từ đầu.
        if (r.Schedule is not null) sets.Add("scheduled_at = @scheduled, use_native_schedule = @native, late_resolved = 0");

        var updated = await db.ExecuteAsync(new CommandDefinition(
            $"UPDATE pub_publications SET {string.Join(", ", sets)} WHERE id = @id AND status IN @from",
            new
            {
                id = r.PublicationId, to = r.To.ToString(), dispatch = ToMs(r.DispatchUtc), now = DapperSetup.NowMs, repost = r.IntentionalRepost,
                remoteId = r.Attach?.RemoteId, remoteUrl = r.Attach?.Url,
                scheduled = ToMs(r.Schedule?.ScheduledUtc), native = r.Schedule?.Native ?? false, from = Names(r.From)
            }, tx, cancellationToken: ct));
        if (updated == 0) return false;

        if (r.Attach is not null) await UpsertRemotePostAsync(db, tx, r.Attach, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> CancelAsync(string id, bool allowUnknown, CancellationToken ct)
    {
        var from = PublicationRules.SourcesOf(PublicationStatus.Cancelled).Where(s => allowUnknown || s != PublicationStatus.Unknown);
        await using var db = factory.Open();
        // Điều kiện nằm trong cùng câu lệnh với mốc "đã gửi chunk cuối" để hủy và gửi chunk cuối không thể cùng thắng.
        // Job đã xếp không bị hủy ở đây: handler tự bỏ qua bài không còn chờ đăng.
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET status = 'Cancelled', blocked_reason = NULL, next_check_at = NULL, updated_at = @now
            WHERE id = @id AND status IN @from AND (@allowUnknown = 1 OR (remote_id IS NULL AND final_chunk_sent_at IS NULL))
            """, new { id, now = DapperSetup.NowMs, allowUnknown, from = Names(from) }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> SaveUploadSessionAsync(string id, string sessionUri, long totalBytes, DateTimeOffset startedUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET upload_session_uri = @sessionUri, upload_bytes_total = @totalBytes, upload_bytes_confirmed = 0,
              upload_started_at = @started, final_chunk_sent_at = NULL, updated_at = @started
            WHERE id = @id AND status = 'Uploading'
            """, new { id, sessionUri, totalBytes, started = ToMs(startedUtc) }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> SaveUploadProgressAsync(string id, long bytesConfirmed, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET upload_bytes_confirmed = @bytesConfirmed, updated_at = @now WHERE id = @id AND status = 'Uploading'
            """, new { id, bytesConfirmed, now = DapperSetup.NowMs }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> MarkFinalChunkSentAsync(string id, DateTimeOffset at, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET final_chunk_sent_at = COALESCE(final_chunk_sent_at, @at), updated_at = @at
            WHERE id = @id AND status = 'Uploading'
            """, new { id, at = ToMs(at) }, cancellationToken: ct)) > 0;
    }

    public async Task ClearUploadSessionAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET upload_session_uri = NULL, upload_bytes_confirmed = 0, final_chunk_sent_at = NULL, updated_at = @now
            WHERE id = @id
            """, new { id, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<bool> AttachRemoteAsync(string id, RemotePost post, CancellationToken ct)
    {
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        // Session URI không còn cần sau khi có video id: xóa luôn (URI phiên upload có thể dùng được mà không cần token).
        var updated = await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET remote_id = @RemoteId, remote_url = @Url, status = 'Processing', processing_polls = 0,
              upload_session_uri = NULL, last_error = NULL, upload_completed_at = @now, updated_at = @now
            WHERE id = @id AND remote_id IS NULL AND status IN ('Uploading', 'RetryScheduled', 'Scheduled', 'Unknown', 'NeedsAction')
            """, new { id, post.RemoteId, post.Url, now = DapperSetup.NowMs }, tx, cancellationToken: ct));
        if (updated == 0) return false;
        await UpsertRemotePostAsync(db, tx, post, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static Task UpsertRemotePostAsync(DbConnection db, DbTransaction tx, RemotePost p, CancellationToken ct) =>
        db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pub_remote_posts(id, publication_id, destination_id, platform, remote_id, url, title, remote_status, discovered_by,
              last_synced_at, created_at)
            VALUES (@Id, @PublicationId, @DestinationId, @Platform, @RemoteId, @Url, @Title, @RemoteStatus, @DiscoveredBy,
              @synced, @created)
            ON CONFLICT(destination_id, remote_id) DO UPDATE SET
              publication_id = excluded.publication_id, discovered_by = excluded.discovered_by, last_synced_at = excluded.last_synced_at
            """,
            new
            {
                p.Id, p.PublicationId, p.DestinationId, Platform = p.Platform.ToString(), p.RemoteId, p.Url, p.Title, p.RemoteStatus,
                p.DiscoveredBy, synced = ToMs(p.LastSyncedUtc), created = ToMs(p.CreatedUtc)
            }, tx, cancellationToken: ct));

    public async Task<bool> MarkPublishedAsync(string id, DateTimeOffset at, string? remoteStatus, string? remoteUrl, CancellationToken ct)
    {
        var atMs = ToMs(at);
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        var updated = await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET status = 'Published', published_at = @at, last_error = NULL, remote_url = COALESCE(@remoteUrl, remote_url),
              next_check_at = NULL, updated_at = @at
            WHERE id = @id AND status IN ('Processing', 'AwaitingUser')
            """, new { id, at = atMs, remoteUrl }, tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_remote_posts SET remote_status = @remoteStatus, url = COALESCE(@remoteUrl, url), last_synced_at = @at
            WHERE publication_id = @id
            """, new { id, at = atMs, remoteStatus, remoteUrl }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return updated > 0;
    }

    public async Task<bool> SetPlatformSettingsAsync(string id, string? platformSettingsJson, IReadOnlyCollection<PublicationStatus> from,
        CancellationToken ct)
    {
        await using var db = factory.Open();
        // Chỉ khi chưa bắt đầu upload: lựa chọn đã gửi cho nền tảng thì không đổi được nữa.
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET platform_settings_json = @platformSettingsJson, updated_at = @now
            WHERE id = @id AND status IN @from AND upload_session_uri IS NULL AND remote_id IS NULL
            """, new { id, platformSettingsJson, now = DapperSetup.NowMs, from = Names(from) }, cancellationToken: ct)) > 0;
    }

    public async Task SetProcessingPollsAsync(string id, int polls, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE pub_publications SET processing_polls = @polls, updated_at = @now WHERE id = @id",
            new { id, polls, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Publication>> FindOnDestinationAsync(string contentItemId, string destinationId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_publications p WHERE p.content_item_id = @contentItemId AND p.destination_id = @destinationId",
            new { contentItemId, destinationId }, cancellationToken: ct))).Select(r => r.ToPublication()).ToList();
    }

    public async Task<IReadOnlyList<RiskHistoryRow>> ListForRiskAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<RiskRow>(new CommandDefinition("""
            SELECT p.id AS publication_id, d.connection_id, p.destination_id, COALESCE(p.published_at, p.scheduled_at, p.created_at) AS at_ms,
              p.content_item_id, ci.video_sha256, p.description
            FROM pub_publications p
            JOIN pub_destinations d ON d.id = p.destination_id
            JOIN pub_content_items ci ON ci.id = p.content_item_id
            WHERE p.status NOT IN ('Draft', 'Cancelled', 'Failed')
              AND COALESCE(p.published_at, p.scheduled_at, p.created_at) >= @from
              AND COALESCE(p.published_at, p.scheduled_at, p.created_at) < @to
            """, new { from = ToMs(fromUtc), to = ToMs(toUtc) }, cancellationToken: ct));
        return rows.Select(r => new RiskHistoryRow(r.PublicationId, r.ConnectionId, FromMs(r.AtMs), r.ContentItemId, r.VideoSha256, r.Description,
                r.DestinationId))
            .ToList();
    }

    public async Task<IReadOnlyList<Publication>> ListByConnectionAsync(string connectionId, IReadOnlyCollection<PublicationStatus> statuses,
        CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM pub_publications p JOIN pub_destinations d ON d.id = p.destination_id
            WHERE d.connection_id = @connectionId AND p.status IN @statuses
            ORDER BY p.scheduled_at, p.id
            """, new { connectionId, statuses = Names(statuses) }, cancellationToken: ct))).Select(r => r.ToPublication()).ToList();
    }

    public async Task<IReadOnlySet<string>> RemoteIdsOnDestinationAsync(string destinationId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<string>(new CommandDefinition("""
            SELECT remote_id FROM pub_publications WHERE destination_id = @destinationId AND remote_id IS NOT NULL
            UNION SELECT remote_id FROM pub_remote_posts WHERE destination_id = @destinationId AND publication_id IS NOT NULL
            """, new { destinationId }, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<int> CountForConnectionAsync(string connectionId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM pub_publications p JOIN pub_destinations d ON d.id = p.destination_id WHERE d.connection_id = @connectionId",
            new { connectionId }, cancellationToken: ct));
    }

    public async Task<PublishSummary> SummaryAsync(DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        var r = await db.QuerySingleAsync<SummaryRow>(new CommandDefinition("""
            SELECT
              COUNT(CASE WHEN scheduled_at >= @start AND scheduled_at < @end AND status NOT IN ('Cancelled', 'Draft') THEN 1 END) AS Today,
              COUNT(CASE WHEN published_at >= @start AND published_at < @end THEN 1 END) AS TodayPublished,
              COUNT(CASE WHEN status = 'Failed' THEN 1 END) AS Failed,
              COUNT(CASE WHEN status = 'NeedsAction' THEN 1 END) AS NeedsAction,
              COUNT(CASE WHEN status = 'Unknown' THEN 1 END) AS Unknown,
              COUNT(CASE WHEN status IN ('Uploading', 'Processing', 'RetryScheduled', 'AwaitingUser') THEN 1 END) AS InProgress
            FROM pub_publications
            """, new { start = ToMs(dayStartUtc), end = ToMs(dayEndUtc) }, cancellationToken: ct));
        return new PublishSummary(r.Today, r.TodayPublished, r.Failed, r.NeedsAction, r.Unknown, r.InProgress);
    }

    private sealed class SummaryRow
    {
        public int Today { get; set; }
        public int TodayPublished { get; set; }
        public int Failed { get; set; }
        public int NeedsAction { get; set; }
        public int Unknown { get; set; }
        public int InProgress { get; set; }
    }

    public async Task<string> StartAttemptAsync(string publicationId, string? jobId, DateTimeOffset at, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO pub_publish_attempts(id, publication_id, job_id, started_at) VALUES (@id, @publicationId, @jobId, @at)",
            new { id, publicationId, jobId, at = ToMs(at) }, cancellationToken: ct));
        return id;
    }

    public async Task FinishAttemptAsync(string attemptId, string outcome, string? error, DateTimeOffset at, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE pub_publish_attempts SET finished_at = @at, outcome = @outcome, error = @error WHERE id = @attemptId",
            new { attemptId, outcome, error, at = ToMs(at) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PublishAttempt>> ListAttemptsAsync(string publicationId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<AttemptRow>(new CommandDefinition("""
            SELECT id, publication_id, job_id, started_at, finished_at, outcome, error, route_kind, route_ref, egress_ip, egress_country, ip_checked_at
            FROM pub_publish_attempts WHERE publication_id = @publicationId ORDER BY started_at, id
            """, new { publicationId }, cancellationToken: ct))).Select(ToAttempt).ToList();
    }

    private const string ConnectionAttempts = """
        SELECT a.id, a.publication_id, a.job_id, a.started_at, a.finished_at, a.outcome, a.error, a.route_kind, a.route_ref, a.egress_ip,
          a.egress_country, a.ip_checked_at
        FROM pub_publish_attempts a
        JOIN pub_publications p ON p.id = a.publication_id
        JOIN pub_destinations d ON d.id = p.destination_id
        """;

    public async Task<IReadOnlyList<PublishAttempt>> ListConnectionAttemptsAsync(string connectionId, int limit, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<AttemptRow>(new CommandDefinition($"""
            {ConnectionAttempts}
            WHERE d.connection_id = @connectionId AND a.route_kind IS NOT NULL
            ORDER BY a.started_at DESC, a.id DESC LIMIT @limit
            """, new { connectionId, limit }, cancellationToken: ct))).Select(ToAttempt).ToList();
    }

    public async Task<PublishAttempt?> LastSucceededEgressAsync(string connectionId, CancellationToken ct)
    {
        await using var db = factory.Open();
        var row = await db.QueryFirstOrDefaultAsync<AttemptRow>(new CommandDefinition($"""
            {ConnectionAttempts}
            WHERE d.connection_id = @connectionId AND a.egress_ip IS NOT NULL AND a.outcome IN @outcomes
            ORDER BY a.started_at DESC, a.id DESC LIMIT 1
            """, new { connectionId, outcomes = PublishAttempt.SucceededOutcomes }, cancellationToken: ct));
        return row is null ? null : ToAttempt(row);
    }

    public async Task<IReadOnlyList<ConnectionEgress>> ListLatestEgressAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        // SQLite: cột trần đi cùng MAX() lấy từ đúng dòng có started_at lớn nhất của nhóm.
        return (await db.QueryAsync<(string ConnectionId, string EgressIp, string? EgressCountry, long StartedAt)>(new CommandDefinition("""
            SELECT d.connection_id, a.egress_ip, a.egress_country, MAX(a.started_at)
            FROM pub_publish_attempts a
            JOIN pub_publications p ON p.id = a.publication_id
            JOIN pub_destinations d ON d.id = p.destination_id
            WHERE a.egress_ip IS NOT NULL
            GROUP BY d.connection_id
            """, cancellationToken: ct)))
            .Select(r => new ConnectionEgress(r.ConnectionId, r.EgressIp, r.EgressCountry)).ToList();
    }

    private static PublishAttempt ToAttempt(AttemptRow a) => new()
    {
        Id = a.Id, PublicationId = a.PublicationId, JobId = a.JobId, StartedUtc = FromMs(a.StartedAt), FinishedUtc = FromMs(a.FinishedAt),
        Outcome = a.Outcome, Error = a.Error, RouteKind = a.RouteKind, RouteRef = a.RouteRef, EgressIp = a.EgressIp,
        EgressCountry = a.EgressCountry, IpCheckedUtc = FromMs(a.IpCheckedAt)
    };
}

[Service(As = [typeof(IQuotaLedger)])]
public sealed class QuotaLedger(IDbConnectionFactory factory) : IQuotaLedger
{
    private const string Caller = "publishing";

    private const string UsedSql = """
        SELECT COALESCE(SUM(units), 0) AS used FROM quota_usage WHERE project_key = @projectKey AND created_at >= @start AND created_at < @end
        """;

    // Dòng ghi phải nằm trong cửa sổ ngày caller tính (đồng hồ lệch/biên ngày) thì mới được cộng vào đúng ngày đó.
    private static long StampIn(long start, long end) => Math.Max(start, Math.Min(DapperSetup.NowMs, end - 1));

    public async Task<bool> TryConsumeAsync(string platform, string projectKey, string endpoint, int units, int limit, DateTimeOffset dayStartUtc,
        DateTimeOffset dayEndUtc, CancellationToken ct)
    {
        long start = ToMs(dayStartUtc), end = ToMs(dayEndUtc);
        await using var db = factory.Open();
        // Một câu INSERT…SELECT giữ khoá ghi suốt lúc cộng tổng: hai job cùng lúc không thể cùng vượt hạn mức.
        return await db.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO quota_usage(platform, endpoint, units, caller, created_at, project_key)
            SELECT @platform, @endpoint, @units, @caller, @at, @projectKey
            WHERE ({UsedSql}) + @units <= @limit
            """, new { platform, endpoint, units, caller = Caller, at = StampIn(start, end), projectKey, start, end, limit },
            cancellationToken: ct)) > 0;
    }

    public async Task MarkExhaustedAsync(string platform, string projectKey, int limit, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc,
        CancellationToken ct)
    {
        long start = ToMs(dayStartUtc), end = ToMs(dayEndUtc);
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO quota_usage(platform, endpoint, units, caller, created_at, project_key)
            SELECT @platform, 'quota_exhausted', @limit - used, @caller, @at, @projectKey
            FROM ({UsedSql})
            WHERE @limit - used > 0
            """, new { platform, caller = Caller, at = StampIn(start, end), projectKey, start, end, limit }, cancellationToken: ct));
    }

    public async Task<int> GetUsedAsync(string projectKey, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            UsedSql, new { projectKey, start = ToMs(dayStartUtc), end = ToMs(dayEndUtc) }, cancellationToken: ct));
    }
}
