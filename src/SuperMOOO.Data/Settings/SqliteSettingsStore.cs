using System.Collections.Concurrent;
using System.Text.Json;
using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Settings;

[Service(As = [typeof(ISettingsStore)])]
public sealed class SqliteSettingsStore(IDbConnectionFactory factory) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // Cache JSON (không cache object) để mỗi lần Get trả bản sao mới, caller sửa không ảnh hưởng store.
    private readonly ConcurrentDictionary<string, string?> _cache = new();

    public event EventHandler<string>? Changed;

    public T Get<T>(string section) where T : class, new()
    {
        var json = _cache.GetOrAdd(section, Load);
        return json is null ? new T() : JsonSerializer.Deserialize<T>(json, Json) ?? new T();
    }

    public async Task SetAsync<T>(string section, T value, CancellationToken ct = default) where T : class
    {
        var json = JsonSerializer.Serialize(value, Json);
        await using (var db = factory.Open())
        {
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO app_setting(key, value_json, updated_at) VALUES (@section, @json, @now)
                ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at
                """, new { section, json, now = DapperSetup.NowMs }, cancellationToken: ct));
        }

        _cache[section] = json;
        Changed?.Invoke(this, section);
    }

    private string? Load(string section)
    {
        using var db = factory.Open();
        return db.QuerySingleOrDefault<string>("SELECT value_json FROM app_setting WHERE key = @section", new { section });
    }
}
