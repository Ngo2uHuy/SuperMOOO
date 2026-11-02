-- Cấu hình & bí mật
CREATE TABLE app_setting(key TEXT PRIMARY KEY, value_json TEXT NOT NULL, updated_at INTEGER NOT NULL);
CREATE TABLE secret(scope TEXT NOT NULL, name TEXT NOT NULL, cipher BLOB NOT NULL, hint TEXT,
  status TEXT NOT NULL DEFAULT 'ok', updated_at INTEGER NOT NULL, PRIMARY KEY(scope, name));       -- status: ok|invalid_format|rejected
CREATE TABLE platform_session(platform TEXT PRIMARY KEY, auth_type TEXT NOT NULL DEFAULT 'none',
  status TEXT NOT NULL DEFAULT 'not_configured', message TEXT, checked_at INTEGER, expires_at INTEGER, updated_at INTEGER NOT NULL);

-- Hàng đợi
CREATE TABLE job(id TEXT PRIMARY KEY, type TEXT NOT NULL, title TEXT NOT NULL, lane TEXT NOT NULL, status TEXT NOT NULL,
  priority INTEGER NOT NULL DEFAULT 5, payload_json TEXT NOT NULL, result_json TEXT, error TEXT, progress REAL NOT NULL DEFAULT 0,
  message TEXT, attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 1, timeout_s INTEGER,
  parent_id TEXT REFERENCES job(id) ON DELETE CASCADE, pipeline_id TEXT, owner TEXT, not_before INTEGER,
  started_at INTEGER, finished_at INTEGER, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
CREATE INDEX ix_job_pick ON job(status, lane, priority, created_at);
CREATE INDEX ix_job_owner ON job(owner, created_at DESC);
CREATE TABLE job_step(job_id TEXT NOT NULL REFERENCES job(id) ON DELETE CASCADE, idx INTEGER NOT NULL, step_id TEXT NOT NULL,
  kind TEXT NOT NULL, status TEXT NOT NULL, cloud_billed INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0,
  state_json TEXT, error TEXT, started_at INTEGER, finished_at INTEGER, PRIMARY KEY(job_id, idx));
CREATE TABLE job_event(id INTEGER PRIMARY KEY, job_id TEXT NOT NULL REFERENCES job(id) ON DELETE CASCADE,
  at INTEGER NOT NULL, level TEXT NOT NULL, message TEXT NOT NULL);
CREATE INDEX ix_job_event ON job_event(job_id, id);

-- File & nội dung
CREATE TABLE media_file(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, kind TEXT NOT NULL,   -- download|output|publish|import|legacy
  operation TEXT, source_url TEXT, size_bytes INTEGER, mime TEXT, duration_ms INTEGER, sha256 TEXT,
  job_id TEXT REFERENCES job(id) ON DELETE SET NULL, created_at INTEGER NOT NULL);
CREATE INDEX ix_media_kind ON media_file(kind, created_at DESC);
CREATE TABLE content_item(id INTEGER PRIMARY KEY, platform TEXT NOT NULL, kind TEXT NOT NULL,     -- video|post|note|channel|page|profile|product|ad
  external_id TEXT NOT NULL, title TEXT, author_id TEXT, author_name TEXT, url TEXT, thumbnail_url TEXT, media_urls_json TEXT,
  content TEXT, published_at INTEGER, duration_ms INTEGER, view_count INTEGER, like_count INTEGER, comment_count INTEGER,
  language TEXT, category TEXT, raw_json TEXT,
  in_library INTEGER NOT NULL DEFAULT 0, project TEXT, campaign TEXT, tags_json TEXT, favorite INTEGER NOT NULL DEFAULT 0,
  duplicate_of INTEGER REFERENCES content_item(id) ON DELETE SET NULL, media_file_id INTEGER REFERENCES media_file(id) ON DELETE SET NULL,
  processed_json TEXT, first_seen_at INTEGER NOT NULL, last_seen_at INTEGER NOT NULL, last_crawled_at INTEGER,
  UNIQUE(platform, kind, external_id));
CREATE INDEX ix_content_library ON content_item(in_library, platform, last_seen_at DESC);
CREATE INDEX ix_content_project ON content_item(project, campaign);
CREATE TABLE crawl_run(id TEXT PRIMARY KEY, platform TEXT NOT NULL, run_type TEXT NOT NULL,      -- scan_channel|search|discover|hashtag|trending|meta_ads|comments
  query TEXT, filters_json TEXT NOT NULL DEFAULT '{}', status TEXT NOT NULL, item_count INTEGER NOT NULL DEFAULT 0, error TEXT,
  job_id TEXT REFERENCES job(id) ON DELETE SET NULL, started_at INTEGER NOT NULL, finished_at INTEGER);
CREATE INDEX ix_crawl_run ON crawl_run(platform, run_type, started_at DESC);
CREATE TABLE crawl_run_item(run_id TEXT NOT NULL REFERENCES crawl_run(id) ON DELETE CASCADE,
  content_item_id INTEGER NOT NULL REFERENCES content_item(id) ON DELETE CASCADE, rank INTEGER, PRIMARY KEY(run_id, content_item_id));

-- Bình luận, theo dõi, chỉ số
CREATE TABLE comment_campaign(id INTEGER PRIMARY KEY, title TEXT, url TEXT NOT NULL, platform TEXT, content_type TEXT,
  comment_count INTEGER, positive_count INTEGER, negative_count INTEGER, neutral_count INTEGER, lead_count INTEGER,
  summary TEXT, created_at INTEGER NOT NULL);
CREATE TABLE comment(id INTEGER PRIMARY KEY, campaign_id INTEGER NOT NULL REFERENCES comment_campaign(id) ON DELETE CASCADE,
  author TEXT, profile_url TEXT, text TEXT, likes INTEGER, created TEXT, sentiment TEXT,
  has_phone INTEGER, has_email INTEGER, is_lead INTEGER, lead_type TEXT);
CREATE INDEX ix_comment_campaign ON comment(campaign_id);
CREATE INDEX ix_comment_lead ON comment(is_lead);
CREATE TABLE watch_target(id INTEGER PRIMARY KEY, name TEXT, url TEXT NOT NULL, platform TEXT NOT NULL,
  target_type TEXT NOT NULL DEFAULT 'channel', frequency_hours INTEGER NOT NULL DEFAULT 24, status TEXT NOT NULL DEFAULT 'active',
  content_item_id INTEGER REFERENCES content_item(id) ON DELETE SET NULL, last_checked_at INTEGER, created_at INTEGER NOT NULL);
CREATE TABLE metric_sample(id INTEGER PRIMARY KEY, content_item_id INTEGER REFERENCES content_item(id) ON DELETE CASCADE,
  platform TEXT NOT NULL, entity_key TEXT NOT NULL, entity_title TEXT, url TEXT, metric TEXT NOT NULL, value REAL NOT NULL, recorded_at INTEGER NOT NULL);
CREATE INDEX ix_metric ON metric_sample(platform, entity_key, metric, recorded_at);
CREATE TABLE audit_log(id INTEGER PRIMARY KEY, source TEXT NOT NULL, action TEXT NOT NULL, target TEXT,
  status TEXT NOT NULL, records_count INTEGER, created_at INTEGER NOT NULL);
CREATE INDEX ix_audit ON audit_log(created_at DESC);
CREATE TABLE quota_usage(id INTEGER PRIMARY KEY, platform TEXT NOT NULL, endpoint TEXT NOT NULL, units INTEGER NOT NULL,
  caller TEXT NOT NULL DEFAULT 'system', created_at INTEGER NOT NULL);
CREATE INDEX ix_quota ON quota_usage(platform, created_at);

-- Đăng bài & reup
CREATE TABLE publish_account(id TEXT PRIMARY KEY, platform TEXT NOT NULL, name TEXT NOT NULL, external_id TEXT,
  status TEXT NOT NULL DEFAULT 'connected', created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);   -- token ở secret(account:<id>)
CREATE TABLE publish_post(id TEXT PRIMARY KEY, account_id TEXT NOT NULL REFERENCES publish_account(id) ON DELETE CASCADE,
  origin TEXT NOT NULL, origin_ref TEXT,                                                       -- manual|auto_workflow|auto_reup
  media_file_id INTEGER REFERENCES media_file(id) ON DELETE SET NULL, media_url TEXT, media_kind TEXT NOT NULL DEFAULT 'video',
  title TEXT, caption TEXT, privacy TEXT NOT NULL DEFAULT 'private', due_at INTEGER NOT NULL,
  status TEXT NOT NULL,                       -- pending_review|scheduled|publishing|checking|published|failed|cancelled
  publish_id TEXT, result_json TEXT, error TEXT, attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 3,
  next_check_at INTEGER, dedupe_key TEXT UNIQUE, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
CREATE INDEX ix_post_due ON publish_post(status, due_at);
CREATE INDEX ix_post_account_due ON publish_post(account_id, due_at);
CREATE INDEX ix_post_origin ON publish_post(origin, origin_ref);
CREATE TABLE reup_rule(id TEXT PRIMARY KEY, name TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1, config_json TEXT NOT NULL,
  initialized INTEGER NOT NULL DEFAULT 0, last_check_at INTEGER, next_check_at INTEGER, message TEXT, created_at INTEGER NOT NULL);
CREATE TABLE reup_item(id TEXT PRIMARY KEY, rule_id TEXT NOT NULL REFERENCES reup_rule(id) ON DELETE CASCADE, platform TEXT NOT NULL,
  source_key TEXT NOT NULL, video_json TEXT NOT NULL, status TEXT NOT NULL, media_file_id INTEGER REFERENCES media_file(id) ON DELETE SET NULL,
  attempts INTEGER NOT NULL DEFAULT 0, next_attempt_at INTEGER, error TEXT, created_at INTEGER NOT NULL, UNIQUE(rule_id, source_key));
CREATE INDEX ix_reup_item ON reup_item(status, next_attempt_at);

-- Giọng & dự án
CREATE TABLE voice_profile(id TEXT PRIMARY KEY, provider TEXT NOT NULL, name TEXT NOT NULL, language TEXT, gender TEXT,
  ref_path TEXT, ref_text TEXT, status TEXT NOT NULL DEFAULT 'ready', extra_json TEXT, created_at INTEGER NOT NULL);  -- chỉ giọng clone/tuỳ biến
CREATE TABLE project(id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, folder TEXT NOT NULL UNIQUE,      -- localize|review|youtube|talkshow|subtitle|reup
  state_json TEXT, updated_at INTEGER NOT NULL);
