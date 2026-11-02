using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Secrets;

[Service(As = [typeof(ISecretStore)])]
public sealed class DpapiSecretStore(IDbConnectionFactory factory, ILogger<DpapiSecretStore> log) : ISecretStore
{
    // Entropy cố định của app: dữ liệu DPAPI copy sang app khác cùng user vẫn không giải mã được.
    // Giữ tên cũ: đổi entropy thì key đã lưu không giải mã được nữa.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIVideoStudio.secret.v1");

    private sealed class Row
    {
        public string Scope { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Hint { get; set; }
        public string Status { get; set; } = "";
        public long UpdatedAt { get; set; }
    }

    public async Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var cipher = await db.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
            "SELECT cipher FROM secret WHERE scope = @Scope AND name = @Name", key, cancellationToken: ct));
        if (cipher is null) return null;

        byte[] plain;
        try { plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException)
        {
            // Dữ liệu khôi phục từ máy/tài khoản Windows khác: coi như chưa có, nơi gọi tự yêu cầu nhập/đăng nhập lại.
            log.LogWarning("Không giải mã được secret {Scope}/{Name} (DPAPI của máy hoặc tài khoản Windows khác); coi như chưa có.", key.Scope, key.Name);
            return null;
        }
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SetAsync(SecretKey key, string value, string status = "ok", CancellationToken ct = default)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        byte[] cipher;
        try { cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }

        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO secret(scope, name, cipher, hint, status, updated_at)
            VALUES (@Scope, @Name, @cipher, @hint, @status, @now)
            ON CONFLICT(scope, name) DO UPDATE SET
              cipher = excluded.cipher, hint = excluded.hint, status = excluded.status, updated_at = excluded.updated_at
            """,
            new { key.Scope, key.Name, cipher, hint = BuildHint(value), status, now = DapperSetup.NowMs },
            cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM secret WHERE scope = @Scope AND name = @Name", key, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<SecretInfo>> ListAsync(string? scopePrefix = null, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        // substr thay LIKE để scope chứa '_' / '%' không bị hiểu thành ký tự đại diện.
        var rows = await db.QueryAsync<Row>(new CommandDefinition("""
            SELECT scope, name, hint, status, updated_at AS UpdatedAt FROM secret
            WHERE @prefix IS NULL OR substr(scope, 1, length(@prefix)) = @prefix
            ORDER BY scope, name
            """, new { prefix = scopePrefix }, cancellationToken: ct));
        return rows.Select(r => new SecretInfo(new SecretKey(r.Scope, r.Name), r.Hint ?? "••••", r.Status,
            DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt))).ToList();
    }

    private static string BuildHint(string value) => value.Length > 4 ? "••••" + value[^4..] : "••••";
}
