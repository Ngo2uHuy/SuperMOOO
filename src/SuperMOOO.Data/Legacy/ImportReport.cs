using System.Text.Json;

namespace SuperMOOO.Data.Legacy;

public sealed class TableCount
{
    public int Read { get; set; }
    public int Imported { get; set; }
    public int Skipped { get; set; }
}

/// <summary>Kết quả nhập: chỉ số đếm và tên khoá/tệp; tuyệt đối không chứa giá trị bí mật.</summary>
public sealed class ImportReport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string SourceHash { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, TableCount> Tables { get; } = [];
    public List<string> InvalidKeys { get; } = [];
    public List<string> MissingFiles { get; } = [];
    public List<string> Warnings { get; } = [];

    public int TotalImported => Tables.Values.Sum(t => t.Imported);

    public TableCount Table(string name) => Tables.TryGetValue(name, out var t) ? t : Tables[name] = new TableCount();

    public void Count(string table, bool imported)
    {
        var t = Table(table);
        t.Read++;
        if (imported) t.Imported++;
        else t.Skipped++;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public async Task SaveAsync(string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"import-report-{At:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, ToJson(), ct);
        SavedPath = path;
    }

    public string? SavedPath { get; private set; }
}
