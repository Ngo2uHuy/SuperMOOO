using System.Reflection;

namespace SuperMOOO.Media.Subtitles;

public sealed record SubtitleStylePreset(string Id, string Name, SubtitleStyle Style);

[Service]
public sealed class SubtitleStylePresets
{
    private readonly IReadOnlyList<SubtitleStylePreset> _all;

    public SubtitleStylePresets() => _all = EmbeddedJson.Load<List<SubtitleStylePreset>>("subtitle_styles.json");

    public IReadOnlyList<SubtitleStylePreset> All => _all;

    public SubtitleStyle Get(string id) =>
        (_all.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
         ?? throw new KeyNotFoundException($"Không có preset phụ đề '{id}'.")).Style;
}

internal static class EmbeddedJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static T Load<T>(string file)
    {
        var assembly = typeof(EmbeddedJson).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<T>(stream, Options)!;
    }
}
