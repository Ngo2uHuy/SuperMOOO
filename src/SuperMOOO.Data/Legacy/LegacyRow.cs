using System.Globalization;
using System.Text.RegularExpressions;

namespace SuperMOOO.Data.Legacy;

/// <summary>Một dòng của bảng cũ đọc linh hoạt (cột có thể thiếu hoặc sai kiểu vì schema cũ lỏng).</summary>
public sealed partial class LegacyRow(Dictionary<string, object?> values)
{
    [GeneratedRegex(@"^\s*(?<n>\d+(?:[.,]\d+)?)\s*(?<u>B|KB|MB|GB|TB)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    public object? Raw(string column) => values.GetValueOrDefault(column);

    public string? Str(string column) => Raw(column) switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s,
        var other => Convert.ToString(other, CultureInfo.InvariantCulture)
    };

    public long? Long(string column) => Raw(column) switch
    {
        long l => l,
        double d => (long)d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (long)d,
        _ => null
    };

    public double? Double(string column) => Raw(column) switch
    {
        long l => l,
        double d => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    public bool Flag(string column) => Long(column) is > 0;

    /// <summary>Thời gian cũ (TIMESTAMP text UTC, hoặc giây/ms số) → unix ms.</summary>
    public long? Ms(string column) => ParseMs(Raw(column));

    public static long? ParseMs(object? value) => value switch
    {
        long l => l > 100_000_000_000 ? l : l * 1000,
        double d => d > 100_000_000_000 ? (long)d : (long)(d * 1000),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => ParseMs(d),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) => t.ToUnixTimeMilliseconds(),
        _ => null
    };

    /// <summary>"12.34 MB" → số byte; không đọc được thì null.</summary>
    public static long? ParseSize(string? text)
    {
        if (text is null) return null;
        var m = SizePattern().Match(text);
        if (!m.Success) return long.TryParse(text, out var plain) ? plain : null;
        var n = double.Parse(m.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var power = Array.IndexOf(["B", "KB", "MB", "GB", "TB"], m.Groups["u"].Value.ToUpperInvariant());
        return (long)(n * Math.Pow(1024, power));
    }
}
