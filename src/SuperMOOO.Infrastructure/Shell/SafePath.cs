namespace SuperMOOO.Infrastructure.Shell;

public static class SafePath
{
    private const int MaxFileNameLength = 150;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Ghép đường dẫn tương đối vào root; ném lỗi nếu kết quả thoát khỏi root (../, đường dẫn tuyệt đối).</summary>
    public static string Combine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Đường dẫn \"{relative}\" nằm ngoài thư mục cho phép.", nameof(relative));
        return full;
    }

    public static string SanitizeFileName(string name, string fallback = "file")
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > MaxFileNameLength) clean = clean[..MaxFileNameLength].TrimEnd();
        if (clean.Length == 0) return fallback;
        return Reserved.Contains(Path.GetFileNameWithoutExtension(clean)) ? "_" + clean : clean;
    }
}
