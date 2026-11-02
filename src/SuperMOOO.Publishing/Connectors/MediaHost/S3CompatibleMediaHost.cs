using System.Text.RegularExpressions;

namespace SuperMOOO.Publishing.Connectors.MediaHost;

/// <summary>
/// Cấu hình host S3-compatible. Ví dụ:
/// AWS S3: Endpoint https://s3.ap-southeast-1.amazonaws.com, Region ap-southeast-1, PathStyle=false (virtual-hosted);
/// Cloudflare R2: Endpoint https://&lt;account-id&gt;.r2.cloudflarestorage.com, Region "auto";
/// MinIO: Endpoint https://minio.example.com (phải HTTPS truy cập được từ Internet để Meta tải về), PathStyle=true.
/// Access key/secret do người dùng nhập, lưu DPAPI ở tầng ứng dụng; key nên chỉ có quyền PutObject/GetObject/DeleteObject trên bucket này.
/// </summary>
public sealed partial class S3MediaHostOptions
{
    public Uri? Endpoint { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public bool PathStyle { get; set; } = true;
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    /// <summary>Tiền tố key cho file tạm của SuperMOOO (đặt lifecycle rule xóa theo tiền tố này).</summary>
    public string KeyPrefix { get; set; } = "supermooo-tmp/";
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>null = hợp lệ.</summary>
    public string? Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri || Endpoint.Scheme != Uri.UriSchemeHttps)
            return "Endpoint của media host phải là URL HTTPS (nền tảng chỉ tải media qua HTTPS công khai).";
        if (Endpoint.AbsolutePath.Trim('/').Length > 0 || Endpoint.Query.Length > 0) return "Endpoint chỉ gồm scheme + host (+ cổng), không có đường dẫn.";
        if (!BucketPattern().IsMatch(Bucket)) return "Tên bucket không hợp lệ (3–63 ký tự: chữ thường, số, dấu chấm, gạch ngang).";
        if (string.IsNullOrWhiteSpace(Region) || Region.Any(char.IsWhiteSpace)) return "Region không hợp lệ (R2 dùng \"auto\").";
        if (AccessKeyId.Length is < 3 or > 128 || AccessKeyId.Any(char.IsWhiteSpace)) return "Access key không hợp lệ.";
        if (SecretAccessKey.Length is < 8 or > 256 || SecretAccessKey.Any(char.IsWhiteSpace)) return "Secret key không hợp lệ.";
        if (KeyPrefix.Length > 0 && !S3CompatibleMediaHost.IsSafeKey(KeyPrefix)) return "Tiền tố key chỉ gồm chữ, số, . _ - /.";
        return null;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketPattern();
}

/// <summary>
/// S3 / R2 / MinIO qua HttpClient + tự ký SigV4: PUT object (UNSIGNED-PAYLOAD, stream từ đĩa), presigned GET có hạn, DELETE.
/// Bucket để private: nền tảng đọc qua presigned URL. HttpClient do tầng ứng dụng cấp (proxy/giới hạn).
/// </summary>
public sealed partial class S3CompatibleMediaHost : IPublicMediaHost
{
    private const string Service = "s3";
    private readonly HttpClient _http;
    private readonly S3MediaHostOptions _options;
    private readonly TimeProvider _time;
    private readonly AwsSigV4.Credentials _credentials;

    public S3CompatibleMediaHost(HttpClient http, S3MediaHostOptions options, TimeProvider? time = null)
    {
        if (options.Validate() is { } error) throw new ArgumentException(error, nameof(options));
        _http = http;
        _options = options;
        _time = time ?? TimeProvider.System;
        _credentials = new AwsSigV4.Credentials(options.AccessKeyId, options.SecretAccessKey);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafeKeyPattern();

    public static bool IsSafeKey(string key) => SafeKeyPattern().IsMatch(key) && !key.Contains("..", StringComparison.Ordinal) && !key.StartsWith('/');

    public async Task<PublicMediaLease> PublishAsync(string objectKey, MediaFile media, string contentType, TimeSpan lifetime, CancellationToken ct)
    {
        var key = FullKey(objectKey);
        var content = new FileSliceContent(media.Path, 0, media.SizeBytes, contentType);
        await SendSignedAsync(HttpMethod.Put, key, content, AwsSigV4.UnsignedPayload, _options.UploadTimeout, "đưa video lên media host", ct);
        return CreateReadLease(objectKey, lifetime);
    }

    /// <summary>Presigned GET cho object đã có (không gọi mạng).</summary>
    public PublicMediaLease CreateReadLease(string objectKey, TimeSpan lifetime)
    {
        var now = _time.GetUtcNow();
        var (baseUri, path) = Locate(FullKey(objectKey));
        var url = AwsSigV4.Presign(_credentials, _options.Region, Service, now, "GET", baseUri, path, lifetime);
        return new PublicMediaLease(objectKey, url, now + lifetime);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        try
        {
            await SendSignedAsync(HttpMethod.Delete, FullKey(objectKey), null, AwsSigV4.EmptyPayloadHash, _options.ApiTimeout, "xóa video khỏi media host", ct);
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.NotFound) { }
    }

    private string FullKey(string objectKey)
    {
        var key = _options.KeyPrefix + objectKey;
        return IsSafeKey(key) ? key : throw new ArgumentException("Object key chỉ gồm chữ, số, . _ - /.", nameof(objectKey));
    }

    private (Uri BaseUri, string Path) Locate(string key)
    {
        var endpoint = _options.Endpoint!;
        return _options.PathStyle
            ? (new Uri($"{endpoint.Scheme}://{endpoint.Authority}"), $"/{_options.Bucket}/{key}")
            : (new Uri($"{endpoint.Scheme}://{_options.Bucket}.{endpoint.Authority}"), "/" + key);
    }

    private async Task SendSignedAsync(HttpMethod method, string key, HttpContent? content, string payloadHash, TimeSpan timeout, string action,
        CancellationToken ct)
    {
        var (baseUri, path) = Locate(key);
        var now = _time.GetUtcNow();
        var amzDate = AwsSigV4.AmzDate(now);
        var authorization = AwsSigV4.Authorization(_credentials, _options.Region, Service, now, method.Method, path, [],
            [("host", baseUri.Authority), ("x-amz-content-sha256", payloadHash), ("x-amz-date", amzDate)], payloadHash);
        var request = new HttpRequestMessage(method, new Uri(baseUri, AwsSigV4.CanonicalUri(path))) { Content = content };
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await PlatformHttp.SendOnceAsync(_http, request, timeout, "media host", ct);
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var code = Regex.Match(body, "<Code>([A-Za-z]{1,64})</Code>").Groups[1].Value;
        var status = (int)response.StatusCode;
        // Không đưa URL/khóa vào thông báo; chỉ mã lỗi S3.
        var message = $"Media host từ chối {action} (HTTP {status}{(code.Length > 0 ? ", " + code : "")}).";
        var kind = status switch
        {
            404 => ConnectorErrorKind.NotFound,
            401 or 403 => ConnectorErrorKind.Permission,
            429 => ConnectorErrorKind.RateLimited,
            503 when code == "SlowDown" => ConnectorErrorKind.RateLimited,
            >= 500 => ConnectorErrorKind.Transient,
            400 when code is "RequestTimeTooSkewed" => ConnectorErrorKind.Validation,
            _ => ConnectorErrorKind.BadResponse
        };
        throw new ConnectorException(kind, message, ConnectorHttp.RetryAfter(response), status, code.Length > 0 ? code : null);
    }

    /// <summary>Key ổn định cho một file của một kết nối: chạy lại sau crash ghi đè đúng object, không để rác.</summary>
    public static string StableKey(string connectionId, MediaFile media)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{connectionId}\n{media.Path}\n{media.SizeBytes}"));
        var ext = Path.GetExtension(media.Path).ToLowerInvariant();
        return Convert.ToHexStringLower(hash)[..32] + (IsSafeKey("x" + ext) ? ext : "");
    }
}
