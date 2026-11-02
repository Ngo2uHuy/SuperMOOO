using System.Reflection;

namespace SuperMOOO.Infrastructure.Conventions;

public static class AssemblyCatalog
{
    /// <summary>Nạp mọi SuperMOOO.*.dll cạnh app (trừ test) để convention quét được cả project chưa được tham chiếu trực tiếp.</summary>
    public static IReadOnlyList<Assembly> LoadAppAssemblies(string? directory = null)
    {
        directory ??= AppContext.BaseDirectory;
        var loaded = AppDomain.CurrentDomain.GetAssemblies().ToDictionary(a => a.GetName().Name!, StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(directory, "SuperMOOO.*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || loaded.ContainsKey(name)) continue;
            loaded[name] = Assembly.LoadFrom(file);
        }

        return loaded.Where(kv => kv.Key.StartsWith("SuperMOOO.", StringComparison.OrdinalIgnoreCase)
                                  && !kv.Key.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Value).ToList();
    }
}
