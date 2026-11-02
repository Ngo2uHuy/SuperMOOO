namespace SuperMOOO.Core.Publishing;

public enum SourceKind { AIVideoStudio, VideoC, Folder }
public enum ImportMode { Manifest, VideoCMeta, LooseFiles }
public enum ContentStatus { Ready, Archived }
public enum MediaKind { Video, Thumbnail, Subtitle, Image }
public enum SocialPlatform { YouTube, Facebook, Instagram, TikTok, Other }
public enum ApiStatus { NotConnected, Connected, NeedsReauth, Revoked }

/// <summary>Trạng thái publication (PLAN mục 6). Nhánh ngoại lệ: RetryScheduled / NeedsAction / Unknown / Failed / Cancelled.</summary>
public enum PublicationStatus
{
    Draft, Validating, AwaitingApproval, Scheduled, Uploading, Processing, Published,
    RetryScheduled, NeedsAction, Unknown, Failed, Cancelled,
    /// <summary>Video đã vào hộp thư của nền tảng (TikTok inbox), chờ người dùng tự hoàn tất trong app; KHÔNG phải Published.</summary>
    AwaitingUser
}


public static class ActionKinds
{
    public const string ImportFailed = "ImportFailed";
    public const string SourceUnavailable = "SourceUnavailable";
    public const string VpnDown = "VpnDown";
    public const string CircuitOpen = "CircuitOpen";
    /// <summary>RefId = connection id: tuyến chính không đạt chính sách IP, có tuyến dự phòng chờ người dùng duyệt (failover = ask).</summary>
    public const string RouteFailoverAsk = "RouteFailoverAsk";
    /// <summary>RefId = connection id: token hỏng/bị thu hồi, cần kết nối lại.</summary>
    public const string ConnectionNeedsReauth = "ConnectionNeedsReauth";
    /// <summary>RefId = publication id.</summary>
    public const string PublishNeedsAction = "PublishNeedsAction";
    public const string PublishFailed = "PublishFailed";
    /// <summary>RefId = publication id: không chắc video đã lên hay chưa; người dùng quyết định, không tự upload lại.</summary>
    public const string PublishUnknown = "PublishUnknown";
    /// <summary>RefId = publication id: bài chiến dịch chờ duyệt (ManualApproval hoặc có cảnh báo rủi ro).</summary>
    public const string PublishAwaitingApproval = "PublishAwaitingApproval";
    /// <summary>RefId = campaign id: chiến dịch bị tự dừng (lỗi liên tiếp, mất quyền, ngắt mạch, quota/ngân sách).</summary>
    public const string CampaignPaused = "CampaignPaused";
    /// <summary>RefId = campaign id: planner không tạo được bài vì rủi ro mức Block hoặc nội dung không hợp lệ.</summary>
    public const string CampaignPlanBlocked = "CampaignPlanBlocked";
    /// <summary>RefId = connection id: token không làm mới được (Meta) hoặc refresh token (TikTok) sắp hết hạn; đăng nhập lại trước hạn.</summary>
    public const string ConnectionTokenExpiring = "ConnectionTokenExpiring";
    /// <summary>RefId = publication id: video đã vào hộp thư TikTok, người dùng phải mở app hoàn tất trước hạn.</summary>
    public const string PublishAwaitingUser = "PublishAwaitingUser";
    /// <summary>RefId = destination id: tỉ lệ lỗi đăng hoặc view/bài của kênh bất thường so với baseline (account_health_events).</summary>
    public const string AccountHealth = "AccountHealth";
    /// <summary>RefId = request id: hết hạn mà job sản xuất không còn.</summary>
    public const string ProductionRequestExpired = "ProductionRequestExpired";
    /// <summary>RefId = request id: producer từ chối hoặc báo lỗi (Rejected/Failed).</summary>
    public const string ProductionRequestFailed = "ProductionRequestFailed";
    /// <summary>RefId = campaign id: chiến dịch cần đặt sản xuất nhưng không có producer phù hợp/còn hạn mức.</summary>
    public const string ProductionBlocked = "ProductionBlocked";
    /// <summary>RefId = "disk": ổ dữ liệu sắp đầy; job nhập/upload tạm dừng tới khi còn đủ chỗ.</summary>
    public const string DiskLow = "DiskLow";
    /// <summary>RefId = content id: file media không còn trên đĩa (restore không kèm media, hoặc bị xóa ngoài app).</summary>
    public const string MediaMissing = "MediaMissing";
    /// <summary>RefId = connection id: IP ra ngoài khác quốc gia so với lần đăng thành công trước; bài dời tới khi người dùng chấp nhận quốc gia mới.</summary>
    public const string IpCountryChanged = "IpCountryChanged";
}

public static class PublishJobTypes
{
    /// <summary>Upload một publication (lane Publish, mỗi tài khoản 1 upload); payload ghi đăng tay hay theo lịch.</summary>
    public const string Upload = "publish.upload";
    /// <summary>Import do người dùng bấm: vẫn chạy khi automation tạm dừng.</summary>
    public const string ImportManual = "import.manual";
    /// <summary>Import do watcher/quét bù tạo: tạm dừng theo automation_paused.</summary>
    public const string ImportAuto = "import.auto";
    /// <summary>Đăng ngay do người dùng bấm: vẫn chạy khi automation tạm dừng.</summary>
    public const string PublishManual = "publish.manual";
    /// <summary>Đăng theo lịch: tạm dừng theo automation_paused.</summary>
    public const string PublishScheduled = "publish.scheduled";
    /// <summary>Tính chữ ký video (ffmpeg) sau khi nhập; chạy nền, tối đa 1 job một lúc.</summary>
    public const string MediaSignature = "media.signature";
    /// <summary>Đồng bộ thống kê bài/kênh của một đích; nhóm riêng, ưu tiên thấp hơn job đăng bài.</summary>
    public const string AnalyticsSync = "analytics.sync";
}

public static class SettingKeys
{
    public const string AutomationPaused = "automation_paused";
    /// <summary>+ connection id: quốc gia mới đang chờ người dùng chấp nhận / đã chấp nhận (cảnh báo IP đổi quốc gia so với lần đăng trước).</summary>
    public const string IpCountryPendingPrefix = "ip_country.pending:";
    public const string IpCountryAcceptedPrefix = "ip_country.accepted:";
    public const string DefaultNetworkProfileId = "default_network_profile_id";
    public const string ProxyTestUrl = "proxy_test_url";
    public const string DefaultProxyTestUrl = "https://api.ipify.org?format=json";
    /// <summary>Chi phí units của videos.insert. Tài liệu nguồn mâu thuẫn (100 vs 1600): mặc định 1600 cho an toàn, cần xác minh.</summary>
    public const string YouTubeInsertCost = "youtube_insert_cost";
    public const string YouTubeDailyQuota = "youtube_daily_quota";
    /// <summary>Kích thước chunk upload (KB), bội số 256.</summary>
    public const string YouTubeChunkSizeKb = "youtube_chunk_size_kb";
    /// <summary>Giới hạn bài/ngày và khoảng cách mặc định cho mọi tài khoản (rỗng = chỉ theo PlatformLimits).</summary>
    public const string PublishMaxPerDay = "publish_max_per_day";
    public const string PublishMinGapMinutes = "publish_min_gap_minutes";
    /// <summary>Đường dẫn ffmpeg.exe; rỗng = không bật kiểm tra gần trùng (không tự dò PATH để hành vi rõ ràng).</summary>
    public const string FfmpegPath = "ffmpeg_path";

    /// <summary>Người dùng xác nhận app TikTok đã qua audit Direct Post (mặc định tắt: Direct Post chỉ SELF_ONLY).</summary>
    public const string TikTokDirectPostAudited = "tiktok_direct_post_audited";
    /// <summary>Xin scope video.publish khi đăng nhập (chỉ bật khi app đã có sản phẩm Direct Post; xin scope chưa duyệt → lỗi trang đăng nhập).</summary>
    public const string TikTokRequestPublishScope = "tiktok_request_publish_scope";
    /// <summary>Loopback | WebView: cách nhận kết quả đăng nhập Meta.</summary>
    public const string MetaLoginFlow = "meta_login_flow";
    /// <summary>localhost | 127.0.0.1: host của redirect loopback cho Meta.</summary>
    public const string MetaLoopbackHost = "meta_loopback_host";
    /// <summary>Media host S3-compatible cho Instagram (khóa truy cập nằm trong ITokenVault).</summary>
    public const string MediaHostEndpoint = "media_host_endpoint";
    public const string MediaHostRegion = "media_host_region";
    public const string MediaHostBucket = "media_host_bucket";
    public const string MediaHostPathStyle = "media_host_path_style";
    public const string MediaHostPrefix = "media_host_prefix";
    public const string MediaHostUrlHours = "media_host_url_hours";

    /// <summary>Xin scope yt-analytics.readonly khi kết nối YouTube (watch time, chia sẻ); đổi cờ thì phải kết nối lại để có quyền.</summary>
    public const string YouTubeAnalyticsEnabled = "youtube_analytics_enabled";
    /// <summary>Xin video.list + user.info.stats khi kết nối TikTok (view/like của từng video, follower).</summary>
    public const string TikTokRequestStatsScopes = "tiktok_request_stats_scopes";
    /// <summary>Xin read_insights + instagram_manage_insights khi kết nối Meta.</summary>
    public const string MetaRequestInsightsScopes = "meta_request_insights_scopes";
    /// <summary>Số ngày giữ snapshot chi tiết; quá hạn thì gộp thành daily_aggregates.</summary>
    public const string AnalyticsRetentionDays = "analytics_snapshot_retention_days";
    /// <summary>Tự tạm dừng chiến dịch liên quan khi sức khỏe tài khoản bất thường (mặc định tắt: chỉ cảnh báo).</summary>
    public const string HealthAutoPauseCampaigns = "analytics_health_auto_pause";
    /// <summary>Ngân sách số bài đồng bộ thống kê mỗi ngày (UTC) cho một nền tảng: analytics_budget_{platform} (chữ thường).</summary>
    public const string AnalyticsBudgetPrefix = "analytics_budget_";

    /// <summary>Giới hạn job chạy đồng thời theo nhóm (publish = upload, analytics, import); rỗng = mặc định của handler.</summary>
    public const string ConcurrencyPublish = "concurrency_publish";
    public const string ConcurrencyAnalytics = "concurrency_analytics";
    public const string ConcurrencyImport = "concurrency_import";
    /// <summary>Băng thông upload tối đa (KB/s), 0 = không giới hạn.</summary>
    public const string UploadBandwidthKbps = "upload_bandwidth_kbps";
    /// <summary>Tổng dung lượng log tối đa (MB); vượt thì xóa file log cũ nhất.</summary>
    public const string LogMaxMb = "log_max_mb";
    /// <summary>Dung lượng trống tối thiểu (MB) của ổ dữ liệu; thấp hơn thì tạm dừng import/upload.</summary>
    public const string DiskMinFreeMb = "disk_min_free_mb";
    public const string BackupAutoEnabled = "backup_auto_enabled";
    public const string BackupIntervalHours = "backup_interval_hours";
    public const string BackupKeep = "backup_keep";
    public const string BackupIncludeMedia = "backup_include_media";
    /// <summary>Thư mục lưu backup; rỗng = backups trong thư mục dữ liệu.</summary>
    public const string BackupDirectory = "backup_directory";
    public const string LastAutoBackupUtc = "last_auto_backup_utc";
}
