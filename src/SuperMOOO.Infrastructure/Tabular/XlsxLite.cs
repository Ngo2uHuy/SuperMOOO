using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SuperMOOO.Infrastructure.Tabular;

/// <summary>Đọc/ghi .xlsx một sheet, mọi ô là chuỗi. Không hỗ trợ .xls (đã chốt bỏ).</summary>
public static class XlsxLite
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static void Write(Stream output, TableData table, string sheetName = "Sheet1")
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
            """);
        Add(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Add(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
            """);
        Add(zip, "xl/workbook.xml", new XDocument(new XElement(Main + "workbook", new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
            new XElement(Main + "sheets", new XElement(Main + "sheet",
                new XAttribute("name", SheetName(sheetName)), new XAttribute("sheetId", 1), new XAttribute(Rel + "id", "rId1"))))).ToString());

        var data = new XElement(Main + "sheetData");
        AppendRow(data, 1, table.Columns);
        for (var i = 0; i < table.Rows.Count; i++) AppendRow(data, i + 2, table.Rows[i]);
        Add(zip, "xl/worksheets/sheet1.xml", new XDocument(new XElement(Main + "worksheet", data)).ToString());
    }

    public static TableData Read(Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var workbook = Load(zip, "xl/workbook.xml") ?? throw new InvalidDataException("File không phải .xlsx hợp lệ (thiếu workbook).");
        var relId = workbook.Descendants(Main + "sheet").FirstOrDefault()?.Attribute(Rel + "id")?.Value
            ?? throw new InvalidDataException("Workbook không có sheet nào.");
        var target = Load(zip, "xl/_rels/workbook.xml.rels")?.Descendants(Pkg + "Relationship")
            .FirstOrDefault(r => r.Attribute("Id")?.Value == relId)?.Attribute("Target")?.Value
            ?? throw new InvalidDataException("Không tìm thấy dữ liệu sheet.");
        var sheet = Load(zip, "xl/" + target.TrimStart('/').Replace("xl/", "")) ?? throw new InvalidDataException("Thiếu file sheet.");

        var shared = Load(zip, "xl/sharedStrings.xml")?.Descendants(Main + "si")
            .Select(si => string.Concat(si.Elements(Main + "t").Concat(si.Elements(Main + "r").Elements(Main + "t")).Select(t => t.Value))).ToList() ?? [];

        var grid = new SortedDictionary<int, SortedDictionary<int, string>>();
        var rowNumber = 0;
        foreach (var row in sheet.Descendants(Main + "row"))
        {
            rowNumber = int.TryParse(row.Attribute("r")?.Value, out var r) ? r : rowNumber + 1;
            var cells = grid[rowNumber] = [];
            var column = 0;
            foreach (var c in row.Elements(Main + "c"))
            {
                column = c.Attribute("r")?.Value is { } reference ? ColumnIndex(reference) : column + 1;
                cells[column] = CellText(c, shared);
            }
        }

        if (grid.Count == 0) return TableData.Empty;
        var width = grid.Values.Max(r => r.Count == 0 ? 0 : r.Keys.Max()) + 1;
        List<string> Line(SortedDictionary<int, string> r) => Enumerable.Range(0, width).Select(i => r.GetValueOrDefault(i, "")).ToList();

        var rows = grid.Skip(1).Select(kv => (IReadOnlyList<string>)Line(kv.Value)).ToList();
        return new TableData(Line(grid.First().Value), rows);
    }

    private static string CellText(XElement c, List<string> shared) => c.Attribute("t")?.Value switch
    {
        "s" => int.TryParse(c.Element(Main + "v")?.Value, out var i) && i < shared.Count ? shared[i] : "",
        "inlineStr" => string.Concat(c.Descendants(Main + "t").Select(t => t.Value)),
        "b" => c.Element(Main + "v")?.Value == "1" ? "TRUE" : "FALSE",
        _ => c.Element(Main + "v")?.Value ?? ""
    };

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter)) index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        return index - 1;
    }

    private static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    private static void AppendRow(XElement sheetData, int number, IReadOnlyList<string> values)
    {
        var row = new XElement(Main + "row", new XAttribute("r", number));
        for (var i = 0; i < values.Count; i++)
            row.Add(new XElement(Main + "c", new XAttribute("r", ColumnName(i) + number), new XAttribute("t", "inlineStr"),
                new XElement(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Clean(values[i])))));
        sheetData.Add(row);
    }

    // Ký tự điều khiển không hợp lệ trong XML 1.0 làm Excel báo file hỏng.
    private static string Clean(string value) => new(value.Where(XmlConvert.IsXmlChar).ToArray());

    private static string SheetName(string name)
    {
        var clean = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray()).Trim();
        return clean.Length == 0 ? "Sheet1" : clean[..Math.Min(clean.Length, 31)];
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
