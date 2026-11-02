using System.ComponentModel.DataAnnotations;
using SuperMOOO.AI.Vision;
using SuperMOOO.Media.Compose;
using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Render;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Media.Tools;
using SuperMOOO.Workflows.Subtitles;

namespace SuperMOOO.Workflows.Steps;

public sealed class ComposeOptions
{
    public string AspectRatio { get; set; } = "original";
    public int? OutputResolution { get; set; }
    public bool Mirror { get; set; }
    public string Fit { get; set; } = "contain";
    public BlurRegion? Blur { get; set; }
    public OverlayAsset? Canvas { get; set; }
    public OverlayAsset? Logo { get; set; }
    public OverlayAsset? Pip { get; set; }
    public bool BurnSubtitles { get; set; } = true;
    public SubtitleStyle? Style { get; set; }
    public string? KaraokeColor { get; set; }
    public bool CutMeta { get; set; }
    public string? OutroFile { get; set; }
}

/// <summary>Ghép video gốc + track âm thanh đã trộn (che/làm mờ vùng, logo/canvas/PiP, hardsub hoặc karaoke theo từng từ).</summary>
public sealed class ComposeLocalizedStep(LocalizeComposer composer, IOutputRegistry outputs) : PipelineStep<ComposeOptions>
{
    public override string Kind => "compose_localized";

    protected override async Task<StepOutput> RunAsync(StepContext c, ComposeOptions o, CancellationToken ct)
    {
        SubtitleDocument? doc = null;
        if (o.BurnSubtitles && Io.StateOrNull(c, "segmentsFile") is { } file)
            doc = SubtitleEntries.ToDocument(await SubtitleEntries.ReadAsync(file, ct));

        var style = o.Style ?? new SubtitleStyle(PositionY: 82, Bold: true);
        if (o.KaraokeColor is { Length: > 0 } karaoke) style = style with { KaraokeColor = karaoke };

        // Bước burned_text (nếu chạy): mốc có chữ cho làm mờ, hoặc patch đã xoá phụ đề gốc.
        List<(double, double)>? spans = null;
        if (Io.StateOrNull(c, "textSpansFile") is { } spansFile)
            spans = [.. (JsonSerializer.Deserialize<List<TextSpan>>(await File.ReadAllTextAsync(spansFile, ct), JsonSerializerOptions.Web) ?? []).Select(s => (s.Start, s.End))];
        var patch = Io.StateOrNull(c, "inpaintPatch") is { } patchFile ? new InpaintPatch(patchFile, (int)Io.Num(c, "inpaintX"), (int)Io.Num(c, "inpaintY")) : null;

        var output = Io.OverwritablePath(Io.OutputDir(c), $"{Io.Name(c)}_localized.mp4");
        await composer.ComposeAsync(Io.Source(c), Io.State(c, "mixedAudio"), output, new LocalizeComposeOptions(o.AspectRatio, o.OutputResolution,
            o.Mirror, o.Fit, o.Blur, o.Canvas, o.Logo, o.Pip, doc, style, o.CutMeta, o.OutroFile, spans, patch), c.Progress, ct);
        await outputs.RegisterOutputAsync(output, "localize", ct);
        return Io.Out([("video", output)], output);
    }
}

public sealed class BurnedTextOptions
{
    /// <summary>Vùng phụ đề gốc theo % khung nguồn (cùng vùng làm mờ).</summary>
    public PercentBox? Box { get; set; }
    /// <summary>Xoá phụ đề gốc bằng inpaint (chậm) thay cho làm mờ.</summary>
    public bool Inpaint { get; set; }
    /// <summary>1 = vẽ lại mọi khung nền đổi; N &gt; 1 dùng lại phần đã vẽ cho N-1 khung kế.</summary>
    [Range(1, 10)] public int KeyframeEvery { get; set; } = 1;
}

/// <summary>Phụ đề cứng trong video nguồn (AI worker): dò lúc có chữ trong vùng → state textSpansFile; tuỳ chọn xoá bằng inpaint → inpaintPatch.</summary>
public sealed class BurnedTextStep(BurnedSubtitleService burned) : PipelineStep<BurnedTextOptions>
{
    public override string Kind => "burned_text";

    protected override async Task<StepOutput> RunAsync(StepContext c, BurnedTextOptions o, CancellationToken ct)
    {
        var b = o.Box ?? throw new InvalidOperationException("Chưa có vùng phụ đề gốc (vùng làm mờ).");
        var box = new TextBand(b.X, b.Y, b.W, b.H);
        var source = Io.Source(c);
        // Dò chữ ~0,1–0,15× thời lượng, inpaint ~5–20×: chia thanh tiến độ theo tỉ lệ đó.
        var share = o.Inpaint ? 0.05 : 1.0;
        var spans = await burned.TimelineAsync(source, box, Scaled(c.Progress, 0, share), ct);
        var spansFile = Io.Work(c, "text_spans.json");
        await File.WriteAllTextAsync(spansFile, JsonSerializer.Serialize(spans, JsonSerializerOptions.Web), ct);
        var values = new List<(string, string)> { ("textSpansFile", spansFile) };
        if (spans.Count == 0) values.Add(("warnings", Io.Warnings(c, ["Không thấy chữ trong vùng phụ đề gốc: bỏ qua làm mờ/xoá."])));
        else if (o.Inpaint)
        {
            var patch = await burned.InpaintAsync(source, box, spans, Io.Work(c, "inpaint_patch.mp4"), o.KeyframeEvery, Scaled(c.Progress, share, 1), ct);
            values.AddRange([("inpaintPatch", patch.File), ("inpaintX", patch.X.ToString(CultureInfo.InvariantCulture)),
                ("inpaintY", patch.Y.ToString(CultureInfo.InvariantCulture))]);
        }

        return Io.Out(values, spansFile);
    }

    private static IProgress<ProgressInfo> Scaled(IProgress<ProgressInfo> progress, double from, double to) => new Part(progress, from, to);

    private sealed class Part(IProgress<ProgressInfo> parent, double from, double to) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value) =>
            parent.Report(value with { Fraction = value.Fraction < 0 ? value.Fraction : from + (to - from) * value.Fraction });
    }
}

public sealed class MaskRegionOptions
{
    public MaskRegionPreset Region { get; set; } = MaskRegionPreset.Bottom;
    public MaskStyle Style { get; set; } = MaskStyle.Blur;
    public PercentBox? Custom { get; set; }
    public string Color { get; set; } = "";
}

/// <summary>Che vùng phụ đề cũ; kết quả trung gian nằm ở thư mục làm việc và thay thế `video` cho bước sau.</summary>
public sealed class MaskRegionStep(MediaToolsService tools) : PipelineStep<MaskRegionOptions>
{
    public override string Kind => "mask_region";

    protected override async Task<StepOutput> RunAsync(StepContext c, MaskRegionOptions o, CancellationToken ct)
    {
        var input = Io.StateOrNull(c, "video") ?? Io.Source(c);
        var result = await tools.MaskRegionAsync(input, o.Region, o.Style, o.Custom is { } b ? (b.X, b.Y, b.W, b.H) : null, o.Color,
            c.WorkFolder, c.Progress, ct);
        return Io.Out([("video", result.Path)], result.Path);
    }
}

public sealed class BurnSubtitlesOptions
{
    public string StylePreset { get; set; } = "CapCutYellow";
    [Range(8, 200)] public int FontSize { get; set; } = 48;
}

public sealed class BurnSubtitlesStep(MediaToolsService tools, IOutputRegistry outputs) : PipelineStep<BurnSubtitlesOptions>
{
    public override string Kind => "burn_subtitles";

    protected override async Task<StepOutput> RunAsync(StepContext c, BurnSubtitlesOptions o, CancellationToken ct)
    {
        var input = Io.StateOrNull(c, "video") ?? Io.Source(c);
        var result = await tools.BurnSubtitlesAsync(input, Io.State(c, "subtitleFile"), o.StylePreset, o.FontSize, Io.OutputDir(c), c.Progress, ct);
        await outputs.RegisterOutputAsync(result.Path, "subtitles", ct);
        return Io.Out([("video", result.Path)], result.Path);
    }
}

public sealed class ReupRenderOptions
{
    public bool BurnCues { get; set; }
}

/// <summary>Xuất video theo ReupEffectProfile (input.profile). Giọng/cue tự sinh từ bước trước được gắn vào profile; intro/outro nối luôn trong cùng lượt encode.</summary>
public sealed class ReupRenderStep(ReupRenderer renderer, IOutputRegistry outputs) : PipelineStep<ReupRenderOptions>
{
    public override string Kind => "reup_render";

    protected override async Task<StepOutput> RunAsync(StepContext c, ReupRenderOptions o, CancellationToken ct)
    {
        var profile = ReadProfile(c);
        if (Io.StateOrNull(c, "dubAudio") is { } voice) profile = profile with { Audio = profile.Audio with { VoiceoverPath = voice } };
        // Nền đã tách giọng (bước separate_vocals); lỗi/thiếu model thì bước đó đã cảnh báo và renderer dùng tiếng gốc đầy đủ.
        if (Io.StateOrNull(c, "backgroundAudio") is { } background) profile = profile with { Audio = profile.Audio with { BackgroundPath = background } };
        if (o.BurnCues && Io.StateOrNull(c, "cuesFile") is { } cues)
        {
            var shift = profile.Audio.VoiceoverOffset.TotalSeconds;
            var entries = (await SubtitleEntries.ReadAsync(cues, ct)).Select(e => e with { Start = e.Start + shift, End = e.End + shift });
            var doc = SubtitleEntries.ToDocument(entries, useTranslation: false);
            profile = profile with { Subtitles = new SubtitleBurn(doc, profile.Subtitles?.Style ?? new SubtitleStyle(PositionY: 82, Bold: true), "Output") };
        }

        var source = Io.Source(c);
        var name = OutputNaming.Resolve(profile.Output.FileNameTemplate, source, profile.PresetId);
        var final = Io.UniquePath(Io.OutputDir(c), name);

        var result = await renderer.RenderAsync(source, profile, final, c.Progress, ct);
        await outputs.RegisterOutputAsync(final, "reup", ct);
        return Io.Out([("video", result.Path), ("finalPath", final)], result.Path);
    }

    internal static ReupEffectProfile ReadProfile(StepContext c) =>
        c.Input["profile"].Deserialize<ReupEffectProfile>(SubtitleEntries.Json) ?? throw new InvalidOperationException("Thiếu hồ sơ hiệu ứng reup (input.profile).");
}

public sealed class IntroOutroOptions
{
    public string? IntroPath { get; set; }
    public string? OutroPath { get; set; }
}

public sealed class IntroOutroStep(IntroOutroConcat concat, IMediaProbe probe, IOutputRegistry outputs) : PipelineStep<IntroOutroOptions>
{
    public override string Kind => "intro_outro";

    protected override async Task<StepOutput> RunAsync(StepContext c, IntroOutroOptions o, CancellationToken ct)
    {
        var main = Io.State(c, "video");
        var files = new[] { o.IntroPath, main, o.OutroPath }.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).ToList();
        var final = Io.StateOrNull(c, "finalPath") ?? Io.UniquePath(Io.OutputDir(c), $"{Io.Name(c)}_final.mp4");
        // reup_render đã nối intro/outro trong lượt encode của nó và ghi thẳng ra finalPath (đã đăng ký); nối lại sẽ thành hai lần.
        if (string.Equals(Path.GetFullPath(main), Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase)) return Io.Out([("video", final)], final);
        if (files.Count == 1)
        {
            File.Copy(main, final, overwrite: true);
        }
        else
        {
            var info = await probe.ProbeAsync(main, ct);
            var (w, h) = info.DisplaySize();
            var encode = c.Input["profile"] is not null ? ReupRenderStep.ReadProfile(c).Output : new OutputOptions(Preset: "faster");
            await concat.ConcatAsync(files, final, w, h, info.Video is { Fps: > 0 } v ? (int)Math.Round(v.Fps) : 30, encode, c.Progress, ct);
        }

        await outputs.RegisterOutputAsync(final, Io.StateOrNull(c, "outputKind") ?? "reup", ct);
        return Io.Out([("video", final)], final);
    }
}
