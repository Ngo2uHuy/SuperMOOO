using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Media.Audio;

/// <summary>Audio nạp từ người dùng: luôn probe, thiếu âm thanh hoặc Duration = 0 là lỗi rõ ràng (F02).</summary>
public sealed record AudioInput(string Path, TimeSpan Duration)
{
    public static async Task<AudioInput> FromFileAsync(string path, IMediaProbe probe, CancellationToken ct)
    {
        var info = await probe.ProbeAsync(path, ct);
        if (info.Audio is null) throw new MediaProbeException($"{System.IO.Path.GetFileName(path)} không có luồng âm thanh.");
        if (info.Duration <= TimeSpan.Zero) throw new MediaProbeException($"Không xác định được thời lượng của {System.IO.Path.GetFileName(path)}.");
        return new AudioInput(path, info.Duration);
    }
}

public enum MixRole { Voice, Original, Bgm }

/// <summary>Stream = "1:a" (chỉ số input ffmpeg). Delay âm = cắt bỏ phần đầu thay vì đệm.</summary>
public sealed record MixInput(string Stream, MixRole Role, double Volume = 1, TimeSpan Delay = default, string? PreFilter = null);

public sealed record MixGraph(string Filter, string OutputLabel);

public sealed record MixRequest(string? VoicePath, string? OriginalPath, string? BgmPath, string Output,
    double OriginalVolume = 0.15, double VoiceVolume = 1, double BgmVolume = 0.12, bool SmartDucking = true, TimeSpan? Duration = null,
    double DuckDb = AudioMixer.DuckDb);

[Service]
public sealed class AudioMixer(FfmpegRunner ffmpeg, IMediaProbe probe)
{
    public const int SampleRate = 48000;

    /// <summary>Độ hạ tiếng gốc/BGM khi có giọng lồng tiếng; nền đã tách giọng chỉ cần hạ nhẹ để giữ nhạc.</summary>
    public const double DuckDb = -11, LightDuckDb = -5;

    // Khoá là tiếng gốc (thêm nhạc nền vào video): nội dung lẫn nhạc/lời nên giữ nén theo mức như Craw (F27).
    private const string Ducking = "sidechaincompress=threshold=0.03:ratio=8:attack=20:release=250";

    // Gain khi khoá đã chuẩn hoá vượt ngưỡng ~30 dB với ratio 20: 10^(-0.95*30/20).
    private const double SaturatedGain = 0.0376;

    /// <summary>
    /// Ducking theo giọng, độ sâu cố định bất kể giọng to/nhỏ: khoá qua gate -45 dBFS (bỏ nhiễu nền), +40 dB rồi cắt cứng nên lúc có lời
    /// luôn ở 0 dBFS; compressor ratio 20 khi đó gần như tắt hẳn và mix pha lại tín hiệu khô để còn đúng depthDb. Gain dựng trên tín hiệu DC
    /// rồi làm mượt (lowpass 4 Hz, đo: -6 dB sau 50 ms, đủ sâu sau ~120 ms) trước khi nhân vào track. Release 700 ms giữ mức hạ qua khoảng
    /// nghỉ 0,4 s giữa câu (chỉ nhích ~1 dB) rồi hồi mượt về 0 dB trong ~1,2 s. Làm mượt (1 - gain) để đầu file không bị kéo từ 0.
    /// </summary>
    private static string VoiceDuck(int i, double depthDb)
    {
        var mix = Math.Clamp((1 - Math.Pow(10, depthDb / 20)) / (1 - SaturatedGain), 0, 1);
        return $"[k{i}]agate=threshold=0.0056:range=0.001:ratio=20:attack=1:release=50,volume=40dB,asoftclip=type=hard,asplit=2[dka{i}][dkb{i}];"
               + $"[dka{i}]aeval=exprs=1:c=same[dkc{i}];"
               + $"[dkc{i}][dkb{i}]sidechaincompress=threshold=0.0316:ratio=20:attack=20:release=700:mix={Fmt.N(mix, 4)},"
               + $"aeval=exprs=1-val(0):c=same,lowpass=f=4:p=1,aeval=exprs=1-val(0):c=same[dkg{i}];"
               + $"[m{i}][dkg{i}]amultiply[dk{i}]";
    }

    /// <summary>
    /// Mọi track được resample, đệm im lặng rồi cắt đúng thời lượng ra (F06) trước khi trộn amix duration=longest normalize=0,
    /// nên hết lời vẫn còn nhạc/tiếng gốc. Ducking: khoá = giọng (hoặc tiếng gốc nếu không có giọng), hạ tiếng gốc/BGM.
    /// </summary>
    public static MixGraph BuildGraph(IReadOnlyList<MixInput> inputs, TimeSpan duration, bool smartDucking, string outputLabel = "aout",
        double duckDb = DuckDb)
    {
        if (inputs.Count == 0) throw new ArgumentException("Cần ít nhất một track.", nameof(inputs));
        var d = Fmt.N(duration.TotalSeconds, 3);
        var parts = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var t = inputs[i];
            var chain = new List<string> { $"aresample={SampleRate}", "aformat=sample_fmts=fltp:channel_layouts=stereo" };
            if (!string.IsNullOrEmpty(t.PreFilter)) chain.Add(t.PreFilter);
            if (t.Delay > TimeSpan.Zero) chain.Add($"adelay={(long)t.Delay.TotalMilliseconds}:all=1");
            else if (t.Delay < TimeSpan.Zero) chain.Add($"atrim=start={Fmt.N(-t.Delay.TotalSeconds, 3)},asetpts=PTS-STARTPTS");
            chain.Add("volume=" + Fmt.N(t.Volume, 4));
            chain.Add("apad");
            chain.Add($"atrim=duration={d}");
            chain.Add("asetpts=PTS-STARTPTS");
            parts.Add($"[{t.Stream}]{string.Join(',', chain)}[m{i}]");
        }

        var labels = Enumerable.Range(0, inputs.Count).Select(i => $"[m{i}]").ToArray();
        var keyIndex = inputs.ToList().FindIndex(x => x.Role == MixRole.Voice);
        if (keyIndex < 0) keyIndex = inputs.ToList().FindIndex(x => x.Role == MixRole.Original);
        var ducked = smartDucking && keyIndex >= 0
            ? Enumerable.Range(0, inputs.Count).Where(i => i != keyIndex && inputs[i].Role != MixRole.Voice
                                                           && !(inputs[keyIndex].Role == MixRole.Original && inputs[i].Role == MixRole.Original)).ToList()
            : [];
        if (ducked.Count > 0)
        {
            var keys = string.Concat(ducked.Select(i => $"[k{i}]"));
            parts.Add($"[m{keyIndex}]asplit={ducked.Count + 1}[mk]{keys}");
            labels[keyIndex] = "[mk]";
            foreach (var i in ducked)
            {
                parts.Add(inputs[keyIndex].Role == MixRole.Voice ? VoiceDuck(i, duckDb) : $"[m{i}][k{i}]{Ducking}[dk{i}]");
                labels[i] = $"[dk{i}]";
            }
        }

        parts.Add(inputs.Count == 1
            ? $"{labels[0]}alimiter=limit=0.95[{outputLabel}]"
            : $"{string.Concat(labels)}amix=inputs={inputs.Count}:duration=longest:normalize=0:dropout_transition=0,alimiter=limit=0.95[{outputLabel}]");
        return new MixGraph(string.Join(';', parts), outputLabel);
    }

    /// <summary>Trộn giọng lồng tiếng + tiếng gốc + BGM thành một file (port mix_final_audio).</summary>
    public async Task<string> MixAsync(MixRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var args = new List<string>();
        var inputs = new List<MixInput>();
        TimeSpan? voiceDur = null, originalDur = null;

        if (request.VoicePath is not null)
        {
            var voice = await AudioInput.FromFileAsync(request.VoicePath, probe, ct);
            voiceDur = voice.Duration;
            inputs.Add(new MixInput($"{inputs.Count}:a", MixRole.Voice, request.VoiceVolume));
            args.AddRange(["-i", voice.Path]);
        }

        if (request.OriginalPath is not null && request.OriginalVolume > 0)
        {
            var original = await AudioInput.FromFileAsync(request.OriginalPath, probe, ct);
            originalDur = original.Duration;
            inputs.Add(new MixInput($"{inputs.Count}:a", MixRole.Original, request.OriginalVolume));
            args.AddRange(["-i", original.Path]);
        }

        if (request.BgmPath is not null && request.BgmVolume > 0)
        {
            await AudioInput.FromFileAsync(request.BgmPath, probe, ct);
            inputs.Add(new MixInput($"{inputs.Count}:a", MixRole.Bgm, request.BgmVolume));
            args.AddRange(["-stream_loop", "-1", "-i", request.BgmPath]);
        }

        if (inputs.Count == 0) throw new ArgumentException("Không có nguồn âm thanh nào để trộn.");
        var duration = request.Duration ?? voiceDur ?? originalDur
            ?? throw new ArgumentException("Cần thời lượng đầu ra khi chỉ có nhạc nền.");

        var graph = BuildGraph(inputs, duration, request.SmartDucking, duckDb: request.DuckDb);
        args.AddRange(["-filter_complex", graph.Filter, "-map", $"[{graph.OutputLabel}]", "-t", Fmt.N(duration.TotalSeconds, 3)]);
        args.AddRange(Path.GetExtension(request.Output).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? ["-c:a", "pcm_s16le", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture)]
            : ["-c:a", "aac", "-b:a", "192k"]);
        await ffmpeg.RunAsync(new FfmpegJob(args, request.Output, duration, Label: "Trộn âm thanh"), progress, ct);
        return request.Output;
    }
}
