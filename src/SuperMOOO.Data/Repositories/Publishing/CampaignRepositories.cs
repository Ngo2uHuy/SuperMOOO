using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Data.Sqlite;
using static SuperMOOO.Data.Repositories.Publishing.PubSql;

namespace SuperMOOO.Data.Repositories.Publishing;

[Service(As = [typeof(ICampaignRepository)])]
public sealed class CampaignRepository(IDbConnectionFactory factory) : ICampaignRepository
{
    private const string Columns = """
        id, name, status, time_zone_id, weight, automation, content_max_age_days, post_budget, max_consecutive_failures, title_template,
        description_template, privacy, evergreen, evergreen_min_days, status_reason, status_changed_at, platform_settings_json, created_at, updated_at,
        suggest_only
        """;

    // Dạng lưu JSON: giờ "HH:mm" để đọc được bằng mắt khi cần sửa tay DB.
    private sealed record SlotJson(DayOfWeek Day, string Time, string? DestinationId);
    private sealed record WindowJson(string Start, string End);

    private sealed class CampaignRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public CampaignStatus Status { get; set; }
        public string TimeZoneId { get; set; } = "UTC";
        public double Weight { get; set; }
        public AutomationLevel Automation { get; set; }
        public int? ContentMaxAgeDays { get; set; }
        public int? PostBudget { get; set; }
        public int MaxConsecutiveFailures { get; set; }
        public string TitleTemplate { get; set; } = "";
        public string DescriptionTemplate { get; set; } = "";
        public PublishPrivacy Privacy { get; set; }
        public bool Evergreen { get; set; }
        public int EvergreenMinDays { get; set; }
        public string? StatusReason { get; set; }
        public long StatusChangedAt { get; set; }
        public string? PlatformSettingsJson { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public bool SuggestOnly { get; set; }

        public Campaign ToCampaign() => new()
        {
            Id = Id, Name = Name, Status = Status, TimeZoneId = TimeZoneId, Weight = Weight, Automation = Automation,
            ContentMaxAgeDays = ContentMaxAgeDays, PostBudget = PostBudget, MaxConsecutiveFailures = MaxConsecutiveFailures,
            TitleTemplate = TitleTemplate, DescriptionTemplate = DescriptionTemplate, Privacy = Privacy, Evergreen = Evergreen,
            EvergreenMinDays = EvergreenMinDays, StatusReason = StatusReason, StatusChangedUtc = FromMs(StatusChangedAt),
            PlatformSettingsJson = PlatformSettingsJson, CreatedUtc = FromMs(CreatedAt), UpdatedUtc = FromMs(UpdatedAt), SuggestOnly = SuggestOnly
        };
    }

    private sealed class RuleRow
    {
        public string Id { get; set; } = "";
        public string CampaignId { get; set; } = "";
        public int Position { get; set; }
        public string? SourceId { get; set; }
        public string TagsJson { get; set; } = "[]";
        public string? Language { get; set; }
        public int? MinDurationSec { get; set; }
        public int? MaxDurationSec { get; set; }
        public AspectFilter Aspect { get; set; }
        public string DestinationIdsJson { get; set; } = "[]";
    }

    private sealed class PolicyRow
    {
        public string CampaignId { get; set; } = "";
        public string SlotsJson { get; set; } = "[]";
        public int MaxPerDay { get; set; }
        public int MinGapMinutes { get; set; }
        public string ForbiddenJson { get; set; } = "[]";
        public int JitterMinutes { get; set; }
        public LateMode LateMode { get; set; }
        public int LateGraceMinutes { get; set; }
        public int LateMaxCatchUp { get; set; }
        public int LateMaxLatenessMinutes { get; set; }
        public int HorizonDays { get; set; }
    }

    private sealed class PostRow
    {
        public string PublicationId { get; set; } = "";
        public string DestinationId { get; set; } = "";
        public string ContentItemId { get; set; } = "";
        public string? CampaignId { get; set; }
        public string? PlanKey { get; set; }
        public long AtMs { get; set; }
        public PublicationStatus Status { get; set; }
    }

    private sealed class LateRow
    {
        public string PublicationId { get; set; } = "";
        public string CampaignId { get; set; } = "";
        public string DestinationId { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public long ScheduledAt { get; set; }
    }

    private static TimeOnly Time(string s) => TimeOnly.ParseExact(s, "HH:mm", CultureInfo.InvariantCulture);
    private static string Text(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static CampaignRule ToRule(RuleRow r) => new()
    {
        Id = r.Id, CampaignId = r.CampaignId, Position = r.Position, SourceId = r.SourceId, Tags = StringList(r.TagsJson), Language = r.Language,
        MinDurationSec = r.MinDurationSec, MaxDurationSec = r.MaxDurationSec, Aspect = r.Aspect, DestinationIds = StringList(r.DestinationIdsJson)
    };

    private static SchedulePolicy ToPolicy(PolicyRow r) => new()
    {
        CampaignId = r.CampaignId,
        Slots = (JsonSerializer.Deserialize<List<SlotJson>>(r.SlotsJson, Json) ?? [])
            .Select(s => new CampaignSlot(s.Day, Time(s.Time), s.DestinationId)).ToList(),
        MaxPerDay = r.MaxPerDay, MinGapMinutes = r.MinGapMinutes,
        Forbidden = (JsonSerializer.Deserialize<List<WindowJson>>(r.ForbiddenJson, Json) ?? [])
            .Select(w => new TimeWindow(Time(w.Start), Time(w.End))).ToList(),
        JitterMinutes = r.JitterMinutes, LateMode = r.LateMode, LateGraceMinutes = r.LateGraceMinutes, LateMaxCatchUp = r.LateMaxCatchUp,
        LateMaxLatenessMinutes = r.LateMaxLatenessMinutes, HorizonDays = r.HorizonDays
    };

    public async Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<CampaignRow>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_campaigns ORDER BY name COLLATE NOCASE, id", cancellationToken: ct))).Select(r => r.ToCampaign()).ToList();
    }

    public async Task<Campaign?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<CampaignRow>(new CommandDefinition(
            $"SELECT {Columns} FROM pub_campaigns WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToCampaign();
    }

    public async Task<IReadOnlyList<CampaignRule>> ListRulesAsync(string? campaignId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<RuleRow>(new CommandDefinition("""
            SELECT id, campaign_id, position, source_id, tags_json, language, min_duration_sec, max_duration_sec, aspect, destination_ids_json
            FROM pub_campaign_rules WHERE @campaignId IS NULL OR campaign_id = @campaignId ORDER BY campaign_id, position, id
            """, new { campaignId }, cancellationToken: ct))).Select(ToRule).ToList();
    }

    public async Task<IReadOnlyList<SchedulePolicy>> ListPoliciesAsync(string? campaignId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<PolicyRow>(new CommandDefinition("""
            SELECT campaign_id, slots_json, max_per_day, min_gap_minutes, forbidden_json, jitter_minutes, late_mode, late_grace_minutes,
              late_max_catch_up, late_max_lateness_minutes, horizon_days
            FROM pub_schedule_policies WHERE @campaignId IS NULL OR campaign_id = @campaignId
            """, new { campaignId }, cancellationToken: ct))).Select(ToPolicy).ToList();
    }

    public async Task SaveAsync(Campaign c, IReadOnlyList<CampaignRule> rules, SchedulePolicy p, CancellationToken ct)
    {
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pub_campaigns(id, name, status, time_zone_id, weight, automation, content_max_age_days, post_budget,
              max_consecutive_failures, title_template, description_template, privacy, evergreen, evergreen_min_days, platform_settings_json,
              status_reason, status_changed_at, created_at, updated_at, suggest_only)
            VALUES (@Id, @Name, @Status, @TimeZoneId, @Weight, @Automation, @ContentMaxAgeDays, @PostBudget,
              @MaxConsecutiveFailures, @TitleTemplate, @DescriptionTemplate, @Privacy, @Evergreen, @EvergreenMinDays, @PlatformSettingsJson,
              @StatusReason, @statusChanged, @created, @updated, @SuggestOnly)
            ON CONFLICT(id) DO UPDATE SET
              name = excluded.name, time_zone_id = excluded.time_zone_id, weight = excluded.weight, automation = excluded.automation,
              content_max_age_days = excluded.content_max_age_days, post_budget = excluded.post_budget,
              max_consecutive_failures = excluded.max_consecutive_failures, title_template = excluded.title_template,
              description_template = excluded.description_template, privacy = excluded.privacy, evergreen = excluded.evergreen,
              evergreen_min_days = excluded.evergreen_min_days, platform_settings_json = excluded.platform_settings_json,
              suggest_only = excluded.suggest_only, updated_at = excluded.updated_at
            """,
            new
            {
                c.Id, c.Name, Status = c.Status.ToString(), c.TimeZoneId, c.Weight, Automation = c.Automation.ToString(), c.ContentMaxAgeDays,
                c.PostBudget, c.MaxConsecutiveFailures, c.TitleTemplate, c.DescriptionTemplate, Privacy = c.Privacy.ToString(), c.Evergreen,
                c.EvergreenMinDays, c.PlatformSettingsJson, c.SuggestOnly, c.StatusReason, statusChanged = ToMs(c.StatusChangedUtc), created = ToMs(c.CreatedUtc),
                updated = ToMs(c.UpdatedUtc)
            }, tx, cancellationToken: ct));

        await db.ExecuteAsync(new CommandDefinition("DELETE FROM pub_campaign_rules WHERE campaign_id = @Id", new { c.Id }, tx, cancellationToken: ct));
        foreach (var r in rules)
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO pub_campaign_rules(id, campaign_id, position, source_id, tags_json, language, min_duration_sec, max_duration_sec,
                  aspect, destination_ids_json)
                VALUES (@Id, @CampaignId, @Position, @SourceId, @TagsJson, @Language, @MinDurationSec, @MaxDurationSec, @Aspect, @DestinationIdsJson)
                """,
                new
                {
                    r.Id, CampaignId = c.Id, r.Position, r.SourceId, TagsJson = JsonSerializer.Serialize(r.Tags, Json), r.Language,
                    r.MinDurationSec, r.MaxDurationSec, Aspect = r.Aspect.ToString(), DestinationIdsJson = JsonSerializer.Serialize(r.DestinationIds, Json)
                }, tx, cancellationToken: ct));

        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pub_schedule_policies(campaign_id, slots_json, max_per_day, min_gap_minutes, forbidden_json, jitter_minutes, late_mode,
              late_grace_minutes, late_max_catch_up, late_max_lateness_minutes, horizon_days)
            VALUES (@CampaignId, @SlotsJson, @MaxPerDay, @MinGapMinutes, @ForbiddenJson, @JitterMinutes, @LateMode,
              @LateGraceMinutes, @LateMaxCatchUp, @LateMaxLatenessMinutes, @HorizonDays)
            ON CONFLICT(campaign_id) DO UPDATE SET
              slots_json = excluded.slots_json, max_per_day = excluded.max_per_day, min_gap_minutes = excluded.min_gap_minutes,
              forbidden_json = excluded.forbidden_json, jitter_minutes = excluded.jitter_minutes, late_mode = excluded.late_mode,
              late_grace_minutes = excluded.late_grace_minutes, late_max_catch_up = excluded.late_max_catch_up,
              late_max_lateness_minutes = excluded.late_max_lateness_minutes, horizon_days = excluded.horizon_days
            """,
            new
            {
                CampaignId = c.Id,
                SlotsJson = JsonSerializer.Serialize(p.Slots.Select(s => new SlotJson(s.Day, Text(s.Time), s.DestinationId)), Json),
                p.MaxPerDay, p.MinGapMinutes,
                ForbiddenJson = JsonSerializer.Serialize(p.Forbidden.Select(w => new WindowJson(Text(w.Start), Text(w.End))), Json),
                p.JitterMinutes, LateMode = p.LateMode.ToString(), p.LateGraceMinutes, p.LateMaxCatchUp, p.LateMaxLatenessMinutes, p.HorizonDays
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<bool> SetStatusAsync(string id, CampaignStatus status, string? reason, IReadOnlyCollection<CampaignStatus> from,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_campaigns SET status = @status, status_reason = @reason, status_changed_at = @now, updated_at = @now
            WHERE id = @id AND status IN @from
            """, new { id, status = status.ToString(), reason, now = ToMs(now), from = from.Select(f => f.ToString()).ToList() },
            cancellationToken: ct)) > 0;
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM pub_campaigns WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyDictionary<string, CampaignCounts>> CountsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<(string CampaignId, int Total, int Awaiting, int Upcoming)>(new CommandDefinition("""
            SELECT campaign_id,
              COUNT(CASE WHEN status NOT IN ('Draft') THEN 1 END),
              COUNT(CASE WHEN status = 'AwaitingApproval' THEN 1 END),
              COUNT(CASE WHEN status IN ('Scheduled', 'AwaitingApproval') AND scheduled_at >= @now THEN 1 END)
            FROM pub_publications WHERE campaign_id IS NOT NULL GROUP BY campaign_id
            """, new { now = ToMs(now) }, cancellationToken: ct));
        return rows.ToDictionary(r => r.CampaignId, r => new CampaignCounts(r.Total, r.Awaiting, r.Upcoming));
    }

    public async Task<IReadOnlyList<PublicationStatus>> RecentOutcomesAsync(string campaignId, DateTimeOffset sinceUtc, int take, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<string>(new CommandDefinition("""
            SELECT status FROM pub_publications
            WHERE campaign_id = @campaignId AND updated_at > @since AND status IN ('Published', 'Failed', 'NeedsAction', 'Unknown')
            ORDER BY updated_at DESC, id DESC LIMIT @take
            """, new { campaignId, since = ToMs(sinceUtc), take }, cancellationToken: ct))).Select(Enum.Parse<PublicationStatus>).ToList();
    }

    public async Task<IReadOnlyList<ChannelPost>> ListChannelPostsAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<PostRow>(new CommandDefinition("""
            SELECT id AS publication_id, destination_id, content_item_id, campaign_id, plan_key,
              COALESCE(published_at, scheduled_at, created_at) AS at_ms, status
            FROM pub_publications WHERE status NOT IN ('Draft', 'Cancelled', 'Failed')
            """, cancellationToken: ct)))
            .Select(r => new ChannelPost(r.PublicationId, r.DestinationId, r.ContentItemId, r.CampaignId, r.PlanKey, FromMs(r.AtMs), r.Status)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>> LoadDeficitsAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<(string DestinationId, string CampaignId, double Deficit)>(new CommandDefinition(
            "SELECT destination_id, campaign_id, deficit FROM pub_campaign_fair_share", cancellationToken: ct));
        return rows.GroupBy(r => r.DestinationId).ToDictionary(g => g.Key,
            g => (IReadOnlyDictionary<string, double>)g.ToDictionary(r => r.CampaignId, r => r.Deficit));
    }

    public async Task<bool> CommitPlanAsync(IReadOnlyList<Publication> publications, IReadOnlyDictionary<string, DateTimeOffset> dispatchUtc,
        IReadOnlyList<ActionRequired> actions, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> deficits, DateTimeOffset now,
        CancellationToken ct)
    {
        var nowMs = ToMs(now);
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            foreach (var p in publications) await PublicationRepository.InsertAsync(db, tx, p, dispatchUtc, ct);
            foreach (var a in actions)
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO action_item(id, kind, ref_id, title, detail, created_at)
                    VALUES (@Id, @Kind, @RefId, @Title, @Detail, @created)
                    ON CONFLICT(kind, ref_id) WHERE resolved_at IS NULL DO NOTHING
                    """, new { a.Id, a.Kind, a.RefId, a.Title, a.Detail, created = ToMs(a.CreatedUtc) }, tx, cancellationToken: ct));
            foreach (var (destinationId, byCampaign) in deficits)
            {
                await db.ExecuteAsync(new CommandDefinition("DELETE FROM pub_campaign_fair_share WHERE destination_id = @destinationId",
                    new { destinationId }, tx, cancellationToken: ct));
                foreach (var (campaignId, deficit) in byCampaign)
                    await db.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO pub_campaign_fair_share(destination_id, campaign_id, deficit, updated_at)
                        SELECT @destinationId, @campaignId, @deficit, @now WHERE EXISTS (SELECT 1 FROM pub_campaigns WHERE id = @campaignId)
                        """, new { destinationId, campaignId, deficit, now = nowMs }, tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            return true;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint)
        {
            // Lượt planner khác (hoặc lần chạy trước bị cắt ngang sau commit) đã tạo cùng slot: bỏ cả lượt, lần sau tính lại.
            await tx.RollbackAsync(CancellationToken.None);
            return false;
        }
    }

    public async Task<IReadOnlyList<LateCandidate>> ListLateAsync(DateTimeOffset nowUtc, string? campaignId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<LateRow>(new CommandDefinition("""
            SELECT p.id AS publication_id, p.campaign_id, p.destination_id, d.connection_id, p.scheduled_at
            FROM pub_publications p JOIN pub_destinations d ON d.id = p.destination_id
            WHERE p.campaign_id IS NOT NULL AND (@campaignId IS NULL OR p.campaign_id = @campaignId)
              AND p.status = 'Scheduled' AND p.late_resolved = 0 AND p.use_native_schedule = 0
              AND p.upload_session_uri IS NULL AND p.remote_id IS NULL AND p.final_chunk_sent_at IS NULL
              AND p.scheduled_at < @now
            ORDER BY p.scheduled_at, p.id
            """, new { now = ToMs(nowUtc), campaignId }, cancellationToken: ct)))
            .Select(r => new LateCandidate(r.PublicationId, r.CampaignId, r.DestinationId, r.ConnectionId, FromMs(r.ScheduledAt))).ToList();
    }

    public async Task<bool> MarkLateResolvedAsync(string publicationId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "UPDATE pub_publications SET late_resolved = 1 WHERE id = @publicationId AND status = 'Scheduled'",
            new { publicationId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> CancelPendingAsync(string publicationId, string reason, CancellationToken ct)
    {
        await using var db = factory.Open();
        // Job đã xếp không bị hủy ở đây: handler tự bỏ qua bài không còn chờ đăng.
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET status = 'Cancelled', last_error = @reason, blocked_reason = NULL, next_check_at = NULL, updated_at = @now
            WHERE id = @publicationId AND status IN ('Scheduled', 'AwaitingApproval')
              AND upload_session_uri IS NULL AND remote_id IS NULL AND final_chunk_sent_at IS NULL
            """, new { publicationId, reason, now = DapperSetup.NowMs }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<string>> ListPendingAsync(string campaignId, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<string>(new CommandDefinition("""
            SELECT id FROM pub_publications
            WHERE campaign_id = @campaignId AND status IN ('Scheduled', 'AwaitingApproval')
              AND upload_session_uri IS NULL AND remote_id IS NULL AND final_chunk_sent_at IS NULL
            """, new { campaignId }, cancellationToken: ct))).ToList();
    }

    public async Task<bool> RescheduleAwaitingAsync(string publicationId, DateTimeOffset scheduledUtc, CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE pub_publications SET scheduled_at = @scheduled, late_resolved = 0, updated_at = @now
            WHERE id = @publicationId AND status = 'AwaitingApproval'
            """, new { publicationId, scheduled = ToMs(scheduledUtc), now = DapperSetup.NowMs }, cancellationToken: ct)) > 0;
    }
}

[Service(As = [typeof(IContentSignatureRepository)])]
public sealed class ContentSignatureRepository(IDbConnectionFactory factory) : IContentSignatureRepository
{
    private static string Encode(IReadOnlyList<ulong> hashes) => string.Join(',', hashes.Select(h => h.ToString("x16", CultureInfo.InvariantCulture)));

    private static IReadOnlyList<ulong> Decode(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => ulong.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToList();

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ulong>>> ListAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<(string Id, string Hashes)>(new CommandDefinition(
            "SELECT content_item_id, frame_hashes FROM pub_content_signatures", cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => Decode(r.Hashes));
    }

    public async Task<IReadOnlyList<ulong>?> GetAsync(string contentItemId, CancellationToken ct)
    {
        await using var db = factory.Open();
        var text = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT frame_hashes FROM pub_content_signatures WHERE content_item_id = @contentItemId", new { contentItemId }, cancellationToken: ct));
        return text is null ? null : Decode(text);
    }

    public async Task SaveAsync(string contentItemId, IReadOnlyList<ulong> frameHashes, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pub_content_signatures(content_item_id, frame_hashes, computed_at) VALUES (@contentItemId, @hashes, @now)
            ON CONFLICT(content_item_id) DO UPDATE SET frame_hashes = excluded.frame_hashes, computed_at = excluded.computed_at
            """, new { contentItemId, hashes = Encode(frameHashes), now = ToMs(now) }, cancellationToken: ct));
    }

    public async Task<(int With, int Without)> CountAsync(CancellationToken ct)
    {
        await using var db = factory.Open();
        return await db.QuerySingleAsync<(int With, int Without)>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM pub_content_signatures),
                   (SELECT COUNT(*) FROM pub_content_items ci WHERE NOT EXISTS (SELECT 1 FROM pub_content_signatures s WHERE s.content_item_id = ci.id))
            """, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> ListMissingAsync(int limit, CancellationToken ct)
    {
        await using var db = factory.Open();
        return (await db.QueryAsync<string>(new CommandDefinition("""
            SELECT id FROM pub_content_items ci WHERE status = 'Ready'
              AND NOT EXISTS (SELECT 1 FROM pub_content_signatures s WHERE s.content_item_id = ci.id)
            ORDER BY imported_at DESC LIMIT @limit
            """, new { limit }, cancellationToken: ct))).ToList();
    }
}
