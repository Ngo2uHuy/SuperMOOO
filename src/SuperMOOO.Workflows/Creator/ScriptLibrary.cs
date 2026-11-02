using System.IO.Compression;
using System.Xml.Linq;

namespace SuperMOOO.Workflows.Creator;

public sealed record ScriptFile(string Path, string Name, int Chars, int Words, string Preview);

/// <summary>Kịch bản TXT/DOCX (DOCX đọc trực tiếp bằng ZipArchive, không thêm gói).</summary>
[Service]
public sealed class ScriptLibrary
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".docx" };

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Kịch bản trong thư mục (không đệ quy), theo tên; file không đọc được bị bỏ qua thay vì làm hỏng cả danh sách.</summary>
    public IReadOnlyList<ScriptFile> Scan(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Thư mục không tồn tại: {folder}");
        var result = new List<ScriptFile>();
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => Extensions.Contains(Path.GetExtension(f))).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.Add(Load(file));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { /* file hỏng/khoá: bỏ qua */ }
        }

        return result;
    }

    /// <summary>Một file kịch bản .txt/.docx người dùng chọn lẻ.</summary>
    public ScriptFile Load(string file)
    {
        if (!Extensions.Contains(Path.GetExtension(file))) throw new ArgumentException("Chỉ nhận kịch bản .txt hoặc .docx.");
        var text = Read(file);
        return new ScriptFile(Path.GetFullPath(file), Path.GetFileNameWithoutExtension(file), text.Length,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Trim()[..Math.Min(200, text.Trim().Length)]);
    }

    public Task<string> ReadAsync(string path, CancellationToken ct = default) => Task.Run(() => Read(path), ct);

    private static string Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".txt") return File.ReadAllText(path);
        if (ext != ".docx") throw new InvalidDataException("Kịch bản phải là TXT hoặc DOCX.");

        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("File DOCX không hợp lệ.");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        return string.Join('\n', doc.Descendants(W + "p").Select(p => string.Concat(p.Descendants().Select(e =>
            e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : e.Name == W + "br" ? "\n" : ""))));
    }
}
