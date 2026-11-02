using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuperMOOO.Infrastructure.Conventions;
using SuperMOOO.Infrastructure.Shell;

namespace SuperMOOO.Infrastructure.Pipelines;

/// <summary>Nạp pipeline JSON: file cùng tên trong DataRoot\pipelines\ ghi đè bản embedded (`*.Definitions.&lt;id&gt;.json`).</summary>
[Service]
public sealed partial class PipelineDefinitionLoader
{
    [GeneratedRegex(@"\.Definitions\.(?<id>[^.]+)\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceName();

    private readonly AppPaths _paths;
    private readonly Dictionary<string, (Assembly Assembly, string Resource)> _embedded = new(StringComparer.OrdinalIgnoreCase);

    public PipelineDefinitionLoader(AppPaths paths, IReadOnlyList<Assembly>? assemblies = null)
    {
        _paths = paths;
        foreach (var assembly in assemblies ?? AssemblyCatalog.LoadAppAssemblies())
            foreach (var name in assembly.GetManifestResourceNames())
                if (ResourceName().Match(name) is { Success: true } m)
                    _embedded[m.Groups["id"].Value] = (assembly, name);
    }

    public IReadOnlyList<string> Ids =>
        _embedded.Keys.Concat(Directory.Exists(_paths.Pipelines)
                ? Directory.EnumerateFiles(_paths.Pipelines, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
                : [])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    public PipelineDefinition Get(string id)
    {
        var overridePath = SafePath.Combine(_paths.Pipelines, SafePath.SanitizeFileName(id) + ".json");
        var (json, source) = File.Exists(overridePath)
            ? (File.ReadAllText(overridePath), overridePath)
            : _embedded.TryGetValue(id, out var res)
                ? (ReadResource(res.Assembly, res.Resource), res.Resource)
                : throw new KeyNotFoundException($"Không có pipeline '{id}'.");

        PipelineDefinition? definition;
        try { definition = JsonSerializer.Deserialize<PipelineDefinition>(json, PipelineJson.Options); }
        catch (JsonException ex) { throw new InvalidDataException($"Pipeline '{id}' ({source}) sai định dạng: {ex.Message}", ex); }

        if (definition is null || !string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase) || definition.Steps is not { Count: > 0 })
            throw new InvalidDataException($"Pipeline '{id}' ({source}) thiếu id khớp tên hoặc không có bước nào.");
        return definition;
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }
}

internal static class PipelineJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
