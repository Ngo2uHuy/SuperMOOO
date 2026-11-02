using SuperMOOO.Data.Repositories.Platforms;

namespace SuperMOOO.Social.Platforms;

public sealed record PlatformStatus(PlatformInfo Platform, string AuthType, string Status, string? Message, DateTimeOffset? CheckedAt);

/// <summary>Cookie/token/API key theo nền tảng (DPAPI qua ISecretStore) + trạng thái hiển thị ở bảng platform_session.</summary>
[Service]
public sealed class PlatformSessionService(ISecretStore secrets, PlatformSessionRepository sessions)
{
    private const string NotConfigured = "Chưa cấu hình cookie/API key.";

    public async Task<PlatformStatus> SaveAsync(string platform, string value, string authType = PlatformSecrets.Cookie,
        CancellationToken ct = default)
    {
        var info = PlatformRegistry.Get(platform);
        var name = authType.Trim().ToLowerInvariant();
        if (!info.SecretNames.Contains(name))
            throw new ArgumentException($"{info.Name} không có loại thông tin đăng nhập \"{authType}\" (chọn: {string.Join(", ", info.SecretNames)}).", nameof(authType));
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Giá trị trống.", nameof(value));

        await secrets.SetAsync(PlatformSecrets.Key(info.Id, name), value.Trim(), ct: ct);
        return await CheckAsync(info.Id, ct);
    }

    /// <summary>Chỉ xác nhận đã có thông tin lưu; không gọi nền tảng để xác thực phiên.</summary>
    public async Task<PlatformStatus> CheckAsync(string platform, CancellationToken ct = default)
    {
        var info = PlatformRegistry.Get(platform);
        var stored = (await secrets.ListAsync(PlatformSecrets.Scope(info.Id), ct))
            .Where(s => s.Key.Scope == PlatformSecrets.Scope(info.Id)).Select(s => s.Key.Name).ToHashSet();
        var found = info.SecretNames.FirstOrDefault(stored.Contains);

        var record = found is null
            ? await sessions.UpsertAsync(info.Id, "none", "not_configured", NotConfigured, ct: ct)
            : await sessions.UpsertAsync(info.Id, found is "cookie" or "sessdata" ? "cookie" : "token", "configured",
                "Đã lưu thông tin đăng nhập; chưa xác thực phiên với nền tảng.", ct: ct);
        return ToStatus(info, record);
    }

    public async Task<PlatformStatus> ClearAsync(string platform, CancellationToken ct = default)
    {
        var info = PlatformRegistry.Get(platform);
        foreach (var name in info.SecretNames) await secrets.DeleteAsync(PlatformSecrets.Key(info.Id, name), ct);
        return ToStatus(info, await sessions.UpsertAsync(info.Id, "none", "not_configured", "Đã xóa thông tin đăng nhập.", ct: ct));
    }

    public async Task<IReadOnlyList<PlatformStatus>> ListAsync(CancellationToken ct = default)
    {
        var saved = (await sessions.ListAsync(ct)).ToDictionary(r => r.Platform);
        return PlatformRegistry.All.Select(p => saved.TryGetValue(p.Id, out var r)
            ? ToStatus(p, r)
            : new PlatformStatus(p, "none", "not_configured", NotConfigured, null)).ToList();
    }

    public Task<string?> GetSecretAsync(string platform, string name, CancellationToken ct = default) =>
        secrets.GetAsync(PlatformSecrets.Key(PlatformRegistry.Get(platform).Id, name), ct);

    /// <summary>Header Cookie/Authorization cho yt-dlp và crawler; Facebook/Instagram dùng token Meta chung khi chưa có token riêng.</summary>
    public async Task<IReadOnlyDictionary<string, string>> BuildHeadersAsync(string platform, CancellationToken ct = default)
    {
        var info = PlatformRegistry.Get(platform);
        var headers = new Dictionary<string, string>();

        var token = await GetSecretAsync(info.Id, PlatformSecrets.Token, ct);
        if (string.IsNullOrWhiteSpace(token) && info.Id == "twitter") token = await GetSecretAsync(info.Id, "bearer_token", ct);
        if (string.IsNullOrWhiteSpace(token) && info.Id is "facebook" or "instagram") token = await GetSecretAsync("meta", PlatformSecrets.Token, ct);
        if (!string.IsNullOrWhiteSpace(token)) headers["Authorization"] = "Bearer " + token;

        var cookie = await GetSecretAsync(info.Id, PlatformSecrets.Cookie, ct);
        if (!string.IsNullOrWhiteSpace(cookie)) headers["Cookie"] = cookie;
        return headers;
    }

    private static PlatformStatus ToStatus(PlatformInfo info, PlatformSessionRecord r) =>
        new(info, r.AuthType, r.Status, r.Message, r.CheckedAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null);
}
