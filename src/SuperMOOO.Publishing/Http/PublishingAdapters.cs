namespace SuperMOOO.Publishing.Http;

/// <summary>Token/khoá đăng bài trong <see cref="ISecretStore"/> (DPAPI): khoá CenterPost ("oauth-token:&lt;id&gt;"…) thành tên trong scope "publishing".</summary>
[Service(As = [typeof(ITokenVault)])]
public sealed class SecretTokenVault(ISecretStore secrets) : ITokenVault
{
    private const string Scope = "publishing";

    public Task SaveAsync(string key, string value, CancellationToken ct = default) => secrets.SetAsync(new SecretKey(Scope, key), value, ct: ct);

    public Task<string?> TryGetAsync(string key, CancellationToken ct = default) => secrets.GetAsync(new SecretKey(Scope, key), ct);

    public Task DeleteAsync(string key, CancellationToken ct = default) => secrets.DeleteAsync(new SecretKey(Scope, key), ct);
}

public static class PublishAudit
{
    /// <summary>Chữ ký audit của CenterPost ghi vào audit_log chung (source = "publishing:&lt;actor&gt;").</summary>
    public static Task WriteAsync(this IAuditLog audit, string actor, string action, string target, string? detail, CancellationToken ct) =>
        audit.AddAsync("publishing:" + actor, action, target, detail ?? "ok", ct: ct);
}
