namespace SuperMOOO.Core.Publishing;

/// <summary>Kênh/trang đích thuộc một kết nối (một kết nối có thể quản lý nhiều đích).</summary>
public sealed class SocialDestination
{
    public string Id { get; init; } = "";
    public string ConnectionId { get; init; } = "";
    public SocialPlatform Platform { get; init; }
    public string RemoteId { get; init; } = "";
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    /// <summary>YouTube: playlist "uploads" của kênh, dùng để đối soát.</summary>
    public string? UploadsPlaylistId { get; set; }
    /// <summary>Thuộc tính nền tảng (JSON object), vd longUploadsStatus của YouTube.</summary>
    public string AttributesJson { get; set; } = "{}";
    /// <summary>Snapshot capability lúc đồng bộ (JSON) để UI hiển thị và validate trước upload.</summary>
    public string CapabilitiesJson { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset SyncedUtc { get; set; }
    /// <summary>Kênh không còn trong danh sách nền tảng trả về lần đồng bộ gần nhất.</summary>
    public DateTimeOffset? RemovedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class Publication
{
    public string Id { get; init; } = "";
    public string ContentItemId { get; init; } = "";
    public string DestinationId { get; init; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string TagsJson { get; set; } = "[]";
    public PublishPrivacy Privacy { get; set; } = PublishPrivacy.Private;
    public bool MadeForKids { get; set; }
    /// <summary>Khai báo nội dung tạo/chỉnh sửa bằng AI (YouTube: status.containsSyntheticMedia).</summary>
    public bool ContainsSyntheticMedia { get; set; }
    /// <summary>null = đăng ngay khi đến lượt (hoặc nháp chưa hẹn giờ).</summary>
    public DateTimeOffset? ScheduledUtc { get; set; }
    /// <summary>Upload sớm ở private và để nền tảng tự công khai đúng giờ (YouTube status.publishAt).</summary>
    public bool UseNativeSchedule { get; set; }
    public PublicationStatus Status { get; set; } = PublicationStatus.Draft;
    /// <summary>Định danh ổn định của lần đăng; không đổi qua các lần retry.</summary>
    public string IdempotencyKey { get; init; } = "";
    public string? JobId { get; set; }
    public string? UploadSessionUri { get; set; }
    public long? UploadBytesTotal { get; set; }
    public long UploadBytesConfirmed { get; set; }
    public DateTimeOffset? UploadStartedUtc { get; set; }
    /// <summary>Đã bắt đầu gửi chunk cuối: từ đây mất kết quả phải đối soát, không được upload lại.</summary>
    public DateTimeOffset? FinalChunkSentUtc { get; set; }
    /// <summary>Lúc nhận remote id sau upload: mốc tính hạn hoàn tất trong hộp thư (TikTok inbox 24h).</summary>
    public DateTimeOffset? UploadCompletedUtc { get; set; }
    /// <summary>Lựa chọn riêng của nền tảng (JSON), vd chế độ TikTok Inbox/Direct Post + form Direct Post.</summary>
    public string? PlatformSettingsJson { get; set; }
    public string? RemoteId { get; set; }
    public string? RemoteUrl { get; set; }
    public string? LastError { get; set; }
    /// <summary>"Auth" = bị chặn do kết nối hỏng; kết nối lại thành công thì tự xếp lại.</summary>
    public string? BlockedReason { get; set; }
    public bool IntentionalRepost { get; set; }
    public int ProcessingPolls { get; set; }
    public DateTimeOffset? PublishedUtc { get; set; }
    /// <summary>Chiến dịch đã tạo bài (null = bài tạo tay).</summary>
    public string? CampaignId { get; set; }
    /// <summary>"campaign:destination:slotUtc" duy nhất: planner chạy lại không tạo trùng.</summary>
    public string? PlanKey { get; set; }
    /// <summary>Đã xét chính sách trễ lịch cho lần hẹn hiện tại (được đăng bù).</summary>
    public bool LateResolved { get; set; }
    /// <summary>Nơi tạo bài: manual | auto_workflow | auto_reup | campaign (xem <see cref="PublicationOrigins"/>).</summary>
    public string Origin { get; set; } = PublicationOrigins.Manual;
    /// <summary>Định danh phía nguồn tạo (vd id workflow S07/S08, id chiến dịch).</summary>
    public string? OriginRef { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public const string BlockedByAuth = "Auth";
    public const string BlockedByVerification = "Verification";
}

public sealed class PublishAttempt
{
    /// <summary>Outcome của lần thử đã đưa được video lên nền tảng.</summary>
    public static readonly IReadOnlyList<string> SucceededOutcomes = ["Succeeded", "Uploaded", "Reconciled", "Published", "Processing", "AwaitingUser"];

    public string Id { get; init; } = "";
    public string PublicationId { get; init; } = "";
    public string? JobId { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }
    /// <summary>Succeeded | Retry | Deferred | Failed | NeedsAction | Unknown | Cancelled.</summary>
    public string? Outcome { get; init; }
    public string? Error { get; init; }
    /// <summary>Tuyến mạng đã dùng: direct | proxy | vpn (null = chưa ghi).</summary>
    public string? RouteKind { get; init; }
    public string? RouteRef { get; init; }
    public string? EgressIp { get; init; }
    public string? EgressCountry { get; init; }
    public DateTimeOffset? IpCheckedUtc { get; init; }
}

public sealed record ConnectionEgress(string ConnectionId, string EgressIp, string? EgressCountry);

public static class PublicationOrigins
{
    public const string Manual = "manual";
    public const string AutoWorkflow = "auto_workflow";
    public const string AutoReup = "auto_reup";
    public const string Campaign = "campaign";
}

public sealed class RemotePost
{
    public string Id { get; init; } = "";
    public string? PublicationId { get; init; }
    public string DestinationId { get; init; } = "";
    public SocialPlatform Platform { get; init; }
    public string RemoteId { get; init; } = "";
    public string? Url { get; init; }
    public string? Title { get; init; }
    public string? RemoteStatus { get; init; }
    /// <summary>Upload | Reconcile | Manual.</summary>
    public string DiscoveredBy { get; init; } = "";
    public DateTimeOffset LastSyncedUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}

public static class PublicationRules
{
    private static readonly Dictionary<PublicationStatus, PublicationStatus[]> Allowed = new()
    {
        [PublicationStatus.Draft] = [PublicationStatus.Validating, PublicationStatus.Cancelled],
        [PublicationStatus.Validating] = [PublicationStatus.Scheduled, PublicationStatus.AwaitingApproval, PublicationStatus.Draft,
            PublicationStatus.Failed, PublicationStatus.Cancelled],
        [PublicationStatus.AwaitingApproval] = [PublicationStatus.Scheduled, PublicationStatus.Draft, PublicationStatus.Cancelled],
        [PublicationStatus.Scheduled] = [PublicationStatus.Uploading, PublicationStatus.RetryScheduled, PublicationStatus.NeedsAction,
            PublicationStatus.Failed, PublicationStatus.Cancelled],
        [PublicationStatus.Uploading] = [PublicationStatus.Processing, PublicationStatus.RetryScheduled, PublicationStatus.NeedsAction,
            PublicationStatus.Unknown, PublicationStatus.Failed, PublicationStatus.Cancelled],
        [PublicationStatus.Processing] = [PublicationStatus.Published, PublicationStatus.AwaitingUser, PublicationStatus.NeedsAction,
            PublicationStatus.Unknown, PublicationStatus.Failed],
        [PublicationStatus.AwaitingUser] = [PublicationStatus.Published, PublicationStatus.Processing, PublicationStatus.NeedsAction,
            PublicationStatus.Unknown, PublicationStatus.Failed],
        [PublicationStatus.RetryScheduled] = [PublicationStatus.Uploading, PublicationStatus.Processing, PublicationStatus.NeedsAction,
            PublicationStatus.Unknown, PublicationStatus.Failed, PublicationStatus.Cancelled],
        [PublicationStatus.NeedsAction] = [PublicationStatus.Scheduled, PublicationStatus.RetryScheduled, PublicationStatus.Processing,
            PublicationStatus.Cancelled],
        [PublicationStatus.Unknown] = [PublicationStatus.Processing, PublicationStatus.RetryScheduled, PublicationStatus.Cancelled],
        [PublicationStatus.Failed] = [PublicationStatus.RetryScheduled, PublicationStatus.Cancelled],
        [PublicationStatus.Published] = [],
        [PublicationStatus.Cancelled] = [],
    };

    public static bool CanTransition(PublicationStatus from, PublicationStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>Các trạng thái có thể đi tới <paramref name="to"/>; dùng làm điều kiện WHERE khi cập nhật để không ghi đè chuyển trạng thái song song.</summary>
    public static IReadOnlyList<PublicationStatus> SourcesOf(PublicationStatus to) =>
        Allowed.Where(kv => kv.Value.Contains(to)).Select(kv => kv.Key).ToList();

    /// <summary>Video có thể đã/đang nằm trên nền tảng: hủy local không còn ý nghĩa "chưa đăng".</summary>
    public static bool MayExistRemotely(Publication p) => p.RemoteId is not null || p.FinalChunkSentUtc is not null;

    /// <summary>Chỉ đổi giờ được khi chưa bắt đầu upload (chưa có session/remote): từ Uploading trở đi thì không.</summary>
    public static bool CanReschedule(Publication p) =>
        p.Status is PublicationStatus.AwaitingApproval or PublicationStatus.Scheduled
        && p.UploadSessionUri is null && p.RemoteId is null && p.FinalChunkSentUtc is null;

    /// <summary>Publication còn chiếm chỗ trên kênh (dùng cho cảnh báo trùng và giới hạn bài/ngày).</summary>
    public static bool IsActive(PublicationStatus s) => s is not (PublicationStatus.Cancelled or PublicationStatus.Failed or PublicationStatus.Draft);
}

/// <summary>Quota YouTube Data API reset lúc nửa đêm giờ Pacific (có DST).</summary>
public static class PacificTime
{
    private static readonly Lazy<TimeZoneInfo> Zone = new(() =>
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
    });

    public static TimeZoneInfo TimeZone => Zone.Value;

    public static DateOnly Day(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Zone.Value).DateTime);

    /// <summary>Đầu ngày Pacific chứa <paramref name="utc"/>, quy về UTC.</summary>
    public static DateTimeOffset DayStart(DateTimeOffset utc)
    {
        var start = Day(utc).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(start, Zone.Value.GetUtcOffset(start)).ToUniversalTime();
    }

    public static DateTimeOffset NextMidnight(DateTimeOffset utc)
    {
        var next = Day(utc).AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(next, Zone.Value.GetUtcOffset(next)).ToUniversalTime();
    }
}
