using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;

namespace SuperMOOO.Media.Effects;

[Service]
public sealed class VoiceEffectPresets
{
    private static readonly Lazy<VoiceEffectPresets> Shared = new(() => new VoiceEffectPresets());
    private readonly IReadOnlyList<VoiceEffect> _all = EmbeddedJson.Load<List<VoiceEffect>>("voice_effects.json");

    public static VoiceEffectPresets Default => Shared.Value;

    public IReadOnlyList<VoiceEffect> All => _all;

    public VoiceEffect Get(string? id) =>
        string.IsNullOrWhiteSpace(id) ? _all[0]
        : _all.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
          ?? throw new KeyNotFoundException($"Không có hiệu ứng giọng '{id}'.");

    /// <summary>
    /// Chuỗi filter audio: resample 48 kHz trước rồi đổi cao độ bằng asetrate theo đúng 48 kHz (không giả định 44.1 kHz),
    /// hệ số ghi 6 chữ số, InvariantCulture. extraPitch cộng thêm cao độ người dùng chọn (±6).
    /// </summary>
    public string BuildFilter(VoiceEffect effect, double extraPitchSemitones = 0, int sampleRate = 48000)
    {
        var chain = new List<string> { "aresample=" + sampleRate };
        var semitones = effect.PitchSemitones + extraPitchSemitones;
        if (Math.Abs(semitones) > 0.0001)
        {
            var k = Math.Pow(2, semitones / 12);
            chain.Add($"asetrate={sampleRate}*{Fmt.N(k)}");
            chain.Add("aresample=" + sampleRate);
            chain.Add(Fmt.TempoChain(1 / k));
        }

        if (Math.Abs(effect.Tempo - 1) > 0.0001) chain.Add(Fmt.TempoChain(effect.Tempo));
        if (!string.IsNullOrWhiteSpace(effect.FfmpegFilter)) chain.Add(effect.FfmpegFilter);
        return string.Join(',', chain);
    }

    /// <summary>Filter cho id preset + cao độ phụ; null nếu không đổi gì (để khỏi resample thừa).</summary>
    public string? BuildFilter(string? effectId, double extraPitchSemitones, int sampleRate = 48000)
    {
        var effect = Get(effectId);
        return effect.Id == "None" && Math.Abs(extraPitchSemitones) <= 0.0001 ? null : BuildFilter(effect, extraPitchSemitones, sampleRate);
    }
}

[Service]
public sealed class VoiceEffectService(FfmpegRunner ffmpeg, VoiceEffectPresets presets, IMediaProbe probe)
{
    /// <summary>Áp hiệu ứng giọng cho file audio (hoặc video: giữ nguyên hình, chỉ đổi tiếng).</summary>
    public async Task<string> ApplyAsync(string input, string output, string? effectId, double extraPitchSemitones,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (Math.Abs(extraPitchSemitones) > 12) throw new ArgumentOutOfRangeException(nameof(extraPitchSemitones), "Cao độ tối đa ±12 nửa cung.");
        var info = await probe.ProbeAsync(input, ct);
        if (info.Audio is null) throw new MediaProbeException("File nguồn không có âm thanh.");

        var filter = presets.BuildFilter(effectId, extraPitchSemitones) ?? "anull";
        var args = new List<string> { "-i", input };
        if (info.Video is not null) args.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
        args.AddRange(["-map", "0:a:0", "-af", filter]);
        args.AddRange(CodecFor(Path.GetExtension(output)));
        await ffmpeg.RunAsync(new FfmpegJob(args, output, info.Duration, Label: "Đổi giọng"), progress, ct);
        return output;
    }

    private static string[] CodecFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".wav" => ["-c:a", "pcm_s16le"],
        ".mp3" => ["-c:a", "libmp3lame", "-b:a", "256k"],
        ".flac" => ["-c:a", "flac"],
        ".ogg" => ["-c:a", "libvorbis"],
        _ => ["-c:a", "aac", "-b:a", "256k"]
    };
}
