namespace SuperMOOO.Core.Publishing;

public interface IClock { DateTimeOffset UtcNow { get; } }

[Service(As = [typeof(IClock)])]
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public static class PathRules
{
    /// <summary>Chuẩn hóa đường dẫn tuyệt đối, bỏ dấu phân cách cuối (giữ nguyên chữ hoa/thường).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>Khóa so sánh: Windows không phân biệt hoa/thường.</summary>
    public static string Key(string path) => Normalize(path).ToLowerInvariant();

    /// <summary>Giải đường dẫn tương đối trong <paramref name="root"/>; chặn rooted, "..", và thoát khỏi root sau khi resolve.</summary>
    public static bool TryResolveInside(string root, string? relative, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\0') >= 0) return false;
        if (Path.IsPathRooted(relative)) return false;
        var segments = relative.Split('/', Path.DirectorySeparatorChar);
        if (segments.Any(s => s == "..")) return false;

        var rootFull = Normalize(root);
        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(rootFull, relative)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        fullPath = candidate;
        return true;
    }
}

public static class RetryPolicy
{
    /// <summary>
    /// Exponential backoff + full jitter: delay trải đều trong [0, 2·exp) (trung bình = exp, jitter01=0.5 cho đúng exp), có sàn 1s và trần cap,
    /// để các job lỗi cùng lúc không retry đồng loạt.
    /// </summary>
    public static TimeSpan Delay(int attempt, double jitter01, TimeSpan? baseDelay = null, TimeSpan? cap = null)
    {
        var b = baseDelay ?? TimeSpan.FromSeconds(5);
        var c = cap ?? TimeSpan.FromMinutes(5);
        var exp = Math.Min(c.TotalSeconds, b.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        var jittered = 2 * exp * Math.Clamp(jitter01, 0, 1);
        return TimeSpan.FromSeconds(Math.Min(c.TotalSeconds, Math.Max(1, jittered)));
    }
}
