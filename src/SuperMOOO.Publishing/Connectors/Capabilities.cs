namespace SuperMOOO.Publishing.Connectors;

public enum Capability
{
    UploadVideo,
    ResumableUpload,
    ReadPublishStatus,
    ScheduleNative,
    UpdateChannelInfo,
    CreateChannel,
    ReadAnalytics,
    ReconcilePost,
    UploadImages
}

public enum CapabilityStatus { Supported, MissingPermission, RequiresManualStep, NotSupported }

public sealed record CapabilityInfo(Capability Capability, CapabilityStatus Status, string? Note = null);

/// <summary>
/// Connector nền tảng: chỉ nói chuyện HTTP với nền tảng qua <see cref="ConnectorSession"/>; không biết DB/job.
/// Trạng thái bền (session upload, byte đã xác nhận) đi qua <see cref="IUploadCheckpoint"/> do tầng ứng dụng cung cấp.
/// Lỗi nền tảng ném <see cref="ConnectorException"/>; ngoại lệ chặn của pipeline mạng (rate limit/breaker) đi xuyên qua nguyên vẹn.
/// </summary>
public interface ISocialConnector
{
    string Platform { get; }

    /// <param name="grantedScopes">Scope đã cấp (null = chưa kết nối API).</param>
    IReadOnlyList<CapabilityInfo> GetCapabilities(string? grantedScopes);

    Task<IReadOnlyList<CapabilityInfo>> GetCapabilitiesAsync(string connectionId, CancellationToken ct) =>
        Task.FromResult(GetCapabilities(null));

    /// <summary>Kiểm tra trước upload (định dạng, kích thước, giới hạn metadata). Rỗng = hợp lệ.</summary>
    IReadOnlyList<ValidationIssue> Validate(PublishVariant variant, MediaFile media, DestinationInfo? destination);

    Task<IReadOnlyList<DestinationInfo>> ListDestinationsAsync(ConnectorSession session, CancellationToken ct);

    Task<UploadOutcome> UploadAsync(ConnectorSession session, PublishVariant variant, MediaFile media, UploadState state,
        IUploadCheckpoint checkpoint, CancellationToken ct);

    Task<RemoteStatus> GetStatusAsync(ConnectorSession session, string remoteId, CancellationToken ct);

    /// <param name="uploadCompletedUtc">Lúc upload xong (tầng ứng dụng lưu): nền tảng có hạn hoàn tất (TikTok inbox 24h) tính từ đây.</param>
    Task<RemoteStatus> GetStatusAsync(ConnectorSession session, string remoteId, DateTimeOffset? uploadCompletedUtc, CancellationToken ct) =>
        GetStatusAsync(session, remoteId, ct);

    /// <summary>Tìm video vừa upload khi mất kết quả (timeout/crash sau byte cuối). Không chắc chắn thì không đoán.</summary>
    Task<ReconcileResult> ReconcileAsync(ConnectorSession session, DestinationInfo destination, ReconcileQuery query, CancellationToken ct);

    /// <summary>Trích remote id từ URL người dùng dán (vd "Đã đăng (nhập URL)"); null nếu không hợp lệ.</summary>
    string? ParseRemoteId(string url);

    string RemoteUrl(string remoteId);
}
