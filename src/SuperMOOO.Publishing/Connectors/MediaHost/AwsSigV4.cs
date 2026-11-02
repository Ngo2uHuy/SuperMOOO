using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SuperMOOO.Publishing.Connectors.MediaHost;

/// <summary>
/// AWS Signature Version 4 cho S3 (header Authorization và presigned URL), tự cài bằng HMAC-SHA256 — không dùng AWS SDK.
/// Đường dẫn S3 chỉ mã hóa URI một lần (khác các dịch vụ AWS khác).
/// https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-header-based-auth.html · .../sigv4-query-string-auth.html
/// </summary>
public static class AwsSigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    /// <summary>Hạn tối đa của presigned URL SigV4 (7 ngày).</summary>
    public static readonly TimeSpan MaxPresignLifetime = TimeSpan.FromDays(7);

    public sealed record Credentials(string AccessKeyId, string SecretAccessKey)
    {
        public override string ToString() => $"Credentials {{ AccessKeyId = {AccessKeyId} }}";
    }

    public static string AmzDate(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    public static string ShortDate(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    public static string Scope(DateTimeOffset t, string region, string service) => $"{ShortDate(t)}/{region}/{service}/aws4_request";

    /// <summary>Mã hóa RFC 3986: giữ A-Z a-z 0-9 - . _ ~ , còn lại %XX (hoa).</summary>
    public static string UriEncode(string value, bool keepSlash = false)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' || (keepSlash && c == '/')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Đường dẫn thô (chưa mã hóa) → canonical URI; rỗng → "/".</summary>
    public static string CanonicalUri(string rawPath) => rawPath.Length == 0 ? "/" : UriEncode(rawPath.StartsWith('/') ? rawPath : "/" + rawPath, keepSlash: true);

    public static string CanonicalQuery(IEnumerable<(string Key, string Value)> query) =>
        string.Join('&', query.Select(p => (Key: UriEncode(p.Key), Value: UriEncode(p.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value));

    /// <returns>Canonical request và danh sách signed headers (tên thường, sắp xếp).</returns>
    public static (string CanonicalRequest, string SignedHeaders) CanonicalRequest(string method, string canonicalUri, string canonicalQuery,
        IEnumerable<(string Name, string Value)> headers, string payloadHash)
    {
        var normalized = headers
            .GroupBy(h => h.Name.Trim().ToLowerInvariant())
            .Select(g => (Name: g.Key, Value: string.Join(',', g.Select(h => CollapseSpaces(h.Value.Trim())))))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();
        var signed = string.Join(';', normalized.Select(h => h.Name));
        var canonicalHeaders = string.Concat(normalized.Select(h => h.Name + ":" + h.Value + "\n"));
        return ($"{method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signed}\n{payloadHash}", signed);
    }

    public static string StringToSign(DateTimeOffset t, string scope, string canonicalRequest) =>
        $"{Algorithm}\n{AmzDate(t)}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

    public static byte[] SigningKey(string secret, DateTimeOffset t, string region, string service)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secret), Encoding.UTF8.GetBytes(ShortDate(t)));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, "aws4_request"u8.ToArray());
    }

    public static string Signature(string secret, DateTimeOffset t, string region, string service, string stringToSign) =>
        Hex(HMACSHA256.HashData(SigningKey(secret, t, region, service), Encoding.UTF8.GetBytes(stringToSign)));

    /// <summary>Giá trị header Authorization. <paramref name="headers"/> phải gồm host, x-amz-date, x-amz-content-sha256 (S3).</summary>
    public static string Authorization(Credentials credentials, string region, string service, DateTimeOffset t, string method, string rawPath,
        IEnumerable<(string Key, string Value)> query, IEnumerable<(string Name, string Value)> headers, string payloadHash)
    {
        var (canonical, signed) = CanonicalRequest(method, CanonicalUri(rawPath), CanonicalQuery(query), headers, payloadHash);
        var scope = Scope(t, region, service);
        var signature = Signature(credentials.SecretAccessKey, t, region, service, StringToSign(t, scope, canonical));
        return $"{Algorithm} Credential={credentials.AccessKeyId}/{scope},SignedHeaders={signed},Signature={signature}";
    }

    /// <summary>Presigned URL (chỉ ký header host, payload UNSIGNED-PAYLOAD). <paramref name="baseUri"/> = scheme + host[:port].</summary>
    public static Uri Presign(Credentials credentials, string region, string service, DateTimeOffset t, string method, Uri baseUri, string rawPath,
        TimeSpan expires)
    {
        if (expires <= TimeSpan.Zero || expires > MaxPresignLifetime)
            throw new ArgumentOutOfRangeException(nameof(expires), "Hạn presigned URL phải trong (0, 7 ngày].");
        var scope = Scope(t, region, service);
        var query = new List<(string, string)>
        {
            ("X-Amz-Algorithm", Algorithm), ("X-Amz-Credential", $"{credentials.AccessKeyId}/{scope}"), ("X-Amz-Date", AmzDate(t)),
            ("X-Amz-Expires", ((long)expires.TotalSeconds).ToString(CultureInfo.InvariantCulture)), ("X-Amz-SignedHeaders", "host")
        };
        var canonicalUri = CanonicalUri(rawPath);
        var canonicalQuery = CanonicalQuery(query);
        var (canonical, _) = CanonicalRequest(method, canonicalUri, canonicalQuery, [("host", baseUri.Authority)], UnsignedPayload);
        var signature = Signature(credentials.SecretAccessKey, t, region, service, StringToSign(t, scope, canonical));
        return new Uri($"{baseUri.Scheme}://{baseUri.Authority}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}");
    }

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (c == ' ') { if (!space) sb.Append(c); space = true; }
            else { sb.Append(c); space = false; }
        }
        return sb.ToString();
    }
}
