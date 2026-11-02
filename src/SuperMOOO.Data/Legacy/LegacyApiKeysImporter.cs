using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuperMOOO.Data.Legacy;

/// <summary>api_keys.json cũ → secret(ai, &lt;provider&gt;#&lt;n&gt;); mỗi key qua ApiKeyValidator, sai định dạng thì lưu invalid_format.</summary>
public sealed partial class LegacyApiKeysImporter(ISecretStore secrets)
{
    [GeneratedRegex(@"[,;\r\n]+")]
    private static partial Regex PoolSeparators();

    /// <summary>Tên khoá cũ → provider: gemini_key / gemini_api_key → gemini; hf_token → hf; fal_key bỏ (không còn sinh video/ảnh AI).</summary>
    public static string? ProviderOf(string legacyName)
    {
        var name = legacyName.Trim().ToLowerInvariant();
        string? provider = name switch
        {
            "hf_token" or "hf_key" => "hf",
            _ when name.EndsWith("_api_key") => name[..^"_api_key".Length],
            _ when name.EndsWith("_key") => name[..^"_key".Length],
            _ => null
        };
        return provider is not null && ApiKeyValidator.Providers.Contains(provider) ? provider : null;
    }

    /// <summary>Nạp từng file theo thứ tự ưu tiên; file đầu tiên có provider nào thì provider đó không bị file sau ghi đè.</summary>
    public async Task<int> ImportAsync(IEnumerable<string> files, ImportReport report, CancellationToken ct)
    {
        var done = new HashSet<string>();
        var imported = 0;
        foreach (var file in files.Where(File.Exists))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct)).RootElement; }
            catch (JsonException)
            {
                report.Warnings.Add($"{Path.GetFileName(file)}: không phải JSON hợp lệ.");
                continue;
            }

            if (root.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var provider = ProviderOf(prop.Name);
                if (provider is null)
                {
                    if (!prop.Name.StartsWith("fal", StringComparison.OrdinalIgnoreCase))
                        report.Warnings.Add($"api_keys: bỏ qua khoá \"{prop.Name}\" (không nhận ra nhà cung cấp).");
                    continue;
                }

                var pool = PoolSeparators().Split(prop.Value.GetString() ?? "").Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
                if (pool.Count == 0 || !done.Add(provider)) continue;

                for (var i = 0; i < pool.Count; i++)
                {
                    var check = ApiKeyValidator.Validate(provider, pool[i]);
                    await secrets.SetAsync(new SecretKey("ai", $"{provider}#{i}"), pool[i], check.Status, ct);
                    if (!check.Ok) report.InvalidKeys.Add($"{provider}#{i}: {check.Message}");
                    report.Count("api_keys", true);
                    imported++;
                }
            }
        }

        return imported;
    }
}
