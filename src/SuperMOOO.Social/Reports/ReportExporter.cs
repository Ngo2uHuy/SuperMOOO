using System.Globalization;
using System.Text;
using SuperMOOO.Infrastructure.Tabular;

namespace SuperMOOO.Social.Reports;

/// <summary>Xuất bình luận ra XLSX hoặc CSV (theo đuôi file).</summary>
[Service]
public sealed class ReportExporter
{
    private static readonly string[] CommentColumns =
        ["STT", "Tác giả", "Link trang cá nhân", "Nội dung bình luận", "Likes", "Thời gian", "Cảm xúc", "Phân loại lead", "Nguồn"];

    public static string SentimentLabel(string sentiment) => sentiment switch { "positive" => "Tích cực", "negative" => "Tiêu cực", _ => "Trung tính" };

    public Task<string> ExportCommentsAsync(IReadOnlyList<CommentRecord> comments, string platform, string path, CancellationToken ct = default) =>
        WriteAsync(new TableData(CommentColumns, comments.Select((c, i) => (IReadOnlyList<string>)
        [
            Num(i + 1), c.Author, c.ProfileUrl ?? "", c.Text, Num(c.Likes), c.Created ?? "", SentimentLabel(c.Sentiment),
            c.LeadType ?? (c.IsLead ? "Lead khách hàng" : "Thường"), platform
        ]).ToList()), path, "Comments & Leads", ct);

    /// <summary>Bảng tuỳ ý (vd kết quả tab Xu hướng của S11).</summary>
    public Task<string> ExportTableAsync(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, string path, string sheet,
        CancellationToken ct = default) =>
        WriteAsync(new TableData(columns, rows), path, sheet, ct);

    private static string Num(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static async Task<string> WriteAsync(TableData table, string path, string sheet, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            await File.WriteAllTextAsync(path, CsvCodec.Write(table), new UTF8Encoding(true), ct);      // BOM để Excel nhận UTF-8
        else
        {
            await using var stream = File.Create(path);
            XlsxLite.Write(stream, table, sheet);
        }

        return path;
    }
}
