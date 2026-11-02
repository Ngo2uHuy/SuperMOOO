using SuperMOOO.Media.Subtitles;

namespace SuperMOOO.Media.Effects;

public sealed record ReupPreset(string Id, string Name, ReupEffectProfile Profile);

[Service]
public sealed class ReupPresetCatalog
{
    private readonly IReadOnlyList<ReupPreset> _all = EmbeddedJson.Load<List<ReupPreset>>("reup_presets.json");

    public IReadOnlyList<ReupPreset> All => _all;

    public ReupEffectProfile Get(string id) =>
        (_all.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
         ?? throw new KeyNotFoundException($"Không có preset reup '{id}'.")).Profile;

    /// <summary>
    /// Áp preset: thay TOÀN BỘ thông số hiệu ứng (canvas, video, âm thanh, đầu ra) nên không rò trạng thái từ preset trước (F11).
    /// Chỉ giữ tài nguyên người dùng đã chọn: logo, phụ đề, intro/outro, file giọng đọc và nhạc nền.
    /// </summary>
    public ReupEffectProfile Apply(string id, ReupEffectProfile current)
    {
        var preset = Get(id);
        return preset with
        {
            Logo = current.Logo,
            Subtitles = current.Subtitles,
            IntroOutro = current.IntroOutro,
            Audio = preset.Audio with
            {
                VoiceoverPath = current.Audio.VoiceoverPath,
                VoiceoverOffset = current.Audio.VoiceoverOffset,
                VoiceoverVolume = current.Audio.VoiceoverVolume,
                BgmPath = current.Audio.BgmPath,
                BgmVolume = current.Audio.BgmVolume,
                BgmLoop = current.Audio.BgmLoop
            }
        };
    }
}

public static class OutputNaming
{
    /// <summary>Mẫu tên file: {name} = tên nguồn không đuôi, {preset} = id preset, {date} = yyyyMMdd.</summary>
    public static string Resolve(string template, string sourcePath, string presetId, string extension = ".mp4")
    {
        var name = template.Replace("{name}", Path.GetFileNameWithoutExtension(sourcePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{preset}", presetId, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim() + extension;
    }
}
