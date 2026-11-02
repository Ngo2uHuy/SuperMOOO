using System.Text.RegularExpressions;

namespace SuperMOOO.Media.Ffmpeg;

public static partial class FilterEscaping
{
    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex SafeName();

    public static bool IsSafeName(string name) => SafeName().IsMatch(name);

    /// <summary>
    /// Chép file (ASS, textfile…) vào thư mục làm việc dưới tên ASCII rồi tham chiếu bằng tên tương đối
    /// (chạy ffmpeg với WorkingDirectory = workDir): tránh mọi escape của dấu nháy, dấu hai chấm, tiếng Việt trong đường dẫn.
    /// </summary>
    public static string Stage(string sourcePath, string workDir, string asciiName)
    {
        if (!IsSafeName(asciiName)) throw new ArgumentException("Tên file tham chiếu trong filter phải là ASCII an toàn.", nameof(asciiName));
        Directory.CreateDirectory(workDir);
        File.Copy(sourcePath, Path.Combine(workDir, asciiName), overwrite: true);
        return asciiName;
    }

    /// <summary>Đường dẫn tuyệt đối dùng làm giá trị option trong filter (vd fontfile): đổi \ thành /, escape : và '.</summary>
    public static string PathForFilter(string path) =>
        path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");
}
