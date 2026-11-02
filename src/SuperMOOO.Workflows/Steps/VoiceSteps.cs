using System.ComponentModel.DataAnnotations;
using SuperMOOO.AI.Keys;
using SuperMOOO.AI.Tts;
using SuperMOOO.Media.Audio;
using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Subtitles;
using Microsoft.Extensions.Logging;
using F = SuperMOOO.Media.Ffmpeg.Fmt;

namespace SuperMOOO.Workflows.Steps;

/// <summary>Một câu đã tổng hợp. Start/End có khi bám phụ đề (lồng tiếng); null khi đọc kịch bản liên tục.</summary>
public sealed record TtsItem(int Index, double? Start, double? End, string Text, string Path, double Seconds, double PendingTempo = 1);

public static class TtsIndex
{
    public static async Task<List<TtsItem>> ReadAsync(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<List<TtsItem>>(await File.ReadAllTextAsync(path, ct), SubtitleEntries.Json) ?? [];

    public static Task WriteAsync(string path, IEnumerable<TtsItem> items, CancellationToken ct) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(items, SubtitleEntries.Json), ct);
}

public sealed class TtsStepOptions
{
    [Required] public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    public string? Language { get; set; }
    [Range(0.5, 2.0)] public double Speed { get; set; } = 1.0;
    [Range(-12.0, 12.0)] public double PitchSemitones { get; set; }
    [Range(0, 5)] public int Retries { get; set; } = 2;
    [Range(0, 10000)] public int RetryDelayMs { get; set; } = 500;
    public bool SkipFailed { get; set; }
    /// <summary>Không atempo tốc độ đọc ở bước này mà ghi vào PendingTempo cho fit_tempo gộp với atempo khớp ô (một lần ffmpeg mỗi câu). Chỉ bật khi pipeline có fit_tempo.</summary>
    public bool DeferSpeed { get; set; }
}

public sealed class WarmTtsOptions
{
    public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    public string? Language { get; set; }
}

/// <summary>Nạp trước server/model TTS ngầm trong lúc ASR/dịch chạy; không chờ kết quả, lỗi chỉ ghi log (bước TTS thật tự khởi động lại).</summary>
public sealed class WarmTtsStep(TtsService tts, ILogger<WarmTtsStep> log) : PipelineStep<WarmTtsOptions>
{
    public override string Kind => "warm_tts";

    protected override Task<StepOutput> RunAsync(StepContext c, WarmTtsOptions o, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await tts.WarmAsync(o.VoiceId, o.Language, ct); }
            catch (OperationCanceledException) { }
            catch (Exception e) { log.LogWarning(e, "Nạp trước TTS {Voice} thất bại", o.VoiceId); }
        }, CancellationToken.None);
        return Task.FromResult(Io.Out([]));
    }
}

/// <summary>TTS theo câu (F28): lỗi một câu thì thử lại, hết lượt mới dừng và nêu số câu; "bỏ qua câu lỗi" chỉ khi người dùng bật và ghi vào báo cáo.</summary>
public sealed class TtsStep(TtsService tts, IEnumerable<ITtsProvider> providers, FfmpegRunner ffmpeg) : PipelineStep<TtsStepOptions>
{
    public override string Kind => "tts";

    protected override async Task<StepOutput> RunAsync(StepContext c, TtsStepOptions o, CancellationToken ct)
    {
        var sentences = await SentencesAsync(c, ct);
        if (sentences.Count == 0) throw new InvalidOperationException("Không có câu nào để tạo giọng đọc.");
        // Báo rõ ngay từ đầu thay vì để server TTS trả lỗi khó hiểu giữa chừng (vd. tắt "Dịch phụ đề" mà bản dịch cũ là tiếng Hàn).
        if (o.Language is { Length: > 0 } lang && sentences.FindIndex(s => !SuperMOOO.AI.Catalog.LanguageCatalog.ScriptMatches(s.Text, lang)) is var bad and >= 0)
            throw new InvalidOperationException($"Câu {bad + 1}/{sentences.Count} không phải {SuperMOOO.AI.Catalog.LanguageCatalog.TextLanguage(lang)} " +
                $"(\"{sentences[bad].Text[..Math.Min(40, sentences[bad].Text.Length)]}\"). Bật \"Dịch phụ đề\" hoặc sửa bản dịch ở S02 cho đúng ngôn ngữ đích.");

        var voice = VoiceIdMigrator.Migrate(null, o.VoiceId);
        var supportsRate = providers.FirstOrDefault(p => p.Id == voice.ProviderId)?.Capabilities.SupportsRate == true;
        var dir = Path.Combine(c.WorkFolder, "tts");
        Directory.CreateDirectory(dir);

        var items = new List<TtsItem>();
        var warnings = new List<string>();
        for (var i = 0; i < sentences.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (start, end, text) = sentences[i];
            try
            {
                var (path, seconds, pending) = await SynthesizeAsync(Path.Combine(dir, $"seg_{i + 1:0000}.wav"), text, o, supportsRate, start is not null && end is not null, ct);
                items.Add(new TtsItem(i + 1, start, end, text, path, seconds, pending));
            }
            catch (Exception e) when (e is not OperationCanceledException && o.SkipFailed)
            {
                warnings.Add($"Câu {i + 1}/{sentences.Count} bị bỏ qua: {e.Message}");
            }
            catch (Exception e) when (e is not OperationCanceledException and not ArgumentException)
            {
                throw new InvalidOperationException($"Lỗi tạo giọng đọc câu {i + 1}/{sentences.Count}: {e.Message}", e);
            }

            c.Progress.Report(new ProgressInfo((i + 1.0) / sentences.Count, $"câu {i + 1}/{sentences.Count}"));
        }

        if (items.Count == 0) throw new InvalidOperationException("Mọi câu đều tạo giọng thất bại.");
        var index = Io.Work(c, "tts.json");
        await TtsIndex.WriteAsync(index, items, ct);
        return Io.Out([("ttsIndex", index), ("ttsVoice", o.VoiceId), ("ttsLanguage", o.Language ?? ""), ("ttsSpeed", Io.Fmt(o.Speed)),
            ("ttsSupportsRate", supportsRate.ToString()), ("warnings", Io.Warnings(c, warnings))], index);
    }

    private async Task<(string Path, double Seconds, double Pending)> SynthesizeAsync(string path, string text, TtsStepOptions o, bool supportsRate, bool timed, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var rate = supportsRate ? o.Speed : 1.0;
                var result = await tts.SynthesizeAsync(new TtsRequest(text, o.VoiceId, path, o.Language, rate, o.PitchSemitones), ct);
                // Provider không có Rate thì chỉnh tốc độ bằng atempo như bản cũ.
                if (supportsRate || Math.Abs(o.Speed - 1) < 0.001) return (result.Path, result.Duration.TotalSeconds, 1);
                if (o.DeferSpeed && timed) return (result.Path, result.Duration.TotalSeconds / o.Speed, o.Speed);
                var sped = Path.ChangeExtension(path, ".speed.wav");
                await ffmpeg.RunAsync(new FfmpegJob(["-i", result.Path, "-af", F.TempoChain(o.Speed), "-c:a", "pcm_s16le"], sped, Label: "Tốc độ đọc"), null, ct);
                return (sped, WavFile.DescribeFile(sped).Duration.TotalSeconds, 1);
            }
            catch (Exception e) when (e is not OperationCanceledException and not ArgumentException && attempt < o.Retries)
            {
                // 429: ApiKeyPool khoá key trong thời gian chờ, thử lại sau 0,5–1 s chắc chắn lại lỗi; chờ hết khoá rồi mới thử.
                var delay = e is ProviderException { Kind: ProviderErrorKind.RateLimited }
                    ? ApiKeyPool.RateLimitCooldown + TimeSpan.FromSeconds(1)
                    : TimeSpan.FromMilliseconds(o.RetryDelayMs * (attempt + 1));
                await Task.Delay(delay, ct);
            }
        }
    }

    private static async Task<List<(double? Start, double? End, string Text)>> SentencesAsync(StepContext c, CancellationToken ct)
    {
        if (Io.StateOrNull(c, "segmentsFile") is { } file)
            return (await SubtitleEntries.ReadAsync(file, ct)).Where(e => e.Display.Trim().Length > 0)
                .Select(e => ((double?)e.Start, (double?)e.End, e.Display.Trim())).ToList();
        var script = Io.StateOrNull(c, "script") ?? Io.Str(c, "script") ?? throw new InvalidOperationException("Thiếu phụ đề hoặc kịch bản để tạo giọng đọc.");
        return SubtitleTimingGenerator.Split(script).Select(s => ((double?)null, (double?)null, s)).ToList();
    }
}

public sealed class FitTempoOptions
{
    public bool AutoFit { get; set; } = true;
    [Range(1.0, 3.0)] public double MaxTempo { get; set; } = 1.8;
    [Range(0.0, 3.0)] public double BorrowGapSeconds { get; set; } = 0.6;
    [Range(0.5, 1.0)] public double MinTempo { get; set; } = 1.0;
    [Range(0.1, 5.0)] public double MinSlotSeconds { get; set; } = 0.5;
}

/// <summary>Khớp từng câu TTS vào ô thoại bằng TempoFitter (F26); tắt AutoFit thì giữ nguyên độ dài tự nhiên.</summary>
public sealed class FitTempoStep(TempoFitter fitter, TtsService tts) : PipelineStep<FitTempoOptions>
{
    public override string Kind => "fit_tempo";

    protected override async Task<StepOutput> RunAsync(StepContext c, FitTempoOptions o, CancellationToken ct)
    {
        var items = await TtsIndex.ReadAsync(Io.State(c, "ttsIndex"), ct);
        var noFit = !o.AutoFit || items.All(i => i.Start is null);
        if (noFit && items.All(i => i.PendingTempo == 1)) return Io.Out([]);

        var total = Io.NumOrNull(c, "duration") ?? items.Max(i => i.End ?? 0);
        var supportsRate = Io.StateOrNull(c, "ttsSupportsRate") == "True";
        var tempo = new TempoOptions(o.MaxTempo, o.BorrowGapSeconds, o.MinTempo, AllowResynth: supportsRate);
        var dir = Path.Combine(c.WorkFolder, "tts");
        var fitted = new List<TtsItem>();
        var warnings = new List<string>();

        for (var n = 0; n < items.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[n];
            if (noFit)
            {
                // Tắt khớp ô nhưng tốc độ đọc còn chờ áp (DeferSpeed): áp riêng một lần.
                if (item.PendingTempo != 1)
                {
                    var sped = Path.Combine(dir, $"seg_{item.Index:0000}.fit.wav");
                    await fitter.ApplyAsync(item.Path, sped, new TempoPlan(TempoAction.Keep, 1, item.Seconds, 0, item.Seconds), item.Seconds, ct, item.PendingTempo);
                    item = item with { Path = sped, PendingTempo = 1 };
                }

                fitted.Add(item);
                continue;
            }

            var slot = Math.Max(o.MinSlotSeconds, item.End!.Value - item.Start!.Value);
            var next = n + 1 < items.Count ? items[n + 1].Start ?? total : total;
            var plan = TempoFitter.Plan(item.Seconds, slot, Math.Max(0, next - item.End.Value), tempo);

            var path = item.Path;
            var seconds = item.Seconds;
            if (plan.Action == TempoAction.Resynthesize)
            {
                var rate = double.Parse(Io.State(c, "ttsSpeed"), CultureInfo.InvariantCulture) * plan.ResynthRateFactor;
                var again = Path.Combine(dir, $"seg_{item.Index:0000}.resynth.wav");
                var result = await tts.SynthesizeAsync(new TtsRequest(item.Text, Io.State(c, "ttsVoice"), again,
                    Io.StateOrNull(c, "ttsLanguage") is { Length: > 0 } l ? l : null, rate), ct);
                path = result.Path;
                plan = TempoFitter.Plan(result.Duration.TotalSeconds, slot, Math.Max(0, next - item.End.Value), tempo with { AllowResynth = false });
                seconds = result.Duration.TotalSeconds;
            }

            var pending = plan.Action == TempoAction.Resynthesize ? 1 : item.PendingTempo;
            if (plan.Action is TempoAction.Atempo or TempoAction.Truncate || pending != 1)
            {
                var output = Path.Combine(dir, $"seg_{item.Index:0000}.fit.wav");
                await fitter.ApplyAsync(path, output, plan, slot, ct, pending);
                path = output;
                if (plan.Action is TempoAction.Atempo or TempoAction.Truncate) seconds = Math.Max(plan.OutputSeconds, slot);
            }

            if (plan.Warning is { } w) warnings.Add($"Câu {item.Index}: {w}");
            fitted.Add(item with { Path = path, Seconds = seconds, PendingTempo = 1 });
            c.Progress.Report(new ProgressInfo((n + 1.0) / items.Count, $"câu {n + 1}/{items.Count}"));
        }

        var index = Io.Work(c, "tts.fitted.json");
        await TtsIndex.WriteAsync(index, fitted, ct);
        return Io.Out([("ttsIndex", index), ("warnings", Io.Warnings(c, warnings))], index);
    }
}

public sealed class AssembleDubOptions
{
    [Range(8000, 96000)] public int SampleRate { get; set; } = AudioMixer.SampleRate;
    [Range(0, 5000)] public int GapMs { get; set; } = 250;
    public bool Export { get; set; }
}

/// <summary>Ghép các câu thành một track: đặt đúng mốc khi có timing, nối tiếp (kèm cue đúng thời lượng thật, F09) khi chỉ có kịch bản.</summary>
public sealed class AssembleDubStep(DubTrackAssembler assembler, IOutputRegistry outputs) : PipelineStep<AssembleDubOptions>
{
    public override string Kind => "assemble_dub";

    protected override async Task<StepOutput> RunAsync(StepContext c, AssembleDubOptions o, CancellationToken ct)
    {
        var items = await TtsIndex.ReadAsync(Io.State(c, "ttsIndex"), ct);
        var timed = items.All(i => i.Start is not null);
        var segments = new List<DubSegment>();
        var cues = new List<SubtitleEntry>();
        double total;
        if (timed)
        {
            segments.AddRange(items.Select(i => new DubSegment(i.Path, TimeSpan.FromSeconds(i.Start!.Value))));
            total = Math.Max(Io.NumOrNull(c, "duration") ?? 0, items.Max(i => i.Start!.Value + i.Seconds));
        }
        else
        {
            var cursor = 0.0;
            foreach (var i in items)
            {
                segments.Add(new DubSegment(i.Path, TimeSpan.FromSeconds(cursor)));
                cues.Add(new SubtitleEntry(Math.Round(cursor, 3), Math.Round(cursor + i.Seconds, 3), i.Text));
                cursor += i.Seconds + o.GapMs / 1000.0;
            }

            total = cursor - o.GapMs / 1000.0;
        }

        var output = Io.Work(c, "dub.wav");
        await assembler.AssembleAsync(segments, TimeSpan.FromSeconds(total), output, o.SampleRate, c.Progress, ct);

        var values = new List<(string, string)> { ("dubAudio", output), ("dubSeconds", Io.Fmt(total)) };
        var files = new List<string> { output };
        if (cues.Count > 0)
        {
            var cuesFile = Io.Work(c, "cues.json");
            await SubtitleEntries.WriteAsync(cuesFile, cues, ct);
            values.Add(("cuesFile", cuesFile));
            // Cue tự sinh đóng vai phụ đề của video chỉ có kịch bản (xuất SRT, gắn chữ).
            values.Add(("segmentsFile", cuesFile));
        }

        if (o.Export)
        {
            var exported = Io.UniquePath(Io.OutputDir(c), $"{Io.Name(c)}_dubbed.wav");
            File.Copy(output, exported);
            await outputs.RegisterOutputAsync(exported, "localize", ct);
            files.Add(exported);
        }

        return Io.Out(values, [.. files]);
    }
}

public sealed class MixAudioOptions
{
    [Range(0.0, 4.0)] public double OriginalVolume { get; set; } = 0.15;
    [Range(0.0, 4.0)] public double VoiceVolume { get; set; } = 1.0;
    public bool MuteOriginal { get; set; }
    public bool SmartDucking { get; set; } = true;
    public string? BgmPath { get; set; }
    [Range(0.0, 2.0)] public double BgmVolume { get; set; } = 0.12;
}

public sealed class MixAudioStep(AudioMixer mixer) : PipelineStep<MixAudioOptions>
{
    public override string Kind => "mix_audio";

    protected override async Task<StepOutput> RunAsync(StepContext c, MixAudioOptions o, CancellationToken ct)
    {
        var hasAudio = Io.StateOrNull(c, "hasAudio") != "False";
        // Nền đã tách giọng (bước separate_vocals) thay cho tiếng gốc; không còn lời nên chỉ duck nhẹ để giữ nhạc.
        var background = Io.StateOrNull(c, "backgroundAudio");
        var original = o.MuteOriginal || !hasAudio ? null : background ?? Io.StateOrNull(c, "denoisedAudio") ?? Io.Source(c);
        var bgm = !string.IsNullOrWhiteSpace(o.BgmPath) && File.Exists(o.BgmPath) ? o.BgmPath : null;
        var output = Io.Work(c, "mixed.wav");
        await mixer.MixAsync(new MixRequest(Io.StateOrNull(c, "dubAudio"), original, bgm, output, o.MuteOriginal ? 0 : o.OriginalVolume,
            o.VoiceVolume, o.BgmVolume, o.SmartDucking, TimeSpan.FromSeconds(Io.Num(c, "duration")),
            background is null ? AudioMixer.DuckDb : AudioMixer.LightDuckDb), c.Progress, ct);
        return Io.Out([("mixedAudio", output)], output);
    }
}

public sealed class VoiceEffectOptions
{
    public string? EffectId { get; set; }
    [Range(-12.0, 12.0)] public double PitchSemitones { get; set; }
    [RegularExpression("dub|audio", ErrorMessage = "Target phải là dub hoặc audio.")] public string Target { get; set; } = "dub";
}

public sealed class VoiceEffectStep(VoiceEffectService effects) : PipelineStep<VoiceEffectOptions>
{
    public override string Kind => "voice_effect";

    protected override async Task<StepOutput> RunAsync(StepContext c, VoiceEffectOptions o, CancellationToken ct)
    {
        var key = o.Target == "dub" ? "dubAudio" : "audio";
        var output = Io.Work(c, $"{key}.effect.wav");
        await effects.ApplyAsync(Io.State(c, key), output, o.EffectId, o.PitchSemitones, c.Progress, ct);
        return Io.Out([(key, output)], output);
    }
}

public sealed class ExportAudioOptions
{
    [RegularExpression("wav|mp3|flac", ErrorMessage = "Định dạng phải là wav, mp3 hoặc flac.")] public string Format { get; set; } = "wav";
    [RegularExpression("dub|audio", ErrorMessage = "Source phải là dub hoặc audio.")] public string Source { get; set; } = "dub";
    public string Operation { get; set; } = "voice";
}

public sealed class ExportAudioStep(FfmpegRunner ffmpeg, IOutputRegistry outputs) : PipelineStep<ExportAudioOptions>
{
    public override string Kind => "export_audio";

    protected override async Task<StepOutput> RunAsync(StepContext c, ExportAudioOptions o, CancellationToken ct)
    {
        var input = Io.State(c, o.Source == "dub" ? "dubAudio" : "audio");
        var output = Io.UniquePath(Io.OutputDir(c), $"{Io.Name(c)}.{o.Format}");
        string[] codec = o.Format switch
        {
            "mp3" => ["-c:a", "libmp3lame", "-b:a", "192k"],
            "flac" => ["-c:a", "flac"],
            _ => ["-c:a", "pcm_s16le"]
        };
        await ffmpeg.RunAsync(new FfmpegJob(["-i", input, "-vn", .. codec], output, Label: "Xuất âm thanh"), c.Progress, ct);
        await outputs.RegisterOutputAsync(output, o.Operation, ct);
        return Io.Out([("outputAudio", output)], output);
    }
}
