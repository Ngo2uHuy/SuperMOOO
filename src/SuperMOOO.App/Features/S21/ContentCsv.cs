using System.IO;
using SuperMOOO.Infrastructure.Tabular;
using SuperMOOO.Publishing.Library;

namespace SuperMOOO.App.Features.S21;

/// <summary>Nhập danh sách video từ CSV/Excel: mỗi dòng một video (cột đường dẫn bắt buộc) kèm title/caption/tags/language/AI/reup.</summary>
public static class ContentCsv
{
    private static readonly string[] PathNames = ["path", "file", "video", "duongdan", "đườngdẫn", "đường dẫn"];
    private static readonly string[] TitleNames = ["title", "tieude", "tiêuđề", "tiêu đề"];
    private static readonly string[] CaptionNames = ["caption", "description", "mota", "môtả", "mô tả"];
    private static readonly string[] TagNames = ["tags", "tag", "nhan", "nhãn"];
    private static readonly string[] LanguageNames = ["language", "lang", "ngonngu", "ngônngữ", "ngôn ngữ"];

    public static TableData Read(string path)
    {
        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return XlsxLite.Read(stream);
        }
        return CsvCodec.Read(File.ReadAllText(path));
    }

    /// <returns>Yêu cầu nhập từng dòng; dòng thiếu đường dẫn hoặc file không tồn tại ghi vào <paramref name="errors"/>.</returns>
    public static IReadOnlyList<IngestRequest> Parse(TableData table, List<string> errors)
    {
        int Col(string[] names) => table.Columns.ToList().FindIndex(c => names.Contains(c.Trim().ToLowerInvariant()));
        var (pathCol, titleCol, captionCol, tagCol, langCol, aiCol, reupCol) =
            (Col(PathNames), Col(TitleNames), Col(CaptionNames), Col(TagNames), Col(LanguageNames), Col(["ai"]), Col(["reup"]));
        if (pathCol < 0) { errors.Add("Thiếu cột đường dẫn video (path/file/video)."); return []; }

        string? Cell(IReadOnlyList<string> row, int col) => col >= 0 && col < row.Count && !string.IsNullOrWhiteSpace(row[col]) ? row[col].Trim() : null;
        bool? Flag(IReadOnlyList<string> row, int col) => Cell(row, col)?.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "x" or "có" or "co" => true,
            "0" or "false" or "no" or "không" or "khong" => false,
            _ => null
        };

        var result = new List<IngestRequest>();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            if (Cell(row, pathCol) is not { } file) { errors.Add($"Dòng {i + 2}: thiếu đường dẫn."); continue; }
            if (!File.Exists(file)) { errors.Add($"Dòng {i + 2}: không thấy file {file}."); continue; }
            try
            {
                var tags = Cell(row, tagCol)?.Split([',', ';', '#'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var request = LooseFileReader.Build(file, LibrarySources.Manual, null, requireStable: false) with
                {
                    Title = Cell(row, titleCol) ?? Path.GetFileNameWithoutExtension(file), Caption = Cell(row, captionCol), Language = Cell(row, langCol),
                    Tags = tags, IsAiGenerated = Flag(row, aiCol) ?? false, IsReupload = Flag(row, reupCol) ?? false
                };
                result.Add(request);
            }
            catch (IngestException e) { errors.Add($"Dòng {i + 2}: {e.Message}"); }
        }
        return result;
    }
}
