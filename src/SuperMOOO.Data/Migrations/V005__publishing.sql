-- Đăng bài (gộp CenterPost): dạng cuối của migration 0001–0008 CenterPost, tiền tố pub_, thời gian là Unix ms (*_at).
-- Không tạo lại job/secret/settings/audit/network_profiles: dùng bảng có sẵn của SuperMOOO. Cấu hình mặc định nằm trong code (section "publishing").

-- Kho thành phẩm
CREATE TABLE pub_content_sources(
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  kind TEXT NOT NULL CHECK (kind IN ('AIVideoStudio','VideoC','Folder')),
  root_path TEXT NOT NULL,
  import_mode TEXT NOT NULL CHECK (import_mode IN ('Manifest','VideoCMeta','LooseFiles')),
  recursive INTEGER NOT NULL DEFAULT 0,
  watch_enabled INTEGER NOT NULL DEFAULT 0,
  enabled INTEGER NOT NULL DEFAULT 1,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL);

CREATE TABLE pub_content_items(
  id TEXT PRIMARY KEY,
  source_id TEXT REFERENCES pub_content_sources(id) ON DELETE SET NULL,
  source_content_id TEXT,
  source_job_id TEXT,
  title TEXT NOT NULL,
  caption TEXT,
  language TEXT,
  tags_json TEXT NOT NULL DEFAULT '[]',
  duration_ms INTEGER,
  width INTEGER,
  height INTEGER,
  video_sha256 TEXT NOT NULL UNIQUE,
  status TEXT NOT NULL CHECK (status IN ('Ready','Archived')),
  imported_at INTEGER NOT NULL,
  original_path TEXT NOT NULL,
  is_ai_generated INTEGER NOT NULL DEFAULT 0,
  is_reupload INTEGER NOT NULL DEFAULT 0,
  request_id TEXT);
CREATE UNIQUE INDEX ux_pub_content_source_cid ON pub_content_items(source_id, source_content_id) WHERE source_content_id IS NOT NULL;
CREATE INDEX ix_pub_content_imported ON pub_content_items(imported_at DESC);
CREATE INDEX ix_pub_content_source ON pub_content_items(source_id);
CREATE INDEX ix_pub_content_request ON pub_content_items(request_id) WHERE request_id IS NOT NULL;

CREATE TABLE pub_media_assets(
  id TEXT PRIMARY KEY,
  content_item_id TEXT NOT NULL REFERENCES pub_content_items(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('Video','Thumbnail','Subtitle')),
  stored_path TEXT NOT NULL,
  size_bytes INTEGER NOT NULL,
  sha256 TEXT NOT NULL,
  original_path TEXT NOT NULL);
CREATE INDEX ix_pub_media_content ON pub_media_assets(content_item_id);

-- sha256 một lần, cache theo size + mtime.
CREATE TABLE pub_file_fingerprints(
  path TEXT PRIMARY KEY COLLATE NOCASE,
  size_bytes INTEGER NOT NULL,
  last_write_at INTEGER NOT NULL,
  sha256 TEXT NOT NULL);

-- Hộp "Cần xử lý" dùng chung; mỗi (kind, ref) chỉ một mục chưa xử lý để không spam.
CREATE TABLE action_item(
  id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  ref_id TEXT NOT NULL,
  title TEXT NOT NULL,
  detail TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  resolved_at INTEGER);
CREATE UNIQUE INDEX ux_action_item_open ON action_item(kind, ref_id) WHERE resolved_at IS NULL;
CREATE INDEX ix_action_item_created ON action_item(created_at DESC);

-- Tài khoản (kết nối OAuth); token ở secret. route_profile_id/ip_policy_json chừa cho VPN theo tài khoản.
CREATE TABLE pub_connections(
  id TEXT PRIMARY KEY,
  platform TEXT NOT NULL CHECK (platform IN ('YouTube','Facebook','Instagram','TikTok','Other')),
  display_name TEXT NOT NULL,
  home_url TEXT NOT NULL,
  route_profile_id TEXT,
  ip_policy_json TEXT,
  api_status TEXT NOT NULL DEFAULT 'NotConnected' CHECK (api_status IN ('NotConnected','Connected','NeedsReauth','Revoked')),
  provider TEXT,
  external_account_id TEXT,
  scopes TEXT,
  token_expires_at INTEGER,
  status_detail TEXT,
  max_posts_per_day INTEGER CHECK (max_posts_per_day IS NULL OR max_posts_per_day > 0),
  min_gap_minutes INTEGER CHECK (min_gap_minutes IS NULL OR min_gap_minutes >= 0),
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL);
CREATE INDEX ix_pub_connections_route ON pub_connections(route_profile_id);
-- Một tài khoản nền tảng chỉ gắn với một kết nối: tránh 2 bản ghi cùng giữ token của một kênh.
CREATE UNIQUE INDEX ux_pub_connections_external ON pub_connections(provider, external_account_id) WHERE external_account_id IS NOT NULL;

-- Kênh/trang đích của một kết nối.
CREATE TABLE pub_destinations(
  id TEXT PRIMARY KEY,
  connection_id TEXT NOT NULL REFERENCES pub_connections(id) ON DELETE CASCADE,
  platform TEXT NOT NULL,
  remote_id TEXT NOT NULL,
  title TEXT NOT NULL,
  url TEXT,
  uploads_playlist_id TEXT,
  attributes_json TEXT NOT NULL DEFAULT '{}',
  capabilities_json TEXT NOT NULL DEFAULT '[]',
  enabled INTEGER NOT NULL DEFAULT 1,
  synced_at INTEGER NOT NULL,
  removed_at INTEGER,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  UNIQUE (connection_id, remote_id));

-- Chiến dịch (tạo trước pub_publications vì bảng đó tham chiếu).
CREATE TABLE pub_campaigns(
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('Active','Paused','Stopped')),
  time_zone_id TEXT NOT NULL,
  weight REAL NOT NULL DEFAULT 1 CHECK (weight > 0),
  automation TEXT NOT NULL CHECK (automation IN ('ManualApproval','AutoWithinLimits')),
  content_max_age_days INTEGER CHECK (content_max_age_days IS NULL OR content_max_age_days > 0),
  post_budget INTEGER CHECK (post_budget IS NULL OR post_budget > 0),
  max_consecutive_failures INTEGER NOT NULL DEFAULT 3 CHECK (max_consecutive_failures > 0),
  title_template TEXT NOT NULL DEFAULT '{title}',
  description_template TEXT NOT NULL DEFAULT '{caption}',
  privacy TEXT NOT NULL DEFAULT 'Private' CHECK (privacy IN ('Private','Unlisted','Public')),
  evergreen INTEGER NOT NULL DEFAULT 0,
  evergreen_min_days INTEGER NOT NULL DEFAULT 30 CHECK (evergreen_min_days > 0),
  status_reason TEXT,
  -- Lỗi liên tiếp chỉ tính sau mốc này: tiếp tục chiến dịch không bị dừng lại ngay vì lỗi cũ.
  status_changed_at INTEGER NOT NULL,
  platform_settings_json TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL);

-- Bài đăng (một nội dung trên một đích). origin/origin_ref: nơi tạo (manual|auto_workflow|auto_reup|campaign).
-- job_id = job upload đang giữ bài (NULL = chờ điều phối tới dispatch_at); next_check_at = lần kiểm trạng thái kế tiếp sau upload.
CREATE TABLE pub_publications(
  id TEXT PRIMARY KEY,
  content_item_id TEXT NOT NULL REFERENCES pub_content_items(id) ON DELETE RESTRICT,
  destination_id TEXT NOT NULL REFERENCES pub_destinations(id) ON DELETE RESTRICT,
  title TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  tags_json TEXT NOT NULL DEFAULT '[]',
  privacy TEXT NOT NULL CHECK (privacy IN ('Private','Unlisted','Public')),
  made_for_kids INTEGER NOT NULL DEFAULT 0,
  contains_synthetic_media INTEGER NOT NULL DEFAULT 0,
  scheduled_at INTEGER,
  use_native_schedule INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL CHECK (status IN ('Draft','Validating','AwaitingApproval','Scheduled','Uploading','Processing',
    'Published','RetryScheduled','NeedsAction','Unknown','Failed','Cancelled','AwaitingUser')),
  idempotency_key TEXT NOT NULL UNIQUE,
  job_id TEXT,
  dispatch_at INTEGER,
  next_check_at INTEGER,
  upload_session_uri TEXT,
  upload_bytes_total INTEGER,
  upload_bytes_confirmed INTEGER NOT NULL DEFAULT 0,
  upload_started_at INTEGER,
  final_chunk_sent_at INTEGER,
  upload_completed_at INTEGER,
  remote_id TEXT,
  remote_url TEXT,
  last_error TEXT,
  blocked_reason TEXT,
  intentional_repost INTEGER NOT NULL DEFAULT 0,
  processing_polls INTEGER NOT NULL DEFAULT 0,
  published_at INTEGER,
  campaign_id TEXT REFERENCES pub_campaigns(id) ON DELETE SET NULL,
  -- "<campaign>:<destination>:<slot>" (chiến dịch) hoặc khoá chống trùng của S07/S08: chạy lại không tạo trùng bài.
  plan_key TEXT,
  late_resolved INTEGER NOT NULL DEFAULT 0,
  platform_settings_json TEXT,
  origin TEXT NOT NULL DEFAULT 'manual',
  origin_ref TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL);
CREATE INDEX ix_pub_publications_status_due ON pub_publications(status, scheduled_at);
CREATE INDEX ix_pub_publications_due ON pub_publications(scheduled_at);
CREATE INDEX ix_pub_publications_destination ON pub_publications(destination_id, scheduled_at);
CREATE INDEX ix_pub_publications_content ON pub_publications(content_item_id, destination_id);
CREATE INDEX ix_pub_publications_dispatch ON pub_publications(status, dispatch_at) WHERE job_id IS NULL;
CREATE INDEX ix_pub_publications_check ON pub_publications(next_check_at) WHERE next_check_at IS NOT NULL;
CREATE INDEX ix_pub_publications_origin ON pub_publications(origin, origin_ref);
-- Một video remote chỉ gắn với một publication trên cùng kênh (đối soát/nhập URL không gắn trùng).
CREATE UNIQUE INDEX ux_pub_publications_remote ON pub_publications(destination_id, remote_id) WHERE remote_id IS NOT NULL;
CREATE UNIQUE INDEX ux_pub_publications_plan_key ON pub_publications(plan_key) WHERE plan_key IS NOT NULL;
CREATE INDEX ix_pub_publications_campaign ON pub_publications(campaign_id, status);

-- Mỗi lần thử đăng; route_*/egress_* ghi tuyến mạng và IP thật đã dùng.
CREATE TABLE pub_publish_attempts(
  id TEXT PRIMARY KEY,
  publication_id TEXT NOT NULL REFERENCES pub_publications(id) ON DELETE CASCADE,
  job_id TEXT,
  started_at INTEGER NOT NULL,
  finished_at INTEGER,
  outcome TEXT,
  error TEXT,
  route_kind TEXT,
  route_ref TEXT,
  egress_ip TEXT,
  egress_country TEXT,
  ip_checked_at INTEGER);
CREATE INDEX ix_pub_attempts_publication ON pub_publish_attempts(publication_id, started_at);

CREATE TABLE pub_remote_posts(
  id TEXT PRIMARY KEY,
  publication_id TEXT REFERENCES pub_publications(id) ON DELETE SET NULL,
  destination_id TEXT NOT NULL REFERENCES pub_destinations(id) ON DELETE CASCADE,
  platform TEXT NOT NULL,
  remote_id TEXT NOT NULL,
  url TEXT,
  title TEXT,
  remote_status TEXT,
  discovered_by TEXT NOT NULL CHECK (discovered_by IN ('Upload','Reconcile','Manual')),
  last_synced_at INTEGER NOT NULL,
  created_at INTEGER NOT NULL,
  UNIQUE (destination_id, remote_id));
CREATE INDEX ix_pub_remote_posts_publication ON pub_remote_posts(publication_id);

-- Quota: một bộ đếm chung. project_key = hash client id OAuth (quota YouTube tính theo project); NULL = lời gọi cũ theo nền tảng.
ALTER TABLE quota_usage ADD COLUMN project_key TEXT;
CREATE INDEX ix_quota_project ON quota_usage(project_key, created_at) WHERE project_key IS NOT NULL;

CREATE TABLE pub_campaign_rules(
  id TEXT PRIMARY KEY,
  campaign_id TEXT NOT NULL REFERENCES pub_campaigns(id) ON DELETE CASCADE,
  position INTEGER NOT NULL,
  source_id TEXT,
  tags_json TEXT NOT NULL DEFAULT '[]',
  language TEXT,
  min_duration_sec INTEGER,
  max_duration_sec INTEGER,
  aspect TEXT NOT NULL DEFAULT 'Any' CHECK (aspect IN ('Any','Landscape','Portrait','Square')),
  destination_ids_json TEXT NOT NULL DEFAULT '[]');
CREATE INDEX ix_pub_campaign_rules_campaign ON pub_campaign_rules(campaign_id, position);

CREATE TABLE pub_schedule_policies(
  campaign_id TEXT PRIMARY KEY REFERENCES pub_campaigns(id) ON DELETE CASCADE,
  slots_json TEXT NOT NULL DEFAULT '[]',
  max_per_day INTEGER NOT NULL DEFAULT 3 CHECK (max_per_day > 0),
  min_gap_minutes INTEGER NOT NULL DEFAULT 120 CHECK (min_gap_minutes >= 0),
  forbidden_json TEXT NOT NULL DEFAULT '[]',
  jitter_minutes INTEGER NOT NULL DEFAULT 0 CHECK (jitter_minutes BETWEEN 0 AND 120),
  late_mode TEXT NOT NULL DEFAULT 'Skip' CHECK (late_mode IN ('Skip','Shift','CatchUp')),
  late_grace_minutes INTEGER NOT NULL DEFAULT 15 CHECK (late_grace_minutes >= 0),
  late_max_catch_up INTEGER NOT NULL DEFAULT 1 CHECK (late_max_catch_up >= 0),
  late_max_lateness_minutes INTEGER NOT NULL DEFAULT 360 CHECK (late_max_lateness_minutes >= 0),
  horizon_days INTEGER NOT NULL DEFAULT 7 CHECK (horizon_days BETWEEN 1 AND 28));

-- Deficit round-robin theo kênh: lưu DB để khởi động lại không reset tỉ lệ chia lượt giữa chiến dịch.
CREATE TABLE pub_campaign_fair_share(
  destination_id TEXT NOT NULL,
  campaign_id TEXT NOT NULL REFERENCES pub_campaigns(id) ON DELETE CASCADE,
  deficit REAL NOT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (destination_id, campaign_id));

-- dHash các khung (hex, phân tách bằng dấu phẩy).
CREATE TABLE pub_content_signatures(
  content_item_id TEXT PRIMARY KEY REFERENCES pub_content_items(id) ON DELETE CASCADE,
  frame_hashes TEXT NOT NULL,
  computed_at INTEGER NOT NULL);

-- Thống kê: metric luôn gắn nền tảng; dữ liệu chưa có không bao giờ thành 0 (value NULL <=> status khác Ok).
CREATE TABLE pub_metric_definitions(
  platform TEXT NOT NULL,
  key TEXT NOT NULL,
  scope TEXT NOT NULL CHECK (scope IN ('Post','Channel')),
  display_name TEXT NOT NULL,
  unit TEXT NOT NULL CHECK (unit IN ('count','ms','seconds','minutes')),
  description TEXT NOT NULL,
  source_endpoint TEXT NOT NULL,
  required_scope TEXT,
  aggregation TEXT NOT NULL DEFAULT 'Sum' CHECK (aggregation IN ('Sum','Mean','None')),
  verified INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (platform, key));

-- Snapshot chi tiết (giữ N ngày rồi gộp vào pub_daily_aggregates). publication_id NULL = metric cấp kênh.
CREATE TABLE pub_metric_snapshots(
  id TEXT PRIMARY KEY,
  platform TEXT NOT NULL,
  destination_id TEXT NOT NULL REFERENCES pub_destinations(id) ON DELETE CASCADE,
  publication_id TEXT REFERENCES pub_publications(id) ON DELETE CASCADE,
  remote_post_id TEXT,
  metric_key TEXT NOT NULL,
  value REAL,
  captured_at INTEGER NOT NULL,
  source TEXT NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('Ok','NotAvailable','MissingPermission','TokenExpired','NotYetAvailable')),
  detail TEXT,
  CHECK ((status = 'Ok') = (value IS NOT NULL)));
CREATE INDEX ix_pub_snapshots_publication ON pub_metric_snapshots(publication_id, metric_key, captured_at);
CREATE INDEX ix_pub_snapshots_channel ON pub_metric_snapshots(destination_id, metric_key, captured_at) WHERE publication_id IS NULL;
CREATE INDEX ix_pub_snapshots_captured ON pub_metric_snapshots(captured_at);

-- Giá trị cuối/nhỏ nhất/lớn nhất trong ngày UTC (day = yyyy-MM-dd) của snapshot Ok đã quá hạn giữ. publication_id '' = cấp kênh.
CREATE TABLE pub_daily_aggregates(
  day TEXT NOT NULL,
  destination_id TEXT NOT NULL REFERENCES pub_destinations(id) ON DELETE CASCADE,
  publication_id TEXT NOT NULL DEFAULT '',
  metric_key TEXT NOT NULL,
  platform TEXT NOT NULL,
  value_last REAL NOT NULL,
  value_min REAL NOT NULL,
  value_max REAL NOT NULL,
  samples INTEGER NOT NULL,
  last_captured_at INTEGER NOT NULL,
  source TEXT NOT NULL,
  PRIMARY KEY (day, destination_id, publication_id, metric_key));
CREATE INDEX ix_pub_daily_publication ON pub_daily_aggregates(publication_id, metric_key, day);
CREATE INDEX ix_pub_daily_destination ON pub_daily_aggregates(destination_id, metric_key, day);

CREATE TABLE pub_account_health_events(
  id TEXT PRIMARY KEY,
  connection_id TEXT NOT NULL REFERENCES pub_connections(id) ON DELETE CASCADE,
  destination_id TEXT REFERENCES pub_destinations(id) ON DELETE CASCADE,
  kind TEXT NOT NULL,
  day TEXT NOT NULL,
  detail TEXT NOT NULL,
  observed REAL,
  baseline REAL,
  z_score REAL,
  campaigns_paused INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL);
-- Một sự kiện mỗi (kênh, loại, ngày): quét lại nhiều lần không sinh trùng.
CREATE UNIQUE INDEX ux_pub_health_event_day ON pub_account_health_events(destination_id, kind, day);
CREATE INDEX ix_pub_health_created ON pub_account_health_events(created_at DESC);

-- Điều phối sản xuất: yêu cầu gửi tới pipeline nội bộ (IContentProducer).
CREATE TABLE pub_production_requests(
  id TEXT PRIMARY KEY,
  producer TEXT NOT NULL,
  campaign_id TEXT REFERENCES pub_campaigns(id) ON DELETE SET NULL,
  topic TEXT NOT NULL,
  language TEXT NOT NULL,
  aspect TEXT NOT NULL CHECK (aspect IN ('16:9','9:16')),
  target_duration_sec INTEGER CHECK (target_duration_sec IS NULL OR target_duration_sec > 0),
  notes TEXT,
  state TEXT NOT NULL CHECK (state IN ('Draft','Sent','Accepted','Rejected','Running','Completed','Failed','Expired','Cancelled')),
  progress INTEGER NOT NULL DEFAULT 0 CHECK (progress BETWEEN 0 AND 100),
  message TEXT,
  producer_job_id TEXT,
  content_ids_json TEXT NOT NULL DEFAULT '[]',
  auto_generated INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL,
  sent_at INTEGER,
  updated_at INTEGER NOT NULL,
  expires_at INTEGER);
CREATE INDEX ix_pub_production_requests_state ON pub_production_requests(state, producer);
CREATE INDEX ix_pub_production_requests_campaign ON pub_production_requests(campaign_id, created_at);
CREATE INDEX ix_pub_production_requests_created ON pub_production_requests(producer, created_at);

-- Lịch sử mọi status đọc được; (request, state, mốc cập nhật của producer) duy nhất nên quét lại không sinh trùng.
CREATE TABLE pub_producer_acknowledgements(
  id TEXT PRIMARY KEY,
  request_id TEXT NOT NULL REFERENCES pub_production_requests(id) ON DELETE CASCADE,
  state TEXT NOT NULL,
  progress INTEGER NOT NULL,
  message TEXT,
  producer_job_id TEXT,
  status_updated_at INTEGER NOT NULL,
  received_at INTEGER NOT NULL);
CREATE UNIQUE INDEX ux_pub_producer_ack ON pub_producer_acknowledgements(request_id, state, status_updated_at);

CREATE TABLE pub_producer_configs(
  producer TEXT PRIMARY KEY,
  exchange_dir TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 0,
  max_open_requests INTEGER NOT NULL DEFAULT 2 CHECK (max_open_requests >= 0),
  daily_request_budget INTEGER NOT NULL DEFAULT 10 CHECK (daily_request_budget >= 0),
  supported_aspects_json TEXT NOT NULL,
  ack_timeout_minutes INTEGER NOT NULL DEFAULT 30 CHECK (ack_timeout_minutes > 0),
  request_ttl_hours INTEGER NOT NULL DEFAULT 24 CHECK (request_ttl_hours > 0),
  updated_at INTEGER NOT NULL);

CREATE TABLE pub_campaign_production(
  campaign_id TEXT PRIMARY KEY REFERENCES pub_campaigns(id) ON DELETE CASCADE,
  enabled INTEGER NOT NULL DEFAULT 0,
  topics_json TEXT NOT NULL DEFAULT '[]',
  language TEXT,
  aspect TEXT CHECK (aspect IS NULL OR aspect IN ('16:9','9:16')),
  buffer_days REAL NOT NULL DEFAULT 3 CHECK (buffer_days > 0),
  target_days REAL NOT NULL DEFAULT 7 CHECK (target_days > 0),
  max_order INTEGER NOT NULL DEFAULT 5 CHECK (max_order > 0),
  preferred_producer TEXT,
  -- Đang bổ sung tồn: sau khi chạm điểm đặt hàng thì đặt tiếp từng yêu cầu cho tới mức mục tiêu.
  replenishing INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL);

INSERT INTO pub_metric_definitions(platform, key, scope, display_name, unit, description, source_endpoint, required_scope, aggregation, verified) VALUES
('YouTube','views','Post','Lượt xem','count','statistics.viewCount của YouTube Data API. Từ 24/08/2026 tính khi video bắt đầu phát (gồm autoplay, hover-preview, bấm phát). Không cùng phép đo với view của Facebook, Instagram, TikTok.','GET /youtube/v3/videos?part=statistics','https://www.googleapis.com/auth/youtube.readonly','Sum',1),
('YouTube','likes','Post','Lượt thích','count','statistics.likeCount: số người đã thích video (không có khi chủ video tắt hiển thị).','GET /youtube/v3/videos?part=statistics','https://www.googleapis.com/auth/youtube.readonly','Sum',1),
('YouTube','comments','Post','Bình luận','count','statistics.commentCount: số bình luận (không có khi tắt bình luận).','GET /youtube/v3/videos?part=statistics','https://www.googleapis.com/auth/youtube.readonly','Sum',1),
('YouTube','watch_time_minutes','Post','Thời gian xem (phút)','minutes','estimatedMinutesWatched của YouTube Analytics API, trọn đời bài, ước tính và có độ trễ vài ngày. Cần bật YouTube Analytics và kết nối lại.','GET youtubeanalytics.googleapis.com/v2/reports?filters=video==ID','https://www.googleapis.com/auth/yt-analytics.readonly','Sum',0),
('YouTube','avg_view_duration_sec','Post','Thời lượng xem trung bình (giây)','seconds','averageViewDuration của YouTube Analytics API, trọn đời bài.','GET youtubeanalytics.googleapis.com/v2/reports?filters=video==ID','https://www.googleapis.com/auth/yt-analytics.readonly','Mean',0),
('YouTube','shares','Post','Lượt chia sẻ','count','shares của YouTube Analytics API, trọn đời bài.','GET youtubeanalytics.googleapis.com/v2/reports?filters=video==ID','https://www.googleapis.com/auth/yt-analytics.readonly','Sum',0),
('YouTube','subscribers','Channel','Người đăng ký','count','statistics.subscriberCount của kênh, làm tròn xuống 3 chữ số có nghĩa; không có khi kênh ẩn số người đăng ký.','GET /youtube/v3/channels?part=statistics&mine=true','https://www.googleapis.com/auth/youtube.readonly','Sum',1),
('YouTube','channel_views','Channel','Tổng lượt xem kênh','count','statistics.viewCount của kênh (tổng các video).','GET /youtube/v3/channels?part=statistics&mine=true','https://www.googleapis.com/auth/youtube.readonly','Sum',1),
('YouTube','video_count','Channel','Số video công khai','count','statistics.videoCount: chỉ tính video công khai.','GET /youtube/v3/channels?part=statistics&mine=true','https://www.googleapis.com/auth/youtube.readonly','Sum',1),

('Facebook','plays','Post','Lượt phát Reels (không tính phát lại)','count','blue_reels_play_count: lượt phát có ít nhất 1 ms phát, không tính phát lại. Không cùng phép đo với view YouTube/TikTok/Instagram.','GET /{video-id}/video_insights?period=lifetime','read_insights','Sum',1),
('Facebook','plays_with_replays','Post','Lượt phát Reels (gồm phát lại)','count','fb_reels_total_plays: tổng lượt phát gồm cả phát lại.','GET /{video-id}/video_insights?period=lifetime','read_insights','Sum',1),
('Facebook','reach','Post','Người xem duy nhất (ước tính)','count','post_impressions_unique: ước tính số người xem duy nhất; không cộng được giữa các bài (trùng người xem).','GET /{video-id}/video_insights?period=lifetime','read_insights','None',1),
('Facebook','watch_time_ms','Post','Tổng thời gian phát','ms','post_video_view_time: tổng thời gian phát, gồm phát lại.','GET /{video-id}/video_insights?period=lifetime','read_insights','Sum',1),
('Facebook','avg_watch_time_ms','Post','Thời gian phát trung bình','ms','post_video_avg_time_watched: thời gian phát trung bình mỗi lượt, gồm phát lại.','GET /{video-id}/video_insights?period=lifetime','read_insights','Mean',1),
('Facebook','reactions','Post','Lượt cảm xúc','count','reactions.summary.total_count của video; chưa xác nhận hành vi trên Reels.','GET /{video-id}?fields=reactions.summary(total_count)','pages_read_engagement','Sum',0),
('Facebook','comments','Post','Bình luận','count','comments.summary.total_count của video; chưa xác nhận hành vi trên Reels.','GET /{video-id}?fields=comments.summary(total_count)','pages_read_engagement','Sum',0),
('Facebook','followers','Channel','Người theo dõi Page','count','followers_count của Page.','GET /{page-id}?fields=followers_count','pages_read_engagement','Sum',1),

('Instagram','views','Post','Lượt xem','count','views của Instagram media insights (Reels). Thay thế impressions/plays đã ngừng; khác phép đo view của nền tảng khác.','GET /{media-id}/insights?metric=views','instagram_manage_insights','Sum',1),
('Instagram','reach','Post','Tiếp cận (ước tính)','count','reach: ước tính số tài khoản duy nhất đã xem; không cộng được giữa các bài.','GET /{media-id}/insights?metric=reach','instagram_manage_insights','None',1),
('Instagram','likes','Post','Lượt thích','count','likes của media insights.','GET /{media-id}/insights?metric=likes','instagram_manage_insights','Sum',1),
('Instagram','comments','Post','Bình luận','count','comments của media insights.','GET /{media-id}/insights?metric=comments','instagram_manage_insights','Sum',1),
('Instagram','shares','Post','Lượt chia sẻ','count','shares của media insights.','GET /{media-id}/insights?metric=shares','instagram_manage_insights','Sum',1),
('Instagram','saved','Post','Lượt lưu','count','saved của media insights.','GET /{media-id}/insights?metric=saved','instagram_manage_insights','Sum',1),
('Instagram','total_interactions','Post','Tổng tương tác','count','total_interactions: tổng thích, bình luận, chia sẻ, lưu (theo định nghĩa của Instagram).','GET /{media-id}/insights?metric=total_interactions','instagram_manage_insights','Sum',1),
('Instagram','avg_watch_time_ms','Post','Thời gian xem trung bình','ms','ig_reels_avg_watch_time: thời gian xem trung bình của Reels.','GET /{media-id}/insights?metric=ig_reels_avg_watch_time','instagram_manage_insights','Mean',1),
('Instagram','followers','Channel','Người theo dõi','count','followers_count của IG User.','GET /{ig-user-id}?fields=followers_count','instagram_basic','Sum',0),

('TikTok','views','Post','Lượt xem','count','view_count của video (video/query). Khác phép đo view của nền tảng khác.','POST /v2/video/query/','video.list','Sum',1),
('TikTok','likes','Post','Lượt thích','count','like_count của video.','POST /v2/video/query/','video.list','Sum',1),
('TikTok','comments','Post','Bình luận','count','comment_count của video.','POST /v2/video/query/','video.list','Sum',1),
('TikTok','shares','Post','Lượt chia sẻ','count','share_count của video.','POST /v2/video/query/','video.list','Sum',1),
('TikTok','followers','Channel','Người theo dõi','count','follower_count của user/info.','GET /v2/user/info/?fields=follower_count','user.info.stats','Sum',1),
('TikTok','channel_likes','Channel','Tổng lượt thích kênh','count','likes_count của user/info: tổng lượt thích trên mọi video.','GET /v2/user/info/?fields=likes_count','user.info.stats','Sum',1),
('TikTok','video_count','Channel','Số video công khai','count','video_count của user/info.','GET /v2/user/info/?fields=video_count','user.info.stats','Sum',1);
