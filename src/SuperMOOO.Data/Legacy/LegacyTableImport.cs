using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuperMOOO.Data.Legacy;

// Phần bảng quan hệ của LegacyImporter: một transaction, mỗi bảng upsert/INSERT OR IGNORE theo khoá tự nhiên.
public sealed partial class LegacyImporter
{
    private static readonly HashSet<string> ContentKinds = ["video", "post", "note", "channel", "page", "profile", "product", "ad"];
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void ImportTables(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report, List<Deferred> deferred,
        Func<string, string>? decrypt)
    {
        var mediaByLegacyPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        ImportDownloadHistory(db, tx, src, report, mediaByLegacyPath);
        ImportPublishMedia(db, tx, src, report, mediaByLegacyPath);
        ImportScanRuns(db, tx, src, report);
        ImportSearchHistory(db, tx, src, report);
        ImportContentLibrary(db, tx, src, report, mediaByLegacyPath);
        ImportPlatformEntities(db, tx, src, report);
        ImportMetaAds(db, tx, src, report);
        ImportCampaignsAndComments(db, tx, src, report);
        ImportWatchlist(db, tx, src, report);
        ImportMetrics(db, tx, src, report);
        ImportAuditAndQuota(db, tx, src, report);
        ImportTaskQueue(db, tx, src, report);
        ImportPlatformSessions(db, tx, src, report);
        ImportReup(db, tx, src, report, mediaByLegacyPath);
    }

    /// <summary>INSERT OR IGNORE theo path (không ghi đè dòng đã có); trả id và cho biết có thêm mới không.</summary>
    private static (long Id, bool Inserted) EnsureMedia(DbConnection db, DbTransaction tx, string path, string kind, string? operation,
        string? url, long? size, long? createdAt)
    {
        var inserted = db.Execute("""
            INSERT OR IGNORE INTO media_file(path, kind, operation, source_url, size_bytes, created_at)
            VALUES (@path, @kind, @operation, @url, @size, @at)
            """, new { path, kind, operation, url, size, at = createdAt ?? Now }, tx) > 0;
        return (db.ExecuteScalar<long>("SELECT id FROM media_file WHERE path = @path", new { path }, tx), inserted);
    }

    private void ImportDownloadHistory(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report, Dictionary<string, long> media)
    {
        foreach (var r in src.Rows("download_history"))
        {
            var path = r.Str("filepath");
            if (path is null || r.Str("filename") is null) { report.Count("download_history", false); continue; }

            var url = r.Str("url");
            var (id, inserted) = EnsureMedia(db, tx, path, url is null ? "output" : "download", r.Str("format_type"), url,
                LegacyRow.ParseSize(r.Str("filesize")), r.Ms("downloaded_at"));
            media[path] = id;
            if (!File.Exists(path) && report.MissingFiles.Count < 200) report.MissingFiles.Add(path);
            report.Count("download_history", inserted);
        }
    }

    private void ImportPublishMedia(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report, Dictionary<string, long> media)
    {
        foreach (var r in src.Rows("publish_media"))
        {
            if (r.Str("path") is not { } path) { report.Count("publish_media", false); continue; }
            var (id, inserted) = EnsureMedia(db, tx, path, "publish", null, null, null, null);
            media[path] = id;
            report.Count("publish_media", inserted);
        }
    }

    private static void ImportScanRuns(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        foreach (var r in src.Rows("scan_runs"))
        {
            if (r.Str("id") is not { } id || r.Str("platform") is not { } platform) { report.Count("scan_runs", false); continue; }
            var n = db.Execute("""
                INSERT OR IGNORE INTO crawl_run(id, platform, run_type, query, filters_json, status, item_count, started_at, finished_at)
                VALUES (@id, @platform, 'scan_channel', @query, @filters, @status, @count, @started, @finished)
                """, new
                {
                    id, platform, query = r.Str("source_url") ?? r.Str("source_name"), filters = r.Str("filters_json") ?? "{}",
                    status = r.Str("status") ?? "completed", count = r.Long("item_count") ?? 0, started = r.Ms("started_at") ?? Now, finished = r.Ms("finished_at")
                }, tx);
            report.Count("scan_runs", n > 0);
        }
    }

    private static void ImportSearchHistory(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        foreach (var r in src.Rows("search_history"))
        {
            if (r.Long("id") is not { } id || r.Str("platform") is not { } platform) { report.Count("search_history", false); continue; }
            var n = db.Execute("""
                INSERT OR IGNORE INTO crawl_run(id, platform, run_type, query, filters_json, status, item_count, started_at)
                VALUES (@id, @platform, 'search', @query, @filters, 'completed', @count, @started)
                """, new
                {
                    id = $"legacy-search-{id}", platform, query = r.Str("query"), filters = r.Str("filters_json") ?? "{}",
                    count = r.Long("result_count") ?? 0, started = r.Ms("created_at") ?? Now
                }, tx);
            report.Count("search_history", n > 0);
        }
    }

    private static string KindOf(string? type) => type is not null && ContentKinds.Contains(type) ? type : "video";

    private void ImportContentLibrary(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report, Dictionary<string, long> media)
    {
        foreach (var r in src.Rows("content_library"))
        {
            if (r.Str("platform") is not { } platform || r.Str("external_id") is not { } externalId) { report.Count("content_library", false); continue; }

            long? mediaId = null;
            if (r.Str("local_path") is { } local)
            {
                if (!media.TryGetValue(local, out var known)) known = EnsureMedia(db, tx, local, "download", null, r.Str("source_url"), null, r.Ms("downloaded_at")).Id;
                mediaId = known;
            }

            var id = db.ExecuteScalar<long>("""
                INSERT INTO content_item(platform, kind, external_id, title, author_id, author_name, url, thumbnail_url, duration_ms,
                    view_count, like_count, comment_count, language, category, raw_json, in_library, project, campaign, tags_json,
                    favorite, media_file_id, first_seen_at, last_seen_at)
                VALUES (@platform, @kind, @externalId, @title, @authorId, @authorName, @url, @thumb, @durationMs, @views, @likes,
                    @comments, @language, @category, @raw, 1, @project, @campaign, @tags, @favorite, @mediaId, @first, @last)
                ON CONFLICT(platform, kind, external_id) DO UPDATE SET
                    in_library = 1, title = COALESCE(excluded.title, title), project = COALESCE(excluded.project, project),
                    campaign = COALESCE(excluded.campaign, campaign), tags_json = COALESCE(excluded.tags_json, tags_json),
                    favorite = MAX(favorite, excluded.favorite), media_file_id = COALESCE(excluded.media_file_id, media_file_id)
                RETURNING id
                """, new
                {
                    platform, kind = KindOf(r.Str("content_type")), externalId, title = r.Str("title"), authorId = r.Str("author_id"),
                    authorName = r.Str("author_name"), url = r.Str("source_url"), thumb = r.Str("thumbnail_url"),
                    durationMs = r.Double("duration") is { } d ? (long?)(d * 1000) : null, views = r.Long("view_count"),
                    likes = r.Long("like_count"), comments = r.Long("comment_count"), language = r.Str("language"), category = r.Str("category"),
                    raw = r.Str("raw_payload"), project = r.Str("project"), campaign = r.Str("campaign"), tags = r.Str("tags_json"),
                    favorite = r.Flag("favorite") ? 1 : 0, mediaId, first = r.Ms("first_seen_at") ?? Now, last = r.Ms("last_seen_at") ?? Now
                }, tx);

            if (r.Str("scan_run_id") is { } run)
                db.Execute("""
                    INSERT OR IGNORE INTO crawl_run_item(run_id, content_item_id) SELECT @run, @id WHERE EXISTS (SELECT 1 FROM crawl_run WHERE id = @run)
                    """, new { run, id }, tx);
            report.Count("content_library", true);
        }
    }

    private static void ImportPlatformEntities(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        foreach (var r in src.Rows("platform_entities"))
        {
            if (r.Str("platform") is not { } platform || r.Str("external_id") is not { } externalId) { report.Count("platform_entities", false); continue; }
            db.Execute("""
                INSERT INTO content_item(platform, kind, external_id, title, author_id, author_name, url, content, media_urls_json,
                    published_at, raw_json, first_seen_at, last_seen_at, last_crawled_at)
                VALUES (@platform, @kind, @externalId, @title, @authorId, @authorName, @url, @content, @media, @published, @raw, @crawled, @crawled, @crawled)
                ON CONFLICT(platform, kind, external_id) DO UPDATE SET
                    title = COALESCE(excluded.title, title), content = COALESCE(excluded.content, content),
                    media_urls_json = COALESCE(excluded.media_urls_json, media_urls_json), raw_json = COALESCE(excluded.raw_json, raw_json),
                    last_crawled_at = excluded.last_crawled_at
                """, new
                {
                    platform, kind = r.Str("entity_type") ?? "channel", externalId, title = r.Str("title"), authorId = r.Str("author_id"),
                    authorName = r.Str("author_name"), url = r.Str("url"), content = r.Str("content"), media = r.Str("media_urls"),
                    published = r.Ms("published_at"), raw = r.Str("raw_payload"), crawled = r.Ms("last_crawled_at") ?? Now
                }, tx);
            report.Count("platform_entities", true);
        }
    }

    private static void ImportMetaAds(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        foreach (var r in src.Rows("meta_ads_cache"))
        {
            if (r.Str("search_key") is not { } key || r.Str("ad_id") is not { } adId) { report.Count("meta_ads_cache", false); continue; }
            var runId = "legacy-meta_ads-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
            var at = r.Ms("created_at") ?? Now;
            db.Execute("""
                INSERT OR IGNORE INTO crawl_run(id, platform, run_type, query, status, started_at, finished_at)
                VALUES (@runId, 'meta', 'meta_ads', @key, 'completed', @at, @at)
                """, new { runId, key, at }, tx);
            var id = db.ExecuteScalar<long>("""
                INSERT INTO content_item(platform, kind, external_id, title, author_id, author_name, raw_json, first_seen_at, last_seen_at)
                VALUES ('meta', 'ad', @adId, @name, @pageId, @name, @raw, @at, @at)
                ON CONFLICT(platform, kind, external_id) DO UPDATE SET raw_json = excluded.raw_json
                RETURNING id
                """, new { adId, name = r.Str("page_name"), pageId = r.Str("page_id"), raw = r.Str("ad_data"), at }, tx);
            var added = db.Execute("INSERT OR IGNORE INTO crawl_run_item(run_id, content_item_id) VALUES (@runId, @id)", new { runId, id }, tx);
            db.Execute("UPDATE crawl_run SET item_count = (SELECT COUNT(*) FROM crawl_run_item WHERE run_id = @runId) WHERE id = @runId", new { runId }, tx);
            report.Count("meta_ads_cache", added > 0);
        }
    }

    private static void ImportCampaignsAndComments(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        var map = new Dictionary<long, long>();
        var fresh = new HashSet<long>();
        foreach (var r in src.Rows("crawl_campaigns"))
        {
            if (r.Long("id") is not { } oldId || r.Str("url") is not { } url) { report.Count("crawl_campaigns", false); continue; }
            var created = r.Ms("created_at") ?? Now;
            var existing = db.ExecuteScalar<long?>("SELECT id FROM comment_campaign WHERE url = @url AND created_at = @created", new { url, created }, tx);
            if (existing is { } known)
            {
                map[oldId] = known;
                report.Count("crawl_campaigns", false);
                continue;
            }

            var id = db.ExecuteScalar<long>("""
                INSERT INTO comment_campaign(title, url, platform, content_type, comment_count, positive_count, negative_count,
                    neutral_count, lead_count, summary, created_at)
                VALUES (@title, @url, @platform, @type, @c, @p, @n, @u, @l, @summary, @created) RETURNING id
                """, new
                {
                    title = r.Str("title"), url, platform = r.Str("platform"), type = r.Str("content_type"), c = r.Long("comment_count"),
                    p = r.Long("positive_count"), n = r.Long("negative_count"), u = r.Long("neutral_count"), l = r.Long("lead_count"),
                    summary = r.Str("summary_text"), created
                }, tx);
            map[oldId] = id;
            fresh.Add(id);
            report.Count("crawl_campaigns", true);
        }

        foreach (var r in src.Rows("comments_cache"))
        {
            if (r.Long("campaign_id") is not { } oldCampaign || !map.TryGetValue(oldCampaign, out var campaign)) { report.Count("comments_cache", false); continue; }
            if (!fresh.Contains(campaign)) { report.Count("comments_cache", false); continue; }   // chiến dịch đã nhập từ lần trước
            db.Execute("""
                INSERT INTO comment(campaign_id, author, profile_url, text, likes, created, sentiment, has_phone, has_email, is_lead, lead_type)
                VALUES (@campaign, @author, @profile, @text, @likes, @created, @sentiment, @phone, @email, @lead, @leadType)
                """, new
                {
                    campaign, author = r.Str("author"), profile = r.Str("profile_url"), text = r.Str("text"), likes = r.Long("likes"),
                    created = r.Str("created"), sentiment = r.Str("sentiment"), phone = r.Flag("has_phone") ? 1 : 0,
                    email = r.Flag("has_email") ? 1 : 0, lead = r.Flag("is_lead") ? 1 : 0, leadType = r.Str("lead_type")
                }, tx);
            report.Count("comments_cache", true);
        }
    }

    private static void ImportWatchlist(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        foreach (var r in src.Rows("watchlist"))
        {
            if (r.Str("url") is not { } url || r.Str("platform") is not { } platform) { report.Count("watchlist", false); continue; }
            var n = db.Execute("""
                INSERT INTO watch_target(name, url, platform, target_type, frequency_hours, status, last_checked_at, created_at)
                SELECT @name, @url, @platform, @type, @freq, @status, @lastCheck, @created
                WHERE NOT EXISTS (SELECT 1 FROM watch_target WHERE url = @url AND platform = @platform)
                """, new
                {
                    name = r.Str("name"), url, platform, type = r.Str("target_type") ?? "channel", freq = r.Long("frequency_hours") ?? 24,
                    status = r.Str("status") ?? "active", lastCheck = r.Ms("last_checked_at"), created = r.Ms("created_at") ?? Now
                }, tx);
            report.Count("watchlist", n > 0);
        }
    }

    private static void ImportMetrics(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        foreach (var r in src.Rows("entity_metrics_history"))
        {
            if (r.Str("entity_id") is not { } key || r.Str("platform") is not { } platform || r.Str("metric_name") is not { } metric
                || r.Double("metric_value") is not { } value) { report.Count("entity_metrics_history", false); continue; }
            var n = db.Execute("""
                INSERT INTO metric_sample(content_item_id, platform, entity_key, entity_title, url, metric, value, recorded_at)
                SELECT (SELECT id FROM content_item WHERE platform = @platform AND external_id = @key LIMIT 1),
                       @platform, @key, @title, @url, @metric, @value, @at
                WHERE NOT EXISTS (SELECT 1 FROM metric_sample WHERE platform = @platform AND entity_key = @key AND metric = @metric AND recorded_at = @at)
                """, new { platform, key, title = r.Str("entity_title"), url = r.Str("url"), metric, value, at = r.Ms("recorded_at") ?? Now }, tx);
            report.Count("entity_metrics_history", n > 0);
        }
    }

    private static void ImportAuditAndQuota(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        foreach (var r in src.Rows("compliance_audit_logs"))
        {
            if (r.Str("source") is not { } source || r.Str("action") is not { } action) { report.Count("compliance_audit_logs", false); continue; }
            var n = db.Execute("""
                INSERT INTO audit_log(source, action, target, status, records_count, created_at)
                SELECT @source, @action, @target, @status, @count, @at
                WHERE NOT EXISTS (SELECT 1 FROM audit_log WHERE source = @source AND action = @action AND created_at = @at AND IFNULL(target, '') = IFNULL(@target, ''))
                """, new { source, action, target = r.Str("target_url"), status = r.Str("status") ?? "ok", count = r.Long("records_count"), at = r.Ms("created_at") ?? Now }, tx);
            report.Count("compliance_audit_logs", n > 0);
        }

        foreach (var r in src.Rows("quota_usage_logs"))
        {
            if (r.Str("platform") is not { } platform || r.Str("endpoint") is not { } endpoint) { report.Count("quota_usage_logs", false); continue; }
            var n = db.Execute("""
                INSERT INTO quota_usage(platform, endpoint, units, caller, created_at)
                SELECT @platform, @endpoint, @units, @caller, @at
                WHERE NOT EXISTS (SELECT 1 FROM quota_usage WHERE platform = @platform AND endpoint = @endpoint AND created_at = @at AND units = @units)
                """, new { platform, endpoint, units = r.Long("units_consumed") ?? 0, caller = r.Str("caller") ?? "system", at = r.Ms("created_at") ?? Now }, tx);
            report.Count("quota_usage_logs", n > 0);
        }
    }

    /// <summary>Chỉ lịch sử job đã xong; queued/running → cancelled (không migrate job dở), bỏ noop_for_test.</summary>
    private static void ImportTaskQueue(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        foreach (var r in src.Rows("task_queue"))
        {
            var type = r.Str("task_type");
            if (r.Str("id") is not { } id || type is null || type == "noop_for_test") { report.Count("task_queue", false); continue; }

            var legacy = (r.Str("status") ?? "").ToLowerInvariant();
            var status = legacy switch
            {
                "completed" or "succeeded" or "success" or "done" => "Succeeded",
                "queued" or "running" or "pending" => "Cancelled",
                "cancelled" or "canceled" => "Cancelled",
                _ => "Failed"
            };
            if (legacy is "queued" or "running" or "pending") report.Warnings.Add($"task_queue: tác vụ {type} đang dở được chuyển thành cancelled.");

            var lane = type.StartsWith("download", StringComparison.OrdinalIgnoreCase) || type.StartsWith("crawl", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("discover", StringComparison.OrdinalIgnoreCase) || type.StartsWith("scan", StringComparison.OrdinalIgnoreCase) ? "Network" : "Media";
            var created = r.Ms("created_at") ?? Now;
            var n = db.Execute("""
                INSERT OR IGNORE INTO job(id, type, title, lane, status, priority, payload_json, result_json, error, progress, attempts,
                    max_attempts, timeout_s, started_at, finished_at, created_at, updated_at)
                VALUES (@id, @type, @type, @lane, @status, @priority, @payload, @result, @error, @progress, @attempts, @max, @timeout,
                    @started, @finished, @created, @updated)
                """, new
                {
                    id, type, lane, status, priority = r.Long("priority") ?? 5, payload = r.Str("payload_json") ?? "{}", result = r.Str("result_json"),
                    error = r.Str("error"), progress = r.Double("progress") ?? 0, attempts = r.Long("attempts") ?? 0, max = r.Long("max_attempts") ?? 1,
                    timeout = r.Long("timeout_seconds"), started = r.Ms("started_at"), finished = r.Ms("finished_at") ?? created, created,
                    updated = r.Ms("updated_at") ?? created
                }, tx);
            report.Count("task_queue", n > 0);
        }
    }

    private static void ImportPlatformSessions(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report)
    {
        foreach (var r in src.Rows("platform_sessions"))
        {
            if (r.Str("platform") is not { } platform) { report.Count("platform_sessions", false); continue; }
            var n = db.Execute("""
                INSERT OR IGNORE INTO platform_session(platform, auth_type, status, message, checked_at, expires_at, updated_at)
                VALUES (@platform, @auth, @status, @message, @lastCheck, @expires, @updated)
                """, new
                {
                    platform = LegacySettingsMap.CanonicalPlatform(platform), auth = r.Str("auth_type") ?? "none", status = r.Str("status") ?? "not_configured",
                    message = r.Str("message"), lastCheck = r.Ms("checked_at"), expires = r.Ms("expires_at"), updated = r.Ms("updated_at") ?? Now
                }, tx);
            report.Count("platform_sessions", n > 0);
        }
    }

    private static void ImportReup(DbConnection db, DbTransaction tx, LegacySource src, ImportReport report, Dictionary<string, long> media)
    {
        foreach (var r in src.Rows("reup_rules"))
        {
            if (r.Str("id") is not { } id || r.Str("config") is not { } config) { report.Count("reup_rules", false); continue; }
            var n = db.Execute("""
                INSERT OR IGNORE INTO reup_rule(id, name, enabled, config_json, initialized, last_check_at, next_check_at, message, created_at)
                VALUES (@id, @name, @enabled, @config, @init, @last, @next, @message, @created)
                """, new
                {
                    id, name = RuleName(config) ?? id, enabled = r.Flag("enabled") ? 1 : 0, config, init = r.Flag("initialized") ? 1 : 0,
                    last = r.Ms("last_check"), next = r.Ms("next_check"), message = r.Str("message"), created = r.Ms("created") ?? Now
                }, tx);
            report.Count("reup_rules", n > 0);
        }

        foreach (var r in src.Rows("reup_items"))
        {
            if (r.Str("id") is not { } id || r.Str("rule_id") is not { } rule || r.Str("platform") is not { } platform
                || r.Str("source_key") is not { } key || r.Str("video") is not { } video) { report.Count("reup_items", false); continue; }
            long? mediaId = r.Str("path") is { } path ? (media.TryGetValue(path, out var known) ? known : EnsureMedia(db, tx, path, "output", null, null, null, null).Id) : null;
            var n = db.Execute("""
                INSERT OR IGNORE INTO reup_item(id, rule_id, platform, source_key, video_json, status, media_file_id, attempts,
                    next_attempt_at, error, created_at)
                SELECT @id, @rule, @platform, @key, @video, @status, @mediaId, @attempts, @next, @error, @created
                WHERE EXISTS (SELECT 1 FROM reup_rule WHERE id = @rule)
                """, new
                {
                    id, rule, platform, key, video, status = r.Str("status") ?? "pending", mediaId, attempts = r.Long("attempts") ?? 0,
                    next = r.Ms("next_attempt"), error = r.Str("error"), created = r.Ms("created") ?? Now
                }, tx);
            report.Count("reup_items", n > 0);
        }
    }

    private static string? RuleName(string configJson) => JsonField(configJson, "name");

    private static string? JsonField(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
