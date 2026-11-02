using System.Text;

namespace SuperMOOO.Infrastructure.Tabular;

public static class CsvCodec
{
    public static string Write(TableData table)
    {
        var sb = new StringBuilder();
        AppendRow(sb, table.Columns);
        foreach (var row in table.Rows) AppendRow(sb, row);
        return sb.ToString();
    }

    /// <summary>RFC 4180: dấu ngoặc kép bao ô có `,` xuống dòng hay `"`; dòng đầu là tiêu đề; BOM được bỏ qua.</summary>
    public static TableData Read(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        text = text.TrimStart('﻿');

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
                continue;
            }

            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': EndRecord(); break;
                default: cell.Append(c); break;
            }
        }

        if (cell.Length > 0 || row.Count > 0) EndRecord();
        if (records.Count == 0) return TableData.Empty;

        var width = records[0].Count;
        return new TableData(records[0], records.Skip(1)
            .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, width).Select(i => i < r.Count ? r[i] : "").ToList()).ToList());

        void EndRecord()
        {
            row.Add(cell.ToString());
            cell.Clear();
            records.Add(row);
            row = [];
        }
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var v = cells[i];
            sb.Append(v.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v);
        }

        sb.Append("\r\n");
    }
}
