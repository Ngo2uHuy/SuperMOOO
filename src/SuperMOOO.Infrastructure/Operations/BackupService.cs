using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Dapper;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Infrastructure.Operations;

public static class BackupJobTypes
{
    public const string Create = "backup.create";
    public const string Stage = "backup.stage";
}

public sealed record BackupMetadata(
    int FormatVersion, int SchemaVersion, string AppVersion, DateTimeOffset CreatedUtc,
    string Machine, string User, string DataRoot, bool IncludesMedia, int MediaFiles);

/// <summary>Bản khôi phục đã dựng sẵn ở restore-pending/pending.json; MediaRoot là thư mục xuất của máy này lúc dựng.</summary>
public sealed record RestoreMarker(string BackupPath, string SourceDataRoot, bool IncludesMedia, string MediaRoot, DateTimeOffset StagedUtc);

public sealed record BackupInfo(
    string Path, string Name, long SizeBytes, bool Valid, string? Error, int SchemaVersion, string? AppVersion,
    DateTimeOffset? CreatedUtc, string? Machine, string? User, bool IncludesMedia, bool SameMachine, IReadOnlyList<string> Warnings, string Kind);

/// <summary>
/// Sao lưu toàn app = zip gồm aivs.db (chụp bằng SQLite backup API, nhất quán khi app đang chạy; settings và secret DPAPI nằm trong DB),
/// voices\, lipsync\avatars\ và tuỳ chọn media\ (thư mục xuất). KHÔNG gồm models, cache, worker venv, lipsync venv (quá lớn, tải lại được).
/// Khôi phục hai bước: kiểm tra + dựng bản chờ (không đụng dữ liệu đang chạy) → <see cref="PendingRestore"/> áp khi app khởi động lại.
/// </summary>
[Service]
public sealed class BackupService(AppPaths paths, IDbConnectionFactory factory, ISettingsStore settings, IClock clock, ILogger<BackupService> log)
{
    public const int FormatVersion = 1;
    public const string DbEntry = "aivs.db";
    public const string MetadataEntry = "backup.json";
    public const string VoicesPrefix = "voices/";
    public const string AvatarsPrefix = "lipsync-avatars/";
    public const string MediaPrefix = "media/";
    private const string ManualPrefix = "supermooo-backup-";
    private const string AutoPrefix = "supermooo-auto-";
    private const string PreRestorePrefix = "supermooo-prerestore-";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string AvatarsDir => System.IO.Path.Combine(paths.DataRoot, "lipsync", "avatars");

    public string OutputRoot() =>
        settings.Get<PathsSettings>("paths").OutputRoot is { Length: > 0 } root ? root : AppPaths.DefaultOutputRoot;

    public string BackupDirectory() =>
        settings.Get<StorageSettings>("storage").BackupDir is { Length: > 0 } dir ? dir : paths.Backups;

    /// <param name="kind">manual | auto | prerestore: quyết định tiền tố tên file; chỉ "auto" bị xoá theo số bản giữ.</param>
    public async Task<BackupInfo> CreateAsync(bool includeMedia, string? targetPath, string kind, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var work = System.IO.Path.Combine(paths.Cache, "backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stamp = clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var prefix = kind switch { "auto" => AutoPrefix, "prerestore" => PreRestorePrefix, _ => ManualPrefix };
            var dest = targetPath ?? System.IO.Path.Combine(BackupDirectory(), $"{prefix}{stamp}.zip");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            Directory.CreateDirectory(work);

            progress?.Report(new ProgressInfo(-1, "Chụp cơ sở dữ liệu"));
            var dbCopy = System.IO.Path.Combine(work, DbEntry);
            var schema = await Task.Run(() => SnapshotDatabase(dbCopy), ct);

            var tmpZip = dest + ".tmp";
            try
            {
                var mediaFiles = 0;
                using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(dbCopy, DbEntry, CompressionLevel.Optimal);
                    progress?.Report(new ProgressInfo(-1, "Nén giọng và avatar"));
                    AddFolder(zip, paths.Voices, VoicesPrefix, CompressionLevel.Optimal, ct);
                    AddFolder(zip, AvatarsDir, AvatarsPrefix, CompressionLevel.Optimal, ct);
                    if (includeMedia)
                    {
                        progress?.Report(new ProgressInfo(-1, "Nén media"));
                        mediaFiles = AddFolder(zip, OutputRoot(), MediaPrefix, CompressionLevel.NoCompression, ct);
                    }
                    var meta = new BackupMetadata(FormatVersion, schema, AppVersion, clock.UtcNow, Environment.MachineName,
                        Environment.UserName, paths.DataRoot, includeMedia, mediaFiles);
                    var entry = zip.CreateEntry(MetadataEntry);
                    await using var writer = entry.Open();
                    await JsonSerializer.SerializeAsync(writer, meta, cancellationToken: ct);
                }
                File.Move(tmpZip, dest, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpZip)) File.Delete(tmpZip);
            }

            if (kind == "auto") ApplyRetention(System.IO.Path.GetDirectoryName(dest)!);
            log.LogInformation("Đã tạo bản sao lưu {Kind}: {Path}", kind, dest);
            return Inspect(dest);
        }
        finally
        {
            DeleteQuietly(work);
            _gate.Release();
        }
    }

    private static string AppVersion => typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>SQLite backup API đọc nhất quán dù app đang ghi (không copy file đang mở); trả user_version của bản chụp.</summary>
    private int SnapshotDatabase(string destination)
    {
        using var source = (SqliteConnection)factory.Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
        return Convert.ToInt32(target.ExecuteScalar<long>("PRAGMA user_version"));
    }

    private static int AddFolder(ZipArchive zip, string root, string prefix, CompressionLevel level, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return 0;
        var count = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            try
            {
                zip.CreateEntryFromFile(file, prefix + relative, level);
                count++;
            }
            catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { }   // đang bị khoá/vừa bị dọn: bỏ qua file đó
        }
        return count;
    }

    // Tên chứa timestamp UTC nên sắp theo tên = sắp theo thời gian.
    private void ApplyRetention(string directory)
    {
        var keep = Math.Max(1, settings.Get<StorageSettings>("storage").AutoBackupKeep);
        foreach (var file in Directory.EnumerateFiles(directory, AutoPrefix + "*.zip").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Không xoá được bản sao lưu cũ {File}: {Reason}", file, e.Message); }
        }
    }

    public DateTimeOffset? LastAutoBackupUtc()
    {
        var dir = BackupDirectory();
        if (!Directory.Exists(dir)) return null;
        var last = Directory.EnumerateFiles(dir, AutoPrefix + "*.zip").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
        return last is null ? null : File.GetLastWriteTimeUtc(last);
    }

    public IReadOnlyList<BackupInfo> List()
    {
        var dir = BackupDirectory();
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "supermooo-*.zip").OrderByDescending(f => f, StringComparer.Ordinal).Take(30).Select(Inspect).ToList();
    }

    /// <summary>Kiểm tra nhanh: zip hợp lệ, có metadata + DB, schema không mới hơn bản app này. Chưa chạy integrity_check (làm ở bước dựng bản chờ).</summary>
    public BackupInfo Inspect(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var kind = name.StartsWith(AutoPrefix, StringComparison.Ordinal) ? "auto"
            : name.StartsWith(PreRestorePrefix, StringComparison.Ordinal) ? "prerestore"
            : name.StartsWith(ManualPrefix, StringComparison.Ordinal) ? "manual" : "external";
        BackupInfo Invalid(string error, long size = 0) => new(path, name, size, false, error, 0, null, null, null, null, false, false, [], kind);

        var file = new FileInfo(path);
        if (!file.Exists) return Invalid("Không tìm thấy file sao lưu.");
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var metaEntry = zip.GetEntry(MetadataEntry);
            if (metaEntry is null || zip.GetEntry(DbEntry) is null || metaEntry.Length > 64 * 1024)
                return Invalid("Không phải file sao lưu của SuperMOOO (thiếu backup.json hoặc aivs.db).", file.Length);

            BackupMetadata? meta;
            using (var stream = metaEntry.Open()) meta = JsonSerializer.Deserialize<BackupMetadata>(stream);
            if (meta is null) return Invalid("backup.json rỗng.", file.Length);
            if (meta.FormatVersion != FormatVersion) return Invalid($"Định dạng sao lưu {meta.FormatVersion} chưa được hỗ trợ.", file.Length);
            var latest = MigrationRunner.LatestVersion();
            if (meta.SchemaVersion > latest)
                return Invalid($"Bản sao lưu tạo bởi phiên bản SuperMOOO mới hơn (schema {meta.SchemaVersion} > {latest}). Hãy cập nhật app rồi thử lại.", file.Length);

            var same = string.Equals(meta.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(meta.User, Environment.UserName, StringComparison.OrdinalIgnoreCase);
            var warnings = new List<string>();
            if (!same)
                warnings.Add($"Sao lưu tạo trên {meta.Machine}\\{meta.User}. API key và token đăng nhập mã hoá DPAPI chỉ giải mã được trên đúng máy và tài khoản Windows đó: "
                             + "sau khi khôi phục hãy nhập lại API key và kết nối lại từng tài khoản đăng bài.");
            if (!meta.IncludesMedia) warnings.Add("Sao lưu không kèm media: file video/ảnh đã xuất không được khôi phục.");
            if (meta.SchemaVersion < latest) warnings.Add($"Sao lưu ở schema {meta.SchemaVersion}; sẽ tự nâng lên {latest} khi app khởi động lại.");
            return new BackupInfo(path, name, file.Length, true, null, meta.SchemaVersion, meta.AppVersion, meta.CreatedUtc,
                meta.Machine, meta.User, meta.IncludesMedia, same, warnings, kind);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        {
            return Invalid($"File sao lưu không đọc được: {e.Message}", file.Length);
        }
    }

    /// <summary>
    /// Bước 1 của khôi phục: kiểm tra đầy đủ (kể cả integrity_check), giải nén vào restore-pending, sao lưu bản hiện tại (không kèm media) làm dự phòng.
    /// Chưa đụng dữ liệu đang chạy; lỗi ở bước nào thì dữ liệu hiện tại còn nguyên.
    /// </summary>
    public async Task<BackupInfo> StageRestoreAsync(string path, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var info = Inspect(path);
        if (!info.Valid) throw new InvalidOperationException(info.Error ?? "File sao lưu không hợp lệ.");

        var building = paths.RestorePending + ".building";
        DeleteQuietly(building);
        Directory.CreateDirectory(building);
        try
        {
            progress?.Report(new ProgressInfo(-1, "Giải nén và kiểm tra"));
            var outputRoot = OutputRoot();
            await Task.Run(() => Extract(path, building, info.IncludesMedia, ct), ct);

            progress?.Report(new ProgressInfo(-1, "Sao lưu dữ liệu hiện tại"));
            var safety = await CreateAsync(false, null, "prerestore", null, ct);
            log.LogInformation("Bản dự phòng trước khi khôi phục: {Path}", safety.Path);

            var source = ReadMetadata(path);
            var marker = new RestoreMarker(path, source.DataRoot, info.IncludesMedia, outputRoot, clock.UtcNow);
            await File.WriteAllTextAsync(System.IO.Path.Combine(building, PendingRestore.MarkerFile), JsonSerializer.Serialize(marker), ct);
            DeleteQuietly(paths.RestorePending);
            Directory.Move(building, paths.RestorePending);
            return info;
        }
        catch
        {
            DeleteQuietly(building);
            throw;
        }
    }

    public bool HasPendingRestore => File.Exists(System.IO.Path.Combine(paths.RestorePending, PendingRestore.MarkerFile));

    public void CancelPendingRestore() => DeleteQuietly(paths.RestorePending);

    private static BackupMetadata ReadMetadata(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(MetadataEntry)!.Open();
        return JsonSerializer.Deserialize<BackupMetadata>(stream)!;
    }

    private static void Extract(string zipPath, string stagingDir, bool includeMedia, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var dbPath = System.IO.Path.Combine(stagingDir, DbEntry);
        zip.GetEntry(DbEntry)!.ExtractToFile(dbPath, overwrite: true);
        VerifyDatabase(dbPath);

        ExtractFolder(zip, VoicesPrefix, System.IO.Path.Combine(stagingDir, "voices"), ct);
        ExtractFolder(zip, AvatarsPrefix, System.IO.Path.Combine(stagingDir, "lipsync-avatars"), ct);
        if (includeMedia) ExtractFolder(zip, MediaPrefix, System.IO.Path.Combine(stagingDir, "media"), ct);
    }

    private static void ExtractFolder(ZipArchive zip, string prefix, string targetRoot, CancellationToken ct)
    {
        var root = System.IO.Path.GetFullPath(targetRoot);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.Name.Length == 0) continue;
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, entry.FullName[prefix.Length..]));
            // Zip-slip: entry không được thoát khỏi thư mục đích.
            if (!target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File sao lưu chứa đường dẫn không hợp lệ.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void VerifyDatabase(string dbPath)
    {
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open();
            var result = db.ExecuteScalar<string>("PRAGMA integrity_check");
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Cơ sở dữ liệu trong bản sao lưu bị hỏng (integrity_check: {result}).");
            var schema = Convert.ToInt32(db.ExecuteScalar<long>("PRAGMA user_version"));
            if (schema > MigrationRunner.LatestVersion())
                throw new InvalidOperationException($"Cơ sở dữ liệu trong bản sao lưu có schema {schema} mới hơn app này.");
        }
        catch (SqliteException e)
        {
            throw new InvalidOperationException($"Cơ sở dữ liệu trong bản sao lưu không đọc được: {e.Message}");
        }
    }

    private static void DeleteQuietly(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Bước 2 của khôi phục: chạy đầu AppHost.Build, trước khi mở DB, nên không file nào đang bị dùng.</summary>
public static class PendingRestore
{
    public const string MarkerFile = "pending.json";

    /// <returns>Mô tả để ghi log; null nếu không có gì chờ áp.</returns>
    public static string? TryApply(AppPaths paths)
    {
        var pending = paths.RestorePending;
        var markerPath = Path.Combine(pending, MarkerFile);
        var stagedDb = Path.Combine(pending, BackupService.DbEntry);
        if (!File.Exists(markerPath) || !File.Exists(stagedDb)) return null;

        RestoreMarker? marker;
        try { marker = JsonSerializer.Deserialize<RestoreMarker>(File.ReadAllText(markerPath)); }
        catch (Exception e) when (e is JsonException or IOException) { return $"Bỏ qua khôi phục chờ áp: {pending} không đọc được ({e.Message})."; }
        if (marker is null) return "Bỏ qua khôi phục chờ áp: pending.json rỗng.";

        var aside = ".before-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var moved = new List<(string From, string To)>();
        var stagedMoved = false;
        try
        {
            foreach (var file in new[] { paths.Database, paths.Database + "-wal", paths.Database + "-shm" }.Where(File.Exists))
            {
                File.Move(file, file + aside);
                moved.Add((file, file + aside));
            }
            File.Move(stagedDb, paths.Database);
            stagedMoved = true;

            ReplaceFolder(Path.Combine(pending, "voices"), paths.Voices, aside, moved);
            ReplaceFolder(Path.Combine(pending, "lipsync-avatars"), Path.Combine(paths.DataRoot, "lipsync", "avatars"), aside, moved);
            MergeMedia(Path.Combine(pending, "media"), marker.MediaRoot);

            Directory.Delete(pending, recursive: true);
            return $"Đã khôi phục từ {marker.BackupPath}; dữ liệu cũ giữ lại với hậu tố {aside}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Hoàn tác để app vẫn khởi động được với dữ liệu cũ.
            try
            {
                if (stagedMoved) File.Move(paths.Database, stagedDb, overwrite: true);
                foreach (var (from, to) in Enumerable.Reverse(moved))
                {
                    if (Directory.Exists(to)) { if (Directory.Exists(from)) Directory.Delete(from, recursive: true); Directory.Move(to, from); }
                    else File.Move(to, from, overwrite: true);
                }
                // Bản chờ đã bị lấy đi một phần: bỏ để người dùng dựng lại từ file sao lưu thay vì áp thiếu.
                Directory.Delete(pending, recursive: true);
            }
            catch (Exception revert) when (revert is IOException or UnauthorizedAccessException)
            {
                return $"Áp khôi phục thất bại ({e.Message}) và hoàn tác lỗi ({revert.Message}); dữ liệu cũ nằm ở các mục *{aside}.";
            }
            return $"Áp khôi phục thất bại, giữ dữ liệu cũ (hãy khôi phục lại từ file sao lưu): {e.Message}";
        }
    }

    /// <summary>Thư mục đích bị thay bằng bản trong sao lưu (bản cũ đổi tên giữ lại); sao lưu không có thư mục đó thì để nguyên.</summary>
    private static void ReplaceFolder(string staged, string target, string aside, List<(string From, string To)> moved)
    {
        if (!Directory.Exists(staged)) return;
        if (Directory.Exists(target))
        {
            Directory.Move(target, target + aside);
            moved.Add((target, target + aside));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(staged, target);
    }

    // Media chỉ thêm file còn thiếu: không ghi đè file người dùng đã có (cùng đường dẫn tương đối).
    private static void MergeMedia(string staged, string target)
    {
        if (!Directory.Exists(staged)) return;
        foreach (var file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(staged, file));
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file, destination);
        }
    }
}
