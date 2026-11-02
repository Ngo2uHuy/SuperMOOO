using SuperMOOO.Media.Audio;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;

namespace SuperMOOO.Media.Effects;

/// <param name="Voiceover">Bắt buộc khi profile có VoiceoverPath: đã probe (F02).</param>
/// <param name="VideoOnly">Chỉ dựng hình (khung xem trước tĩnh): bỏ audio.</param>
public sealed record ReupBuildInput(MediaInfo Source, ReupEffectProfile Profile, AudioInput? Voiceover = null, bool VideoOnly = false);

public sealed record ReupGraph(IReadOnlyList<string> InputArgs, string FilterComplex, string VideoLabel, string? AudioLabel,
    TimeSpan OutputDuration, int Width, int Height, string? AssContent)
{
    public const string AssFileName = "subs.ass";
}

/// <summary>Dựng filter graph ffmpeg cho một ReupEffectProfile; thuần tính toán, không IO (renderer ghi ASS và chạy ffmpeg).</summary>
[Service]
public sealed class ReupFilterGraphBuilder(VoiceEffectPresets voiceEffects)
{
    public ReupGraph Build(ReupBuildInput input)
    {
        var p = input.Profile;
        var src = input.Source;
        if (src.Video is null) throw new MediaProbeException("Nguồn không có luồng video.");

        var display = src.DisplaySize();
        var (w, h) = AspectRatio.Parse(p.Canvas.Aspect).Canvas(Math.Clamp(p.Output.Height, 360, 2160), display);
        var fps = p.Output.Fps > 0 ? p.Output.Fps : 30;

        var trimStart = p.Video.TrimStart ?? TimeSpan.Zero;
        var trimEnd = p.Video.TrimEnd is { } te && te < src.Duration ? te : src.Duration;
        var clip = trimEnd - trimStart;
        if (clip < TimeSpan.FromMilliseconds(100)) throw new ArgumentException("Đoạn cắt quá ngắn hoặc nằm ngoài video.");
        var speed = Math.Clamp(p.Video.Speed, 0.5, 2);
        var videoOut = TimeSpan.FromSeconds(clip.TotalSeconds / speed);

        var offset = p.Audio.VoiceoverOffset;
        AudioInput? voice = null;
        if (!input.VideoOnly && !string.IsNullOrWhiteSpace(p.Audio.VoiceoverPath))
            voice = input.Voiceover ?? throw new ArgumentException("Giọng đọc phải được probe trước khi dựng (AudioInput).");

        // F04: thời lượng ra = max(video, hết giọng) thay vì cắt giọng theo video.
        var outDur = videoOut;
        if (voice is not null && !p.Output.WhenVoiceLonger.Equals("CutVoice", StringComparison.OrdinalIgnoreCase))
            outDur = TimeSpan.FromSeconds(Math.Max(videoOut.TotalSeconds, (offset + voice.Duration).TotalSeconds));
        var extra = outDur - videoOut;
        var loop = extra > TimeSpan.FromMilliseconds(1) && p.Output.WhenVoiceLonger.Equals("LoopVideo", StringComparison.OrdinalIgnoreCase)
                   && trimStart == TimeSpan.Zero && p.Video.TrimEnd is null;

        // Input
        var args = new List<string>();
        var seek = new List<string>();
        if (loop) seek.AddRange(["-stream_loop", "-1"]);
        else
        {
            if (trimStart > TimeSpan.Zero) seek.AddRange(["-ss", Fmt.Sec(trimStart)]);
            if (trimEnd < src.Duration) seek.AddRange(["-t", Fmt.Sec(clip)]);
        }

        args.AddRange([.. seek, "-i", src.Path]);
        var next = 1;
        int voiceIdx = -1, bgmIdx = -1, logoIdx = -1, backgroundIdx = -1;
        // Nền đã tách giọng cùng thời lượng với nguồn nên cắt/lặp y như nguồn rồi thay cho 0:a.
        var useBackground = !input.VideoOnly && src.Audio is not null && !p.Audio.MuteOriginal && !string.IsNullOrWhiteSpace(p.Audio.BackgroundPath);
        if (useBackground) { args.AddRange([.. seek, "-i", p.Audio.BackgroundPath!]); backgroundIdx = next++; }
        if (voice is not null) { args.AddRange(["-i", voice.Path]); voiceIdx = next++; }
        var useBgm = !input.VideoOnly && !string.IsNullOrWhiteSpace(p.Audio.BgmPath);
        if (useBgm) { args.AddRange(["-stream_loop", "-1", "-i", p.Audio.BgmPath!]); bgmIdx = next++; }
        if (p.Logo is not null && !string.IsNullOrWhiteSpace(p.Logo.Path)) { args.AddRange(["-loop", "1", "-i", p.Logo.Path]); logoIdx = next++; }

        // Hình: canvas -> hflip -> zoom -> eq -> setpts -> fps -> tpad -> phụ đề -> logo.
        // Phụ đề cháy sau setpts/flip/zoom nên chữ không bị lật/zoom và mốc thời gian là thời gian ra (F03).
        var graph = new List<string>();
        graph.Add(Canvas(p.Canvas, w, h, display));

        var fx = new List<string>();
        if (p.Video.FlipH) fx.Add("hflip");
        if (p.Video.ZoomPercent > 100.0001)
        {
            var z = Math.Min(p.Video.ZoomPercent, 115) / 100;
            fx.Add($"scale={Fmt.Even(w * z)}:{Fmt.Even(h * z)},crop={w}:{h}");
        }

        if (Math.Abs(p.Video.Brightness) > 0.0001 || Math.Abs(p.Video.Contrast - 1) > 0.0001 || Math.Abs(p.Video.Saturation - 1) > 0.0001)
            fx.Add($"eq=brightness={Fmt.N(p.Video.Brightness, 3)}:contrast={Fmt.N(p.Video.Contrast, 3)}:saturation={Fmt.N(p.Video.Saturation, 3)}");
        if (Math.Abs(speed - 1) > 0.0001) fx.Add($"setpts={Fmt.N(1 / speed)}*PTS");
        fx.Add($"fps={fps}");
        if (extra > TimeSpan.FromMilliseconds(1) && !loop) fx.Add($"tpad=stop_mode=clone:stop_duration={Fmt.N(extra.TotalSeconds, 3)}");

        string? ass = null;
        if (p.Subtitles is { } sub && sub.Document.Cues.Count > 0)
        {
            ass = AssWriter.Write(sub.Document, sub.Style, new AssOptions(w, h, sub.TimeBase, trimStart, speed, outDur));
            fx.Add("ass=filename=" + ReupGraph.AssFileName);
        }

        fx.Add("format=yuv420p");
        var last = "v1";
        graph.Add($"[c]{string.Join(',', fx)}[v1]");

        if (logoIdx > 0)
        {
            graph.AddRange(LogoOverlay.Build(w, h, p.Logo!, $"{logoIdx}:v", "v1", "vout"));
            last = "vout";
        }

        // Audio
        string? audioLabel = null;
        if (!input.VideoOnly)
        {
            var mix = new List<MixInput>();
            if (src.Audio is not null && !p.Audio.MuteOriginal)
            {
                var pre = new List<string>();
                // Nền đã bỏ lời: khử nhiễu/hiệu ứng giọng không còn gì để tác động, chỉ giữ đổi tốc độ.
                if (!useBackground && p.Audio.Denoise.Equals("Fast", StringComparison.OrdinalIgnoreCase)) pre.Add("afftdn=nr=12:nf=-25");
                if (Math.Abs(speed - 1) > 0.0001) pre.Add(Fmt.TempoChain(speed));
                if (!useBackground && voiceEffects.BuildFilter(p.Audio.VoiceEffect, Math.Clamp(p.Audio.PitchSemitones, -6, 6)) is { } vf) pre.Add(vf);
                mix.Add(new MixInput(useBackground ? $"{backgroundIdx}:a" : "0:a", MixRole.Original, p.Audio.OriginalVolume,
                    PreFilter: pre.Count > 0 ? string.Join(',', pre) : null));
            }

            if (voiceIdx > 0) mix.Add(new MixInput($"{voiceIdx}:a", MixRole.Voice, p.Audio.VoiceoverVolume, offset));
            if (bgmIdx > 0) mix.Add(new MixInput($"{bgmIdx}:a", MixRole.Bgm, p.Audio.BgmVolume));
            if (mix.Count > 0)
            {
                var m = AudioMixer.BuildGraph(mix, outDur, p.Audio.SmartDucking, duckDb: useBackground ? AudioMixer.LightDuckDb : AudioMixer.DuckDb);
                graph.Add(m.Filter);
                audioLabel = m.OutputLabel;
            }
        }

        return new ReupGraph(args, string.Join(';', graph), last, audioLabel, outDur, w, h, ass);
    }

    /// <summary>Tham số đầu ra: map, encoder đúng bảng preset, audio AAC, và xoá metadata đầy đủ (F18).</summary>
    public static string[] OutputArgs(ReupEffectProfile p, ReupGraph g)
    {
        var o = p.Output;
        var args = new List<string> { "-map", $"[{g.VideoLabel}]" };
        if (g.AudioLabel is null) args.Add("-an");
        else args.AddRange(["-map", $"[{g.AudioLabel}]"]);

        args.AddRange(EncoderProfile.For(o.Encoder).VideoArgs(o.Preset, o.BitrateKbps));
        if (g.AudioLabel is not null) args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000"]);
        args.AddRange(["-t", Fmt.N(g.OutputDuration.TotalSeconds, 3), "-movflags", "+faststart"]);

        if (o.StripMetadata)
        {
            args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact",
                "-metadata:s:v:0", "encoder="]);
            if (g.AudioLabel is not null) args.AddRange(["-flags:a", "+bitexact"]);
        }

        return [.. args];
    }

    // F01: nền mờ cần [0:v] hai lần nên phải split trước; đọc [0:v] hai lần là lỗi ffmpeg.
    private static string Canvas(CanvasOptions c, int w, int h, (int Width, int Height) src)
    {
        var aspect = AspectRatio.Parse(c.Aspect);
        if (aspect.IsOriginal) return $"[0:v]scale={w}:{h},setsar=1[c]";
        if (c.Fit.Equals("Cover", StringComparison.OrdinalIgnoreCase))
            return $"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1[c]";

        var sameRatio = src.Height > 0 && Math.Abs((double)src.Width / src.Height - (double)w / h) < 0.01;
        if (c.BlurBackground && !sameRatio)
            return $"[0:v]split=2[bgsrc][fgsrc];"
                   + $"[bgsrc]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},boxblur=25:5[bg];"
                   + $"[fgsrc]scale={w}:{h}:force_original_aspect_ratio=decrease[fg];"
                   + "[bg][fg]overlay=(W-w)/2:(H-h)/2,setsar=1[c]";
        return $"[0:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1[c]";
    }
}
