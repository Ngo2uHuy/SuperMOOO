namespace SuperMOOO.Publishing.Connectors.MediaHost;

/// <summary>
/// URL đọc công khai có hạn của một file đã đưa lên host. URL chứa chữ ký: không ghi log, không lưu DB.
/// </summary>
public sealed class PublicMediaLease(string objectKey, Uri url, DateTimeOffset expiresUtc)
{
    public string ObjectKey { get; } = objectKey;
    public Uri Url { get; } = url;
    public DateTimeOffset ExpiresUtc { get; } = expiresUtc;

    public override string ToString() => $"PublicMediaLease {{ ObjectKey = {ObjectKey}, ExpiresUtc = {ExpiresUtc:O} }}";
}

/// <summary>
/// Nơi đưa file lên để nền tảng tự tải về qua HTTPS công khai (Instagram video_url). Xóa ngay sau khi đăng xong/thất bại;
/// nên đặt thêm lifecycle rule xóa object sau 1–2 ngày phòng khi app tắt giữa chừng.
/// </summary>
public interface IPublicMediaHost
{
    /// <summary>Đưa file lên (ghi đè nếu key đã có) và trả URL đọc HTTPS có hạn.</summary>
    Task<PublicMediaLease> PublishAsync(string objectKey, MediaFile media, string contentType, TimeSpan lifetime, CancellationToken ct);

    /// <summary>Xóa object; không tồn tại coi như thành công.</summary>
    Task DeleteAsync(string objectKey, CancellationToken ct);
}
