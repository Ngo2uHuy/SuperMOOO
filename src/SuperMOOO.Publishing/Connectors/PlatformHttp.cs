using System.Globalization;
using System.Text.Json.Nodes;

namespace SuperMOOO.Publishing.Connectors;

/// <summary>Phần HTTP dùng chung cho các connector nền tảng (Meta, TikTok).</summary>
public static class PlatformHttp
{
    /// <summary>Gửi 1 lần với timeout riêng; lỗi mạng/timeout → <see cref="ConnectorException"/> (không kèm URL: URL có thể chứa chữ ký).</summary>
    public static async Task<HttpResponseMessage> SendOnceAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout, string platform,
        CancellationToken ct)
    {
        using var _ = request;
        request.Options.Set(ConnectorHttp.CallerHandlesAuth, true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { return await http.SendAsync(request, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ConnectorErrorKind.Timeout, $"Hết thời gian chờ phản hồi từ {platform}.");
        }
        catch (HttpRequestException)
        {
            throw new ConnectorException(ConnectorErrorKind.Transient, $"Không kết nối được tới {platform}.");
        }
    }

    public static long? Long(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p) => p,
        _ => null
    };

    public static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>ISO 8601; Graph trả offset dạng "+0000" (không có dấu hai chấm) nên chuẩn hóa trước.</summary>
    public static DateTimeOffset? ParseTime(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length > 5 && s[^5] is '+' or '-' && s[^4..].All(char.IsAsciiDigit)) s = s[..^2] + ":" + s[^2..];
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t : null;
    }

    public static string Unix(DateTimeOffset t) => t.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    public static string Truncate(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    public static IReadOnlySet<string> ParseScopes(string? scopes) =>
        (scopes ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    public static async Task DelayAsync(TimeSpan delay, TimeProvider time, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay, time, ct);
    }
}

public static class PlatformText
{
    /// <summary>
    /// Caption = tiêu đề, mô tả, rồi hashtag từ thẻ. Tiêu đề luôn đứng đầu: đối soát chỉ biết tiêu đề nên khớp theo tiền tố caption.
    /// </summary>
    public static string Caption(PublishVariant v)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(v.Title)) parts.Add(v.Title.Trim());
        if (!string.IsNullOrWhiteSpace(v.Description)) parts.Add(v.Description.Trim());
        var text = string.Join("\n\n", parts);
        var tags = v.Tags.Select(t => "#" + string.Concat(t.Where(c => !char.IsWhiteSpace(c) && c != '#')))
            .Where(t => t.Length > 1 && !text.Contains(t, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tags.Count > 0) text = text.Length == 0 ? string.Join(' ', tags) : text + "\n\n" + string.Join(' ', tags);
        return text;
    }

    public static int HashtagCount(string caption) => caption.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Count(w => w.StartsWith('#') && w.Length > 1);

    public static bool MatchesTitle(string? caption, string title)
    {
        var t = title.Trim();
        return t.Length > 0 && caption is not null && caption.Trim().StartsWith(t, StringComparison.Ordinal);
    }
}
