namespace SuperMOOO.Workflows.Auto;

public static class SourceKinds
{
    public static bool IsUrl(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tên dùng cho thư mục/file kết quả: tên file, hoặc mã ngắn của URL (tên thật chỉ biết sau khi tải).</summary>
    public static string NameOf(string source) => IsUrl(source)
        ? "url_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))[..8]
        : Path.GetFileNameWithoutExtension(source);

    /// <summary>Nguồn URL tự thêm bước download (tiền tố pipeline): đặt cờ `download` trong input; tên/thư mục lấy từ `outputDir`, `name` của người gọi.</summary>
    public static void MarkDownload(JsonObject input)
    {
        if (input["source"]?.GetValue<string>() is { } s && IsUrl(s)) input["download"] = true;
    }
}
