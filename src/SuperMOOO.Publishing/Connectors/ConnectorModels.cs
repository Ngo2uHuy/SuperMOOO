using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors;

public enum ConnectorErrorKind
{
    /// <summary>401 sau khi đã làm mới token, hoặc refresh token bị từ chối (invalid_grant): cần kết nối lại.</summary>
    Auth,
    /// <summary>Hết quota ngày của project.</summary>
    QuotaExceeded,
    /// <summary>429 / rateLimitExceeded: thử lại sau, tôn trọng Retry-After.</summary>
    RateLimited,
    /// <summary>5xx hoặc lỗi kết nối.</summary>
    Transient,
    Timeout,
    /// <summary>Thiếu quyền (scope, kênh) — không retry mù.</summary>
    Permission,
    /// <summary>Chính sách/giới hạn tài khoản (vd uploadLimitExceeded) — không retry mù.</summary>
    Policy,
    /// <summary>Metadata/file bị từ chối (400) — retry không đổi kết quả.</summary>
    Validation,
    NotFound,
    BadResponse,
    /// <summary>Nền tảng yêu cầu người dùng tự xác minh (CAPTCHA/checkpoint/sinh trắc học); không tự retry.</summary>
    VerificationRequired
}

/// <summary>Thông báo không bao giờ chứa token, session URI hay URL có tham số bí mật.</summary>
public sealed class ConnectorException(
    ConnectorErrorKind kind, string message, TimeSpan? retryAfter = null, int? httpStatus = null, string? reason = null)
    : Exception(message)
{
    public ConnectorErrorKind Kind { get; } = kind;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public int? HttpStatus { get; } = httpStatus;
    /// <summary>Mã lý do của nền tảng (vd quotaExceeded, invalid_grant).</summary>
    public string? Reason { get; } = reason;

    public bool IsRetryable => Kind is ConnectorErrorKind.RateLimited or ConnectorErrorKind.Transient or ConnectorErrorKind.Timeout;
}

/// <summary>Nguồn access token của một kết nối; tầng ứng dụng lo lưu trữ, làm mới chủ động và đánh dấu NeedsReauth.</summary>
public interface IAccessTokenSource
{
    /// <summary>Token còn hạn (tự làm mới nếu sắp hết hạn).</summary>
    Task<string> GetAsync(CancellationToken ct);

    /// <summary>Bắt buộc làm mới (sau 401). Lỗi refresh → <see cref="ConnectorException"/> Auth.</summary>
    Task<string> RefreshAsync(CancellationToken ct);
}

/// <param name="Http">HttpClient của kết nối (proxy, rate limit, circuit breaker). Không có timeout mặc định: connector tự đặt theo từng request.</param>
public sealed record ConnectorSession(string ConnectionId, HttpClient Http, IAccessTokenSource Tokens);

/// <param name="PlatformSettingsJson">Lựa chọn riêng của nền tảng do người dùng nhập (vd form TikTok Direct Post); connector tự đọc phần của mình.</param>
public sealed record PublishVariant(
    string Title, string Description, IReadOnlyList<string> Tags, PublishPrivacy Privacy, bool MadeForKids,
    bool ContainsSyntheticMedia, DateTimeOffset? PublishAtUtc, long? DurationMs = null, string? PlatformSettingsJson = null);

public sealed record MediaFile(string Path, long SizeBytes, IReadOnlyList<MediaFile>? Images = null)
{
    public bool IsImagePost => Images is not null;

    public static MediaFile? FromAssets(IEnumerable<MediaAsset> assets)
    {
        static MediaFile FileOf(MediaAsset a) => new(a.StoredPath, File.Exists(a.StoredPath) ? new FileInfo(a.StoredPath).Length : a.SizeBytes);
        var images = assets.Where(a => a.Kind == MediaKind.Image).OrderBy(a => a.Id, StringComparer.Ordinal).Select(FileOf).ToList();
        if (images.Count > 0) return new MediaFile(images[0].Path, images.Sum(i => i.SizeBytes), images);
        return assets.FirstOrDefault(a => a.Kind == MediaKind.Video) is { } video ? FileOf(video) : null;
    }

    public static List<ValidationIssue> ValidateImages(MediaFile media, int maxCount, long maxBytes, params string[] extensions)
    {
        var issues = new List<ValidationIssue>();
        if (media.Images is not { Count: > 0 } images) return [new("IMAGES_MISSING", "Bài đăng chưa có ảnh.")];
        if (images.Count > maxCount) issues.Add(new("TOO_MANY_IMAGES", $"Nền tảng nhận tối đa {maxCount} ảnh trong một bài."));
        foreach (var image in images)
        {
            var name = System.IO.Path.GetFileName(image.Path);
            if (!extensions.Contains(System.IO.Path.GetExtension(image.Path), StringComparer.OrdinalIgnoreCase))
                issues.Add(new("IMAGE_FORMAT", $"{name}: chỉ nhận {string.Join(", ", extensions)}."));
            if (!File.Exists(image.Path)) issues.Add(new("FILE_MISSING", "Không tìm thấy ảnh: " + name));
            if (image.SizeBytes <= 0 || image.SizeBytes > maxBytes)
                issues.Add(new("IMAGE_SIZE", $"{name}: ảnh phải lớn hơn 0 và không quá {maxBytes / (1024 * 1024)} MB."));
        }
        return issues;
    }

    public string ImageMime => System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg"
    };
}

public sealed record ValidationIssue(string Code, string Message);

public sealed record DestinationInfo(
    string RemoteId, string Title, string? Url, string? UploadsPlaylistId, IReadOnlyDictionary<string, string> Attributes);

/// <param name="SessionUri">Session upload đã lưu (null = chưa khởi tạo).</param>
/// <param name="FinalChunkSent">Đã bắt đầu gửi chunk cuối ở lần chạy trước.</param>
public sealed record UploadState(string? SessionUri, long BytesConfirmed, bool FinalChunkSent);

/// <summary>Ghi bền các mốc upload. Lưu session URI phải xong trước khi gửi byte đầu tiên.</summary>
public interface IUploadCheckpoint
{
    /// <summary>Ngay trước khi tạo session (lời gọi insert tốn quota): tầng ứng dụng trừ quota, có thể ném để dời job.</summary>
    Task BeforeCreateSessionAsync(CancellationToken ct);
    Task SaveSessionAsync(string sessionUri, long totalBytes, CancellationToken ct);
    Task SaveProgressAsync(long bytesConfirmed, CancellationToken ct);
    Task MarkFinalChunkSendingAsync(CancellationToken ct);
    /// <summary>
    /// Ngay trước request đăng/commit cuối (sau khi media đã lên): kiểm lại IP/quốc gia của tuyến cả máy, vì VPN có thể rớt giữa lúc upload.
    /// Sai → ném lỗi dời bài; connector phải gọi trước <see cref="MarkFinalChunkSendingAsync"/> để bài không bị coi là đã gửi lệnh cuối.
    /// </summary>
    Task VerifyRouteAsync(CancellationToken ct);
    /// <summary>Session hết hạn khi chưa gửi chunk cuối: bỏ để tạo session mới (chưa có video nào được tạo).</summary>
    Task ClearSessionAsync(CancellationToken ct);
}

public abstract record UploadOutcome
{
    public sealed record Completed(string RemoteId) : UploadOutcome;
    /// <summary>Có thể máy chủ đã nhận đủ byte nhưng không có video id: phải đối soát, không upload lại.</summary>
    public sealed record Ambiguous(string Reason) : UploadOutcome;
}

public enum RemoteState { Processing, Ready, Failed, NotFound }

/// <param name="Detail">Ready: có thể là permalink (Instagram) hoặc "post_id=..." (TikTok) để tầng ứng dụng lưu URL thật.</param>
public sealed record RemoteStatus(RemoteState State, string? PrivacyStatus, string? Detail);

public static class RemotePrivacy
{
    /// <summary>PrivacyStatus khi video đã vào hộp thư nền tảng, chờ người dùng hoàn tất trong app (TikTok inbox).</summary>
    public const string AwaitingUser = "awaiting_user";
}

/// <param name="UploadStartedUtc">Thời điểm bắt đầu upload: cửa sổ khớp tính từ đây.</param>
/// <param name="ExcludeRemoteIds">Video đã gắn với publication khác (không được gắn trùng).</param>
public sealed record ReconcileQuery(
    string Title, DateTimeOffset UploadStartedUtc, DateTimeOffset NowUtc, long? SizeBytes, IReadOnlySet<string> ExcludeRemoteIds,
    bool IsImagePost = false);

public abstract record ReconcileResult
{
    public sealed record Found(string RemoteId) : ReconcileResult;
    public sealed record NotFound : ReconcileResult;
    public sealed record Ambiguous(IReadOnlyList<string> Candidates) : ReconcileResult;
}

public static class ConnectorHttp
{
    /// <summary>
    /// Request mang option này thì ResilienceHandler không tự mở breaker khi 401/403: connector còn làm mới token và thử lại 1 lần,
    /// tầng ứng dụng quyết định (NeedsReauth + breaker, hoặc quota không phải lỗi xác thực).
    /// </summary>
    public const string CallerHandlesAuthOptionName = "SuperMOOO.CallerHandlesAuth";
    public static readonly HttpRequestOptionsKey<bool> CallerHandlesAuth = new(CallerHandlesAuthOptionName);

    public static string Query(params (string Key, string Value)[] pairs) =>
        string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    public static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs) =>
        new(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));

    public static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }

    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date)
        {
            var wait = date - (response.Headers.Date ?? DateTimeOffset.UtcNow);
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    public static bool IsHttpsHost(string? url, string domain) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    public static bool IsServerError(HttpStatusCode code) => (int)code >= 500;

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);
}

/// <summary>Nội dung HTTP là một đoạn của file: stream từ đĩa, không nạp cả file vào bộ nhớ.</summary>
public sealed class FileSliceContent : HttpContent
{
    private readonly string _path;
    private readonly long _offset;
    private readonly long _length;

    public FileSliceContent(string path, long offset, long length, string? mediaType)
    {
        _path = path;
        _offset = offset;
        _length = length;
        if (mediaType is not null) Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        file.Seek(_offset, SeekOrigin.Begin);
        var target = new ThrottledStream(stream);
        var buffer = new byte[81920];
        var remaining = _length;
        while (remaining > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) throw new IOException("File video ngắn hơn kích thước đã khai báo (file bị thay đổi?).");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
