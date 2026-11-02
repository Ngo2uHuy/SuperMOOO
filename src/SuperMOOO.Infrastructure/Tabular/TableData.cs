namespace SuperMOOO.Infrastructure.Tabular;

/// <summary>Bảng chuỗi dùng chung cho ResultGrid và xuất CSV/XLSX; mọi ô là chuỗi để round-trip không đổi định dạng (vd mã "007").</summary>
public sealed record TableData(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public static TableData Empty { get; } = new([], []);

    /// <summary>Mảng object JSON → bảng (cột = hợp các key theo thứ tự xuất hiện); giá trị lồng nhau giữ nguyên dạng JSON.</summary>
    public static TableData FromJson(JsonNode? node)
    {
        var items = node switch
        {
            JsonArray array => array.OfType<JsonObject>().ToList(),
            JsonObject single => [single],
            _ => []
        };

        var columns = items.SelectMany(o => o.Select(p => p.Key)).Distinct().ToList();
        var rows = items.Select(o => (IReadOnlyList<string>)columns.Select(c => Cell(o[c])).ToList()).ToList();
        return new TableData(columns, rows);
    }

    private static string Cell(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString()
    };
}
