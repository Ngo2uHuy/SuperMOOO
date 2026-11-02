using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuperMOOO.Data.Sqlite;
using Microsoft.Data.Sqlite;

using Bag = System.Collections.Generic.Dictionary<string, System.Text.Json.Nodes.JsonNode?>;

namespace SuperMOOO.Data.Legacy;

public sealed record LegacyImportOptions(string CrawFolder, string? OldAppFolder = null, string? FernetKeyPath = null);

/// <summary>
/// Nhập dữ liệu bản Craw cũ (§3.3–3.5). DB nguồn luôn được COPY sang thư mục tạm trước khi mở để SQLite không tạo
/// -wal/-shm trong thư mục gốc; không ghi gì vào thư mục nguồn. Chạy lại không nhân đôi (upsert theo khoá tự nhiên).
/// </summary>
[Service]
public sealed partial class LegacyImporter(IDbConnectionFactory target, ISecretStore secrets, ISettingsStore settings, AppPaths paths)
{
    private static readonly string[] MediaExtensions = [".mp4", ".mkv", ".mov", ".webm", ".avi", ".mp3", ".wav", ".m4a", ".flac", ".srt", ".ass"];
    private static readonly string[] DbSidecars = ["-wal", "-shm"];

    private sealed record Deferred(SecretKey Key, string Value, string Status);

    public async Task<ImportReport> ImportAsync(LegacyImportOptions options, CancellationToken ct = default)
    {
        var report = new ImportReport();
        var root = Path.GetFullPath(options.CrawFolder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Không có thư mục: {root}");
        var folders = new[] { root, options.OldAppFolder is { Length: > 0 } old ? Path.GetFullPath(old) : null }.OfType<string>().ToList();

        await new LegacyApiKeysImporter(secrets).ImportAsync(folders.Select(f => Path.Combine(f, "api_keys.json")), report, ct);
        await ImportBgmAsync(folders, report, ct);
        CopyReferenceFiles(root, report);
        await ImportVoiceFilesAsync(root, report, ct);
        await ImportJsonSectionsAsync(root, ct);

        var dbPath = Path.Combine(root, "streamgrabber.db");
        var deferred = new List<Deferred>();
        if (File.Exists(dbPath))
        {
            report.SourceHash = await HashAsync(dbPath, ct);
            var source = ReadLegacyDatabase(dbPath, report);
            await ImportSettingsAsync(source.Rows("settings"), report, ct);

            await using var db = target.Open();
            await using var tx = await db.BeginTransactionAsync(ct);
            ImportTables(db, tx, source, report, deferred, DecryptorFor(options));
            await tx.CommitAsync(ct);

            foreach (var d in deferred) await secrets.SetAsync(d.Key, d.Value, d.Status, ct);
        }
        else
        {
            report.Warnings.Add("Không thấy streamgrabber.db trong thư mục đã chọn; chỉ nhập các file cấu hình.");
        }

        await IndexOutputFilesAsync(root, report, ct);

        await settings.SetAsync("legacy_import", new Bag
        {
            ["sourceHash"] = report.SourceHash, ["at"] = report.At.ToString("O"),
            ["counts"] = JsonSerializer.SerializeToNode(report.Tables.ToDictionary(t => t.Key, t => t.Value.Imported))
        }, ct);
        await report.SaveAsync(paths.Legacy, ct);
        return report;
    }

    // ---- DB nguồn ----

    private sealed class LegacySource(Dictionary<string, List<LegacyRow>> tables)
    {
        public List<LegacyRow> Rows(string table) => tables.GetValueOrDefault(table) ?? [];
    }

    private static LegacySource ReadLegacyDatabase(string dbPath, ImportReport report)
    {
        var temp = Directory.CreateTempSubdirectory("aivs-legacy-");
        var copy = Path.Combine(temp.FullName, "legacy.db");
        try
        {
            File.Copy(dbPath, copy);
            foreach (var suffix in DbSidecars.Where(s => File.Exists(dbPath + s))) File.Copy(dbPath + suffix, copy + suffix);

            var tables = new Dictionary<string, List<LegacyRow>>(StringComparer.OrdinalIgnoreCase);
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite }.ToString()))
            {
                conn.Open();
                var integrity = Scalar(conn, "PRAGMA integrity_check");
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"DB cũ không toàn vẹn (integrity_check: {integrity}).");

                var names = new List<string>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) names.Add(r.GetString(0));
                }

                foreach (var name in names)
                {
                    var rows = new List<LegacyRow>();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT * FROM \"{name.Replace("\"", "\"\"")}\"";   // tên bảng lấy từ sqlite_master, không phải input
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        for (var i = 0; i < r.FieldCount; i++) values[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                        rows.Add(new LegacyRow(values));
                    }

                    tables[name] = rows;
                }
            }

            return new LegacySource(tables);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { temp.Delete(recursive: true); }
            catch (IOException) { report.Warnings.Add("Không xoá được thư mục tạm của bản sao DB."); }
        }
    }

    private static string? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static Func<string, string>? DecryptorFor(LegacyImportOptions options)
    {
        var keyPath = options.FernetKeyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamGrabber", "publishing", "credentials.key");
        if (!File.Exists(keyPath)) return null;
        var key = File.ReadAllText(keyPath).Trim();
        return token => FernetDecryptor.Decrypt(key, token);
    }

    // ---- settings ----

    private async Task ImportSettingsAsync(List<LegacyRow> rows, ImportReport report, CancellationToken ct)
    {
        var secretsToWrite = new Dictionary<SecretKey, (int Rank, string Value)>();
        var sections = new Dictionary<string, Bag>();
        var network = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var key = row.Str("key");
            var value = row.Str("value");
            if (key is null) { report.Count("settings", false); continue; }

            var map = LegacySettingsMap.Resolve(key);
            if (map.Kind == LegacyTargetKind.Skip || value is null) { report.Count("settings", false); continue; }

            switch (map.Kind)
            {
                case LegacyTargetKind.Network:
                    network[key] = value;
                    report.Count("settings", true);
                    break;
                case LegacyTargetKind.Setting:
                    if (!sections.TryGetValue(map.Scope, out var obj)) sections[map.Scope] = obj = settings.Get<Bag>(map.Scope);
                    // Chỉ hạn mức là số; chat id Telegram giữ chuỗi dù trông như số.
                    obj[map.Name] = map.Scope == "quota" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        ? JsonValue.Create(number) : JsonValue.Create(value);
                    report.Count("settings", true);
                    break;
                default:
                    if (map.Kind == LegacyTargetKind.Unknown) report.Warnings.Add($"settings: khoá lạ \"{key}\" được giữ ở secret(legacy).");
                    var secretKey = new SecretKey(map.Scope, map.Name);
                    var better = !secretsToWrite.TryGetValue(secretKey, out var current) || map.Rank > current.Rank;
                    if (better) secretsToWrite[secretKey] = (map.Rank, value);
                    report.Count("settings", better);
                    break;
            }
        }

        foreach (var (key, (_, value)) in secretsToWrite) await secrets.SetAsync(key, value, ct: ct);
        foreach (var (section, obj) in sections) await settings.SetAsync(section, obj, ct);
        await ImportNetworkAsync(network, report, ct);
    }

    private async Task ImportNetworkAsync(Dictionary<string, string> raw, ImportReport report, CancellationToken ct)
    {
        var net = settings.Get<NetworkSettings>("network");
        var changed = false;

        if (raw.TryGetValue("crawler_network_v1", out var json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var p in doc.RootElement.TryGetProperty("profiles", out var list) ? list.EnumerateArray() : [])
                {
                    var name = Prop(p, "name") ?? "Proxy cũ";
                    if ((Prop(p, "kind") ?? "proxy") != "proxy")
                    {
                        report.Warnings.Add($"network: bỏ hồ sơ \"{name}\" (VPN không còn được nhập).");
                        continue;
                    }

                    string? url;
                    try { url = Prop(p, "proxy_secret") is { } sealedValue ? Unseal(sealedValue) : Prop(p, "proxy"); }
                    catch (Exception e) when (e is CryptographicException or FormatException)
                    {
                        report.Warnings.Add($"network: không giải mã được proxy của hồ sơ \"{name}\" (khác tài khoản Windows?).");
                        continue;
                    }

                    var id = Prop(p, "id") is { Length: 32 } legacyId ? legacyId : Guid.NewGuid().ToString("N");
                    if (!await AddProxyAsync(net, id, name, url, report, ct)) continue;
                    if (p.TryGetProperty("favorite", out var fav) && fav.ValueKind == JsonValueKind.True && !net.Favorites.Contains(id)) net.Favorites.Add(id);
                    changed = true;
                }
            }
            catch (JsonException)
            {
                report.Warnings.Add("network: crawler_network_v1 không phải JSON hợp lệ.");
            }
        }

        if (raw.TryGetValue("proxy", out var plain) && await AddProxyAsync(net, "legacy-proxy", "Proxy cũ", plain, report, ct)) changed = true;
        if (changed) await settings.SetAsync("network", net, ct);
    }

    private async Task<bool> AddProxyAsync(NetworkSettings net, string id, string name, string? url, ImportReport report, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Port <= 0)
        {
            report.Warnings.Add($"network: proxy của \"{name}\" không đọc được.");
            return false;
        }

        var scheme = uri.Scheme == "socks5h" ? "socks5" : uri.Scheme;
        if (scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        {
            report.Warnings.Add($"network: proxy \"{name}\" dùng giao thức {uri.Scheme} chưa hỗ trợ.");
            return false;
        }

        var parts = uri.UserInfo.Split(':', 2);
        var user = parts[0].Length > 0 ? Uri.UnescapeDataString(parts[0]) : null;
        net.Profiles.RemoveAll(p => p.Id == id);
        net.Profiles.Add(new ProxyProfile(id, name, scheme, uri.Host, uri.Port, user));
        if (parts.Length == 2) await secrets.SetAsync(new SecretKey($"network:{id}", "password"), Uri.UnescapeDataString(parts[1]), ct: ct);
        report.Count("network_profiles", true);
        return true;
    }

    /// <summary>Proxy cũ mã hoá DPAPI không entropy; giải mã rồi để ISecretStore mã hoá lại với entropy của app.</summary>
    private static string Unseal(string base64) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser));

    private static string? Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    // ---- file JSON ----

    private async Task ImportBgmAsync(List<string> folders, ImportReport report, CancellationToken ct)
    {
        var file = folders.Select(f => Path.Combine(f, "bgm_config.json")).FirstOrDefault(File.Exists);
        if (file is null) return;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
            var e = doc.RootElement;
            var files = e.TryGetProperty("bgm_files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [];
            foreach (var missing in files.Where(x => !File.Exists(x))) report.MissingFiles.Add(missing);

            await settings.SetAsync("audio.bgm", new Bag
            {
                ["Files"] = new JsonArray(files.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["Volume"] = e.TryGetProperty("bgm_volume", out var v) && v.TryGetDouble(out var vol) ? vol : 0.12,
                ["Enabled"] = e.TryGetProperty("enable_bgm", out var en) && en.ValueKind == JsonValueKind.True
            }, ct);
            report.Count("bgm_config", true);
        }
        catch (JsonException)
        {
            report.Warnings.Add("bgm_config.json không phải JSON hợp lệ.");
        }
    }

    /// <summary>Voice.json không import vào catalog (không engine nào dùng); chỉ giữ bản tham khảo.</summary>
    private void CopyReferenceFiles(string root, ImportReport report)
    {
        var voice = Path.Combine(root, "Voice.json");
        if (!File.Exists(voice)) return;
        Directory.CreateDirectory(paths.Legacy);
        File.Copy(voice, Path.Combine(paths.Legacy, "Voice.json"), overwrite: true);
        report.Count("voice_json", true);
    }

    private async Task ImportVoiceFilesAsync(string root, ImportReport report, CancellationToken ct)
    {
        foreach (var file in new[] { Path.Combine(root, "cloned_voices.json"), Path.Combine(root, "data", "native_tts", "voices", "registry.json") })
        {
            if (!File.Exists(file)) continue;
            JsonElement list;
            try
            {
                list = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct)).RootElement;
                if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("voices", out var inner)) list = inner;
            }
            catch (JsonException)
            {
                report.Warnings.Add($"{Path.GetFileName(file)}: không phải JSON hợp lệ.");
                continue;
            }

            var items = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList()
                : list.ValueKind == JsonValueKind.Object ? list.EnumerateObject().Select(p => p.Value).ToList() : [];
            foreach (var item in items.Where(i => i.ValueKind == JsonValueKind.Object))
                await ImportVoiceAsync(item, root, report, ct);
        }
    }

    private async Task ImportVoiceAsync(JsonElement item, string root, ImportReport report, CancellationToken ct)
    {
        var id = Prop(item, "id") ?? Prop(item, "name");
        if (id is null) { report.Count("voice_profile", false); return; }

        var wav = Prop(item, "wav") ?? Prop(item, "path") ?? Prop(item, "ref_path") ?? Prop(item, "audio");
        var text = Prop(item, "ref_text") ?? Prop(item, "text") ?? Prop(item, "transcript");
        string? refPath = null;
        if (wav is not null)
        {
            var source = Path.IsPathRooted(wav) ? wav : Path.Combine(root, wav);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(paths.Voices);
                refPath = Path.Combine(paths.Voices, $"{id}{Path.GetExtension(source)}");
                File.Copy(source, refPath, overwrite: true);
            }
            else report.MissingFiles.Add(wav);
        }

        await using var db = target.Open();
        var inserted = await db.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO voice_profile(id, provider, name, language, ref_path, ref_text, status, created_at)
            VALUES (@id, 'native.omnivoice', @name, @language, @refPath, @text, @status, @now)
            """, new
            {
                id = $"legacy-{id}", name = Prop(item, "name") ?? id, language = Prop(item, "language"), refPath, text,
                status = text is null ? "needs_transcript" : "ready", now = DapperSetup.NowMs
            }, cancellationToken: ct));
        report.Count("voice_profile", inserted > 0);
    }

    /// <summary>native_tts\config.json và ai_models\profile.json → section; khoá snake_case đổi sang PascalCase, bỏ video_*/image_*.</summary>
    private async Task ImportJsonSectionsAsync(string root, CancellationToken ct)
    {
        await CopySectionAsync(Path.Combine(root, "data", "native_tts", "config.json"), "tts.native", _ => true, ct);
        await CopySectionAsync(Path.Combine(root, "data", "ai_models", "profile.json"), "ai.models",
            k => !k.StartsWith("video_", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("image_", StringComparison.OrdinalIgnoreCase), ct);
    }

    private async Task CopySectionAsync(string file, string section, Func<string, bool> keep, CancellationToken ct)
    {
        if (!File.Exists(file)) return;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var obj = settings.Get<Bag>(section);
            foreach (var p in doc.RootElement.EnumerateObject().Where(p => keep(p.Name)))
                obj[Pascal(p.Name)] = JsonNode.Parse(p.Value.GetRawText());
            await settings.SetAsync(section, obj, ct);
        }
        catch (JsonException) { /* file hỏng: bỏ qua, người dùng cấu hình lại */ }
    }

    private static string Pascal(string name) =>
        string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));

    // ---- file output cũ → Kho file (kind legacy); job.json/dự án dở không migrate (Quyết định #7) ----

    private async Task IndexOutputFilesAsync(string root, ImportReport report, CancellationToken ct)
    {
        var output = Path.Combine(root, "output");
        if (!Directory.Exists(output)) return;

        await using var db = target.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                     .Where(f => MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var inserted = await db.ExecuteAsync(
                "INSERT OR IGNORE INTO media_file(path, kind, operation, size_bytes, created_at) VALUES (@path, 'legacy', 'output', @size, @at)",
                new { path = info.FullName, size = info.Length, at = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() }, tx);
            report.Count("output_files", inserted > 0);
        }

        await tx.CommitAsync(ct);
    }
}
