using System.Globalization;

namespace SuperMOOO.Workflows.Steps;

/// <summary>Đọc input/state giữa các bước. State chỉ chứa chuỗi: số theo InvariantCulture, dữ liệu lớn nằm trong file ở thư mục làm việc.</summary>
internal static class Io
{
    public static string? Str(StepContext c, string key) =>
        c.Input[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    public static bool Flag(StepContext c, string key) =>
        c.Input[key] is JsonValue v && v.TryGetValue<bool>(out var on) && on;

    public static string Require(StepContext c, string key) =>
        Str(c, key) ?? throw new InvalidOperationException($"Thiếu đầu vào '{key}' của tác vụ.");

    /// <summary>Bước download (nguồn là URL) ghi file đã tải vào state "source" và thay cho input.source ở các bước sau.</summary>
    public static string Source(StepContext c) => StateOrNull(c, "source") ?? Require(c, "source");

    public static string OutputDir(StepContext c)
    {
        var dir = Require(c, "outputDir");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Name(StepContext c) => Str(c, "name") ?? Path.GetFileNameWithoutExtension(StateOrNull(c, "source") ?? Str(c, "source") ?? "output");

    public static string State(StepContext c, string key) =>
        c.State.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new InvalidOperationException($"Bước trước chưa tạo '{key}'.");

    public static string? StateOrNull(StepContext c, string key) => c.State.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public static double Num(StepContext c, string key) => double.Parse(State(c, key), CultureInfo.InvariantCulture);

    public static double? NumOrNull(StepContext c, string key) =>
        StateOrNull(c, key) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : null;

    public static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static string Work(StepContext c, string file)
    {
        Directory.CreateDirectory(c.WorkFolder);
        return Path.Combine(c.WorkFolder, file);
    }

    public static StepOutput Out(IEnumerable<(string Key, string Value)> values, params string[] files) =>
        new(values.ToDictionary(v => v.Key, v => v.Value), files);

    /// <summary>Cảnh báo theo câu/bước gom vào state "warnings" (mỗi dòng một cảnh báo) để hiện trong kết quả job.</summary>
    public static string Warnings(StepContext c, IEnumerable<string> add) =>
        string.Join('\n', (StateOrNull(c, "warnings")?.Split('\n') ?? []).Concat(add));

    public static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)}_{i}{Path.GetExtension(fileName)}");
        return path;
    }

    /// <summary>
    /// Giữ tên cố định để chạy lại thì ghi đè kết quả cũ; nhưng nếu file cũ đang bị giữ (đang phát ở khung kết quả, mở trong trình phát khác)
    /// thì ghi sang tên mới thay vì lỗi "Access to the path is denied" ở bước cuối sau khi đã tốn công dựng.
    /// </summary>
    public static string OverwritablePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        for (var i = 2; IsLocked(path); i++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)}_{i}{Path.GetExtension(fileName)}");
        return path;
    }

    private static bool IsLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
