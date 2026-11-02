namespace SuperMOOO.Core.Persistence;

public sealed record SecretKey(string Scope, string Name);
// ("ai","gemini#0") ("platform:tiktok","cookie") ("oauth:youtube","client") ("account:<id>","token") ("network:<id>","password") ("notify:telegram","bot_token")
public sealed record SecretInfo(SecretKey Key, string Hint, string Status, DateTimeOffset UpdatedAt);  // Hint "••••abcd"; Status ok | invalid_format | rejected

public interface ISecretStore
{
    Task<string?> GetAsync(SecretKey key, CancellationToken ct = default);
    Task SetAsync(SecretKey key, string value, string status = "ok", CancellationToken ct = default);
    Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default);
    Task<IReadOnlyList<SecretInfo>> ListAsync(string? scopePrefix = null, CancellationToken ct = default);
}

public interface ISettingsStore
{
    T Get<T>(string section) where T : class, new();   // "paths" "tools" "ai.models" "tts.native" "tts.edge" "audio.bgm" "audio.mix" "network" "download" "notify.telegram" "quota" "social.tiktok" "worker" "ui"
    Task SetAsync<T>(string section, T value, CancellationToken ct = default) where T : class;
    event EventHandler<string>? Changed;
}

public interface IDbConnectionFactory { DbConnection Open(); }
