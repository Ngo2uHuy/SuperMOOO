using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperMOOO.AI.Catalog;

public sealed record ModelPreset(
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("price")] string Price,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("popularity")] string? Popularity = null,
    [property: JsonPropertyName("popularity_source")] string? PopularitySource = null);

/// <summary>21 preset đã duyệt cho văn bản/ASR/TTS (video/ảnh do ArtCraft đảm nhiệm nên không có).</summary>
[Service]
public sealed class ModelCatalog
{
    private sealed record Document([property: JsonPropertyName("checked")] string Checked, [property: JsonPropertyName("presets")] List<ModelPreset> Presets);

    public ModelCatalog()
    {
        var assembly = typeof(ModelCatalog).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("model_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var doc = JsonSerializer.Deserialize<Document>(stream)!;
        Checked = doc.Checked;
        Presets = doc.Presets;
    }

    public string Checked { get; }
    public IReadOnlyList<ModelPreset> Presets { get; }

    public IEnumerable<ModelPreset> For(string task) => Presets.Where(p => p.Task == task);

    public ModelPreset? Find(string task, string provider, string model) =>
        Presets.FirstOrDefault(p => p.Task == task && p.Provider == provider && p.Model == model);
}
