namespace SuperMOOO.AI.Keys;

/// <summary>Pool nhiều key mỗi provider (secret scope "ai", tên "provider#n"): xoay khi 429/hết hạn mức/KeyInvalid.</summary>
[Service]
public sealed class ApiKeyPool(ISecretStore secrets, TimeProvider? time = null)
{
    public const string Scope = "ai";
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QuotaCooldown = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldown = [];
    private readonly Dictionary<string, int> _current = [];

    public async Task<bool> HasKeyAsync(string provider, CancellationToken ct = default) =>
        (await ListAsync(provider, ct)).Any(i => i.Status == "ok");

    /// <summary>Key tuỳ chọn: có key thì xoay như ExecuteAsync, không có thì gọi không key (máy chủ tự host / Pollinations).</summary>
    public async Task<T> ExecuteOptionalAsync<T>(string provider, Func<string?, CancellationToken, Task<T>> call, CancellationToken ct) =>
        await HasKeyAsync(provider, ct) ? await ExecuteAsync(provider, (k, c) => call(k, c), ct) : await call(null, ct);

    /// <summary>Chạy call với key hiện tại; lỗi do key/hạn mức thì chuyển key kế, lỗi khác ném ngay.</summary>
    public async Task<T> ExecuteAsync<T>(string provider, Func<string, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        var mine = await ListAsync(provider, ct);
        var usable = mine.Where(i => i.Status == "ok").ToList();
        if (usable.Count == 0)
            throw mine.Count == 0
                ? new ProviderException(provider, ProviderErrorKind.KeyMissing, $"Nhập API key {provider} tại Cài đặt → API key.")
                : new ProviderException(provider, ProviderErrorKind.KeyInvalid, $"Mọi key {provider} đều sai định dạng hoặc bị từ chối; nhập key mới tại Cài đặt → API key.");

        int start;
        lock (_gate) start = _current.GetValueOrDefault(provider) % usable.Count;

        ProviderException? last = null;
        for (var i = 0; i < usable.Count; i++)
        {
            var index = (start + i) % usable.Count;
            var info = usable[index];
            if (InCooldown(info.Key.Name)) continue;

            var value = await secrets.GetAsync(info.Key, ct);
            if (string.IsNullOrEmpty(value)) continue;

            try
            {
                var result = await call(value, ct);
                lock (_gate) _current[provider] = index;
                return result;
            }
            catch (ProviderException e) when (e.Kind is ProviderErrorKind.RateLimited or ProviderErrorKind.QuotaExceeded or ProviderErrorKind.KeyInvalid)
            {
                last = e;
                if (e.Kind == ProviderErrorKind.KeyInvalid) await secrets.SetAsync(info.Key, value, "rejected", ct);
                else Cool(info.Key.Name, e.Kind == ProviderErrorKind.QuotaExceeded ? QuotaCooldown : RateLimitCooldown);
                lock (_gate) _current[provider] = index + 1;
            }
        }

        throw last ?? new ProviderException(provider, ProviderErrorKind.RateLimited, $"Mọi key {provider} đang bị giới hạn tốc độ; thử lại sau.");
    }

    private async Task<List<SecretInfo>> ListAsync(string provider, CancellationToken ct) =>
        (await secrets.ListAsync(Scope, ct))
        .Where(i => i.Key.Scope == Scope && (i.Key.Name == provider || i.Key.Name.StartsWith(provider + "#", StringComparison.Ordinal)))
        .OrderBy(i => i.Key.Name, StringComparer.Ordinal).ToList();

    private bool InCooldown(string name)
    {
        lock (_gate) return _cooldown.TryGetValue(name, out var until) && until > _time.GetUtcNow();
    }

    private void Cool(string name, TimeSpan span)
    {
        lock (_gate) _cooldown[name] = _time.GetUtcNow() + span;
    }
}
