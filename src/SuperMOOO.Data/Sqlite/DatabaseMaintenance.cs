using Microsoft.Data.Sqlite;

namespace SuperMOOO.Data.Sqlite;

/// <summary>Sao lưu aivs.db cạnh file gốc (aivs.db.bak-yyyyMMdd-HHmmss) và thu gọn file khi nhiều trang trống.</summary>
[Service]
public sealed class DatabaseMaintenance(AppPaths paths, IDbConnectionFactory factory)
{
    private const int KeepBackups = 2;

    public bool HasBackup => Backups().Any();

    /// <summary>Dùng backup API của SQLite nên bản sao nhất quán cả khi app đang mở kết nối khác và còn dữ liệu trong WAL.</summary>
    public string Backup()
    {
        var target = $"{paths.Database}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
        using (var source = (SqliteConnection)factory.Open())
        // Không pool để file backup được đóng ngay, lần sau còn xoá được.
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        foreach (var old in Backups().Skip(KeepBackups))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* lần sao lưu sau xoá tiếp */ }
        }

        return target;
    }

    /// <summary>VACUUM chỉ khi số trang trống vượt ngưỡng: VACUUM ghi lại cả file và giữ khoá ghi suốt thời gian chạy.</summary>
    public bool CompactIfFragmented(double minFreeRatio)
    {
        using var db = factory.Open();
        var pages = db.ExecuteScalar<long>("PRAGMA page_count");
        var free = db.ExecuteScalar<long>("PRAGMA freelist_count");
        if (pages == 0 || free < pages * minFreeRatio) return false;

        db.Execute("VACUUM");
        // VACUUM ở chế độ WAL đẩy cả DB vào file -wal; checkpoint để file -wal không phình theo.
        db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        return true;
    }

    // Tên có dấu thời gian cố định độ dài nên sắp theo chuỗi là mới nhất trước.
    private IEnumerable<string> Backups() =>
        Directory.EnumerateFiles(Path.GetDirectoryName(paths.Database)!, Path.GetFileName(paths.Database) + ".bak-*")
            .OrderDescending(StringComparer.Ordinal);
}
