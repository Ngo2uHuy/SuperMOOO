using System.Text.Json;

namespace SuperMOOO.Infrastructure.Network;

public sealed record ProxyEntry(ProxyProfile Profile, bool Favorite);

/// <summary>Hồ sơ proxy lưu ở section "network"; mật khẩu chỉ nằm trong ISecretStore (scope network:&lt;id&gt;).</summary>
[Service]
public sealed class ProxyProfileService(ISettingsStore settings, ISecretStore secrets)
{
    private const string Section = "network";
    private static readonly string[] Schemes = ["http", "https", "socks4", "socks4a", "socks5"];

    public static SecretKey PasswordKey(string profileId) => new($"network:{profileId}", "password");

    public Task<IReadOnlyList<ProxyEntry>> ListAsync(CancellationToken ct = default)
    {
        var s = settings.Get<NetworkSettings>(Section);
        IReadOnlyList<ProxyEntry> list = s.Profiles
            .Select(p => new ProxyEntry(p, s.Favorites.Contains(p.Id)))
            .OrderByDescending(e => e.Favorite).ThenBy(e => e.Profile.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return Task.FromResult(list);
    }

    public async Task<ProxyProfile> SaveAsync(ProxyProfile profile, string? password, CancellationToken ct = default)
    {
        Validate(profile);
        profile = profile with { Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id };

        var s = settings.Get<NetworkSettings>(Section);
        s.Profiles.RemoveAll(p => p.Id == profile.Id);
        s.Profiles.Add(profile);
        await settings.SetAsync(Section, s, ct);
        if (!string.IsNullOrEmpty(password)) await secrets.SetAsync(PasswordKey(profile.Id), password, ct: ct);
        return profile;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var s = settings.Get<NetworkSettings>(Section);
        if (s.Profiles.RemoveAll(p => p.Id == id) == 0) return false;
        s.Favorites.Remove(id);
        await settings.SetAsync(Section, s, ct);
        await secrets.DeleteAsync(PasswordKey(id), ct);
        return true;
    }

    public async Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default)
    {
        var s = settings.Get<NetworkSettings>(Section);
        if (s.Profiles.All(p => p.Id != id)) throw new KeyNotFoundException($"Không có hồ sơ proxy '{id}'.");
        s.Favorites.Remove(id);
        if (favorite) s.Favorites.Add(id);
        await settings.SetAsync(Section, s, ct);
    }

    /// <summary>
    /// Nhập hàng loạt: mảng JSON, hoặc mỗi dòng một proxy dạng URI (`socks5://user:pass@host:1080`) hay CSV
    /// (`tên,scheme,host,port,user,password`). Sai dòng nào thì không lưu gì cả. Trả số hồ sơ đã lưu.
    /// </summary>
    public async Task<int> ImportAsync(string text, CancellationToken ct = default)
    {
        var parsed = text.TrimStart().StartsWith('[') ? ParseJson(text) : ParseLines(text);
        foreach (var (profile, _) in parsed) Validate(profile);
        foreach (var (profile, password) in parsed) await SaveAsync(profile, password, ct);
        return parsed.Count;
    }

    private static List<(ProxyProfile, string?)> ParseJson(string text)
    {
        var items = JsonSerializer.Deserialize<List<ImportItem>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new FormatException("JSON proxy rỗng.");
        return items.Select(i => (new ProxyProfile("", i.Name ?? $"{i.Host}:{i.Port}", i.Scheme ?? "http", i.Host ?? "", i.Port, i.Username), i.Password)).ToList();
    }

    private static List<(ProxyProfile, string?)> ParseLines(string text)
    {
        var result = new List<(ProxyProfile, string?)>();
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            try
            {
                if (line.Contains("://"))
                {
                    var uri = new Uri(line);
                    var user = uri.UserInfo.Split(':', 2);
                    result.Add((new ProxyProfile("", $"{uri.Host}:{uri.Port}", uri.Scheme, uri.Host, uri.Port,
                        user[0] is { Length: > 0 } u ? Uri.UnescapeDataString(u) : null),
                        user.Length > 1 ? Uri.UnescapeDataString(user[1]) : null));
                    continue;
                }

                var c = line.Split(',', StringSplitOptions.TrimEntries);
                if (c[0].Equals("name", StringComparison.OrdinalIgnoreCase)) continue;      // dòng tiêu đề CSV
                var at = (int i) => c.ElementAtOrDefault(i) is { Length: > 0 } v ? v : null;
                result.Add((new ProxyProfile("", at(0) ?? "", at(1) ?? "http", at(2) ?? "", int.Parse(at(3) ?? "0"), at(4)), at(5)));
            }
            catch (Exception ex) when (ex is FormatException or UriFormatException or OverflowException)
            {
                throw new FormatException($"Dòng {n + 1} không đọc được: {ex.Message}", ex);
            }
        }

        return result;
    }

    private static void Validate(ProxyProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Host)) throw new ArgumentException("Thiếu host proxy.");
        if (p.Port is < 1 or > 65535) throw new ArgumentException($"Cổng không hợp lệ: {p.Port}.");
        if (!Schemes.Contains(p.Scheme, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Giao thức '{p.Scheme}' không hỗ trợ (dùng {string.Join(", ", Schemes)}).");
    }

    private sealed record ImportItem(string? Name, string? Scheme, string? Host, int Port, string? Username, string? Password);
}
