namespace SuperMOOO.Core.Publishing;

/// <summary>
/// Trạng thái của một lần đọc metric. Chỉ <see cref="Ok"/> mới có giá trị; các trạng thái còn lại KHÔNG bao giờ được ghi thành 0.
/// </summary>
public enum MetricStatus
{
    Ok,
    /// <summary>Nền tảng không trả metric này (bài bị ẩn/xóa, tài khoản không đủ điều kiện, API trả rỗng sau thời gian chờ).</summary>
    NotAvailable,
    MissingPermission,
    TokenExpired,
    /// <summary>Nền tảng chưa tổng hợp xong (bài quá mới, Instagram trễ tới 48 giờ).</summary>
    NotYetAvailable
}

public enum MetricScope { Post, Channel }

/// <summary>Cách gộp metric của nhiều bài/kênh thành một số: không phải metric nào cũng cộng được (vd người xem duy nhất).</summary>
public enum MetricAggregation { Sum, Mean, None }

/// <summary>
/// Định nghĩa metric theo nền tảng. (Platform, Key) là khóa: "views" của YouTube, Instagram và TikTok là ba phép đo khác nhau
/// nên mỗi nền tảng có định nghĩa và mô tả ngữ nghĩa riêng; không bao giờ cộng gộp chéo nền tảng.
/// </summary>
public sealed class MetricDefinition
{
    public string Platform { get; init; } = "";
    public string Key { get; init; } = "";
    public MetricScope Scope { get; init; }
    public string DisplayName { get; init; } = "";
    /// <summary>count | ms | seconds | minutes.</summary>
    public string Unit { get; init; } = "count";
    public string Description { get; init; } = "";
    public string SourceEndpoint { get; init; } = "";
    /// <summary>Quyền cần có; null = đã đủ quyền cơ bản của kết nối.</summary>
    public string? RequiredScope { get; init; }
    public MetricAggregation Aggregation { get; init; } = MetricAggregation.Sum;
    /// <summary>Đã đối chiếu tên metric với tài liệu chính thức (chưa có nghĩa là đã chạy thử trên tài khoản thật).</summary>
    public bool Verified { get; init; }
}

public sealed class MetricSnapshot
{
    public string Id { get; init; } = "";
    public SocialPlatform Platform { get; init; }
    public string DestinationId { get; init; } = "";
    /// <summary>null = metric cấp kênh.</summary>
    public string? PublicationId { get; init; }
    public string? RemotePostId { get; init; }
    public string MetricKey { get; init; } = "";
    /// <summary>null khi <see cref="Status"/> khác Ok.</summary>
    public double? Value { get; init; }
    public DateTimeOffset CapturedUtc { get; init; }
    public string Source { get; init; } = "";
    public MetricStatus Status { get; init; }
    public string? Detail { get; init; }
}

public sealed class AccountHealthEvent
{
    public string Id { get; init; } = "";
    public string ConnectionId { get; init; } = "";
    public string? DestinationId { get; init; }
    public string Kind { get; init; } = "";
    public string Detail { get; init; } = "";
    public double? Observed { get; init; }
    public double? Baseline { get; init; }
    public double? ZScore { get; init; }
    public int CampaignsPaused { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}

public static class HealthEventKinds
{
    public const string PublishFailureRate = "PublishFailureRate";
    public const string ViewDrop = "ViewDrop";
}

/// <summary>Khóa metric dùng chung giữa connector, worker và UI.</summary>
public static class MetricKeys
{
    // Dùng chung tên nhưng ngữ nghĩa theo nền tảng (xem metric_definitions).
    public const string Views = "views";
    public const string Likes = "likes";
    public const string Comments = "comments";
    public const string Shares = "shares";
    public const string Reach = "reach";
    public const string Saved = "saved";
    public const string TotalInteractions = "total_interactions";
    public const string Followers = "followers";
    public const string VideoCount = "video_count";

    // YouTube
    public const string Subscribers = "subscribers";
    public const string ChannelViews = "channel_views";
    public const string WatchTimeMinutes = "watch_time_minutes";
    public const string AvgViewDurationSec = "avg_view_duration_sec";

    // Facebook Reels
    public const string Plays = "plays";
    public const string PlaysWithReplays = "plays_with_replays";
    public const string Reactions = "reactions";
    public const string WatchTimeMs = "watch_time_ms";
    public const string AvgWatchTimeMs = "avg_watch_time_ms";

    // TikTok
    public const string ChannelLikes = "channel_likes";

    /// <summary>Metric "lượt xem" chính của nền tảng, dùng cho giờ đăng tốt và phát hiện sụt view (mỗi nền tảng đo khác nhau, chỉ so trong cùng nền tảng).</summary>
    public static string PrimaryView(SocialPlatform platform) => platform == SocialPlatform.Facebook ? Plays : Views;
}
