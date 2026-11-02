using System.Globalization;
using System.Text.Json.Nodes;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors;

/// <param name="Value">Có giá trị khi và chỉ khi <paramref name="Status"/> là Ok: dữ liệu chưa có không bao giờ thành 0.</param>
/// <param name="Source">Endpoint đã trả giá trị (hiển thị cho người dùng, không chứa token).</param>
public sealed record MetricValue(string Key, double? Value, MetricStatus Status, string Source, string? Detail = null)
{
    public static MetricValue Ok(string key, double value, string source) => new(key, value, MetricStatus.Ok, source);

    public static MetricValue Unavailable(string key, MetricStatus status, string source, string? detail = null) =>
        new(key, null, status, source, detail);
}

/// <param name="RemoteId">Id nền tảng dùng để đọc metric (YouTube/Facebook/Instagram: remote id; TikTok: video id, không phải publish_id).</param>
public sealed record PostMetricTarget(string PublicationId, string RemoteId, DateTimeOffset PublishedUtc);

/// <param name="GrantedScopes">Scope đã cấp cho kết nối.</param>
/// <param name="AnalyticsEnabled">Người dùng đã bật YouTube Analytics: thiếu scope thì báo MissingPermission thay vì bỏ qua.</param>
public sealed record MetricContext(string? GrantedScopes, bool AnalyticsEnabled = false);

/// <summary>
/// Khả năng đọc thống kê, tách khỏi <see cref="ISocialConnector"/> để không phá chữ ký cũ.
/// Thiếu quyền → <see cref="MetricStatus.MissingPermission"/>, chưa có số → NotYetAvailable/NotAvailable; KHÔNG bao giờ trả 0 thay cho "chưa có".
/// Lỗi xác thực/hạn mức/mạng vẫn ném <see cref="ConnectorException"/> để tầng ứng dụng quyết định (TokenExpired, dời job).
/// </summary>
public interface IMetricsConnector
{
    /// <returns>Theo publication id: mỗi metric một <see cref="MetricValue"/>.</returns>
    Task<IReadOnlyDictionary<string, IReadOnlyList<MetricValue>>> ReadPostMetricsAsync(
        ConnectorSession session, DestinationInfo destination, IReadOnlyList<PostMetricTarget> posts, MetricContext context,
        DateTimeOffset nowUtc, CancellationToken ct);

    Task<IReadOnlyList<MetricValue>> ReadChannelMetricsAsync(
        ConnectorSession session, DestinationInfo destination, MetricContext context, DateTimeOffset nowUtc, CancellationToken ct);
}

public static class MetricParsing
{
    public static long? Long(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) && d >= 0 && d == Math.Floor(d) => (long)d,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p) => p,
        _ => null
    };

    public static double? Number(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => null
    };

    public static bool HasScope(string? granted, string scope) =>
        (granted ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);

    /// <summary>Chưa có số trên bài còn mới thì "chưa sẵn sàng" (nền tảng tổng hợp trễ); bài cũ mà vẫn thiếu thì "không có".</summary>
    public static MetricStatus MissingStatus(DateTimeOffset publishedUtc, DateTimeOffset nowUtc, TimeSpan grace) =>
        nowUtc - publishedUtc < grace ? MetricStatus.NotYetAvailable : MetricStatus.NotAvailable;

    public static IEnumerable<MetricValue> AllUnavailable(IEnumerable<string> keys, MetricStatus status, string source, string? detail) =>
        keys.Select(k => MetricValue.Unavailable(k, status, source, detail));

    public static IEnumerable<T[]> Chunks<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size) yield return [.. items.Skip(i).Take(size)];
    }
}
