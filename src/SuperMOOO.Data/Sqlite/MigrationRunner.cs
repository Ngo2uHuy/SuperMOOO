using System.Reflection;
using System.Text.RegularExpressions;

namespace SuperMOOO.Data.Sqlite;

[Service]
public sealed partial class MigrationRunner(IDbConnectionFactory factory, DatabaseMaintenance maintenance)
{
    [GeneratedRegex(@"^Migrations\.V(\d+)__.+\.sql$")]
    private static partial Regex ScriptName();

    /// <summary>Phiên bản script mới nhất trong bản app này (khôi phục dùng để từ chối bản sao lưu của app mới hơn).</summary>
    public static int LatestVersion() =>
        typeof(MigrationRunner).Assembly.GetManifestResourceNames()
            .Select(name => ScriptName().Match(name))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0).Max();

    /// <summary>Áp các script có phiên bản lớn hơn PRAGMA user_version; trả về phiên bản sau cùng.</summary>
    public int Migrate()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Select(name => (Name: name, Match: ScriptName().Match(name)))
            .Where(x => x.Match.Success)
            .Select(x => (x.Name, Version: int.Parse(x.Match.Groups[1].Value)))
            .OrderBy(x => x.Version)
            .ToList();

        using var db = factory.Open();
        var current = db.ExecuteScalar<int>("PRAGMA user_version");

        // DB đã có dữ liệu (không phải cài mới) thì sao lưu trước khi script mới chạm vào.
        if (current > 0 && scripts.Any(s => s.Version > current)) maintenance.Backup();

        foreach (var (name, version) in scripts.Where(s => s.Version > current))
        {
            using var tx = db.BeginTransaction();
            db.Execute(ReadScript(assembly, name), transaction: tx);
            db.Execute($"PRAGMA user_version = {version}", transaction: tx);   // PRAGMA không nhận tham số; version là int tự parse
            tx.Commit();
            current = version;
        }

        return current;
    }

    private static string ReadScript(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
