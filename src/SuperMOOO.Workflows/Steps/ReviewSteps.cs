using System.ComponentModel.DataAnnotations;
using SuperMOOO.AI.Tts;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Render;
using SuperMOOO.Workflows.Review;
using SuperMOOO.Workflows.Subtitles;
using SuperMOOO.Workflows.TtsStudio;
using F = SuperMOOO.Media.Ffmpeg.Fmt;

namespace SuperMOOO.Workflows.Steps;

internal static class SceneFile
{
    public static async Task<List<ReviewScene>> ReadAsync(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<List<ReviewScene>>(await File.ReadAllTextAsync(path, ct), SubtitleEntries.Json) ?? [];

    public static Task WriteAsync(string path, IEnumerable<ReviewScene> scenes, CancellationToken ct) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(scenes, SubtitleEntries.Json), ct);
}

public sealed class StoryboardStepOptions
{
    [RegularExpression("summary|review|highlights", ErrorMessage = "Kiểu dựng phải là summary, review hoặc highlights.")] public string Mode { get; set; } = "summary";
    [Range(5, 1800)] public double TargetSeconds { get; set; } = 60;
    public bool Ai { get; set; }
    public string Style { get; set; } = "Kịch tính";
    public string Language { get; set; } = "vi";
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    /// <summary>Không AI: dùng chính lời thoại làm lời bình (đọc lại); tắt thì clip giữ tiếng gốc, không lời bình.</summary>
    public bool Narration { get; set; } = true;
}

/// <summary>Chọn cảnh + lời bình. Nếu người dùng đã đưa sẵn cảnh (input.scenes, đã sửa tay ở S04) thì giữ nguyên.</summary>
public sealed class StoryboardStep(StoryboardService storyboard) : PipelineStep<StoryboardStepOptions>
{
    public override string Kind => "storyboard";

    // Chọn cảnh bằng AI là lời gọi LLM tính phí: lỗi thì chờ người dùng chủ động thử lại.
    public override bool IsCloudBilled => true;

    protected override async Task<StepOutput> RunAsync(StepContext c, StoryboardStepOptions o, CancellationToken ct)
    {
        var output = Io.Work(c, "scenes.json");
        if (c.Input["scenes"] is JsonArray given)
        {
            var scenes = given.Deserialize<List<ReviewScene>>(SubtitleEntries.Json) ?? [];
            if (scenes.Count == 0) throw new InvalidOperationException("Chưa có cảnh nào để xuất.");
            await SceneFile.WriteAsync(output, scenes, ct);
            return Io.Out([("scenesFile", output), ("storyboardMethod", "manual")], output);
        }

        var duration = Io.Num(c, "duration");
        IReadOnlyList<StoryWindow> windows;
        if (Io.StateOrNull(c, "clipsFile") is { } clipsFile)
            windows = StoryboardService.Windows(JsonSerializer.Deserialize<List<RenderClip>>(await File.ReadAllTextAsync(clipsFile, ct), SubtitleEntries.Json) ?? []);
        else
            windows = StoryboardService.Windows(await SubtitleEntries.ReadAsync(Io.State(c, "segmentsFile"), ct), duration);

        var result = await storyboard.BuildAsync(windows, new StoryboardOptions(o.Mode, o.TargetSeconds, o.Ai, o.Style, o.Language, o.ProviderId, o.Model), ct);
        var final = result.Scenes.Select(s => s with
        {
            Narration = !o.Narration ? "" : s.Narration.Length > 0 ? s.Narration : (o.Ai ? "" : s.Caption[..Math.Min(2000, s.Caption.Length)])
        }).ToList();
        await SceneFile.WriteAsync(output, final, ct);
        return Io.Out([("scenesFile", output), ("storyboardMethod", result.Method), ("storyboardSeconds", Io.Fmt(result.Duration))], output);
    }
}

public sealed class NarrationOptions
{
    [Required] public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    /// <summary>Giọng đọc phần thoại của cảnh; trống = dùng giọng lời bình.</summary>
    public string? DialogVoiceId { get; set; }
    public string? Language { get; set; }
    [Range(0.5, 2.0)] public double Speed { get; set; } = 1.0;
}

/// <summary>TTS lời bình (và thoại) từng cảnh, co tempo cho vừa độ dài cảnh (quá 1,8 lần thì từ chối thay vì cắt chữ).</summary>
public sealed class NarrationStep(TtsService tts, FfmpegRunner ffmpeg) : PipelineStep<NarrationOptions>
{
    private const double MaxOverrun = 1.8;

    public override string Kind => "narration";

    protected override async Task<StepOutput> RunAsync(StepContext c, NarrationOptions o, CancellationToken ct)
    {
        var scenes = await SceneFile.ReadAsync(Io.State(c, "scenesFile"), ct);
        var dir = Path.Combine(c.WorkFolder, "narration");
        Directory.CreateDirectory(dir);
        var paths = new List<string?>();

        for (var i = 0; i < scenes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var scene = scenes[i];
            var parts = new List<string>();
            foreach (var (role, text, voice) in new[] { ("n", scene.Narration, o.VoiceId), ("d", scene.Dialog, o.DialogVoiceId ?? o.VoiceId) })
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var wav = Path.Combine(dir, $"scene_{i + 1:000}_{role}.wav");
                // Giọng native bỏ qua Rate: chỉnh bằng atempo như bước tts, không thì tốc độ đọc người chọn bị mất.
                var rate = tts.SupportsRate(voice) ? o.Speed : 1.0;
                await tts.SynthesizeAsync(new TtsRequest(text, voice, wav, o.Language, rate), ct);
                parts.Add(Math.Abs(rate - o.Speed) < 0.001 ? wav : await VoiceSpeed.ApplyAsync(ffmpeg, wav, o.Speed, ct));
            }

            paths.Add(parts.Count == 0 ? null : await FitAsync(parts, Path.Combine(dir, $"scene_{i + 1:000}.wav"), scene.End - scene.Start, i + 1, ct));
            c.Progress.Report(new ProgressInfo((i + 1.0) / scenes.Count, $"cảnh {i + 1}/{scenes.Count}"));
        }

        var file = Io.Work(c, "narration.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(paths, SubtitleEntries.Json), ct);
        return Io.Out([("narrationFile", file)], file);
    }

    private async Task<string> FitAsync(List<string> parts, string output, double length, int number, CancellationToken ct)
    {
        var args = new List<string>();
        foreach (var p in parts) args.AddRange(["-i", p]);
        var graph = string.Concat(parts.Select((_, k) => $"[{k}:a]aformat=sample_rates=24000:channel_layouts=mono[a{k}];"))
                    + string.Concat(parts.Select((_, k) => $"[a{k}]")) + $"concat=n={parts.Count}:v=0:a=1[joined]";
        var spoken = parts.Sum(p => WavFile.DescribeFile(p).Duration.TotalSeconds);
        if (spoken <= 0) throw new InvalidOperationException("TTS không tạo được lời bình.");
        if (spoken > length * MaxOverrun)
            throw new InvalidOperationException($"Lời bình cảnh {number} quá dài ({spoken:0.0}s cho cảnh {length:0.0}s); rút ngắn lời bình hoặc tăng thời lượng cảnh.");

        // Ghép (và co tempo nếu cần) trong một lần chạy ffmpeg.
        var tempo = spoken > length ? $";[joined]{F.TempoChain(spoken / length)}[out]" : ";[joined]anull[out]";
        await ffmpeg.RunAsync(new FfmpegJob([.. args, "-filter_complex", graph + tempo, "-map", "[out]", "-c:a", "pcm_s16le"], output, Label: "Lời bình cảnh"), null, ct);
        return output;
    }
}

public sealed class RenderProjectOptions
{
    public string Aspect { get; set; } = "Original";
    public string Fit { get; set; } = "Contain";
    [Range(144, 2160)] public int Height { get; set; } = 720;
    [Range(15, 60)] public int Fps { get; set; } = 30;
    [Range(0.0, 2.0)] public double Fade { get; set; } = 0.25;
    public bool Captions { get; set; } = true;
    [RegularExpression("#[0-9a-fA-F]{6}", ErrorMessage = "Màu phụ đề phải có dạng #RRGGBB.")] public string CaptionColor { get; set; } = "#FFCC00";
    /// <summary>Chữ trên nền hộp (thẻ) thay vì chữ viền.</summary>
    public bool CardCaptions { get; set; }
    [Range(0.0, 4.0)] public double Volume { get; set; } = 1.0;
    public string? MusicPath { get; set; }
    [Range(0.0, 1.0)] public double MusicVolume { get; set; } = 0.12;
    public ExportMode ExportMode { get; set; } = ExportMode.Montage;
}

/// <summary>Dựng RenderProject nội bộ từ các cảnh (không có editor): montage một MP4 hoặc mỗi cảnh một MP4.</summary>
public sealed class RenderProjectStep(ProjectRenderer renderer, IOutputRegistry outputs) : PipelineStep<RenderProjectOptions>
{
    public override string Kind => "render_project";

    protected override async Task<StepOutput> RunAsync(StepContext c, RenderProjectOptions o, CancellationToken ct)
    {
        var scenes = await SceneFile.ReadAsync(Io.State(c, "scenesFile"), ct);
        var narration = Io.StateOrNull(c, "narrationFile") is { } file
            ? JsonSerializer.Deserialize<List<string?>>(await File.ReadAllTextAsync(file, ct), SubtitleEntries.Json) ?? []
            : [];
        var source = Io.Source(c);
        var clips = scenes.Select(s => new RenderClip(source, s.Start, s.End, 1, o.Volume,
            s.Narration.Length > 0 ? s.Narration : s.Caption, s.Narration)).ToList();
        var project = new RenderProject(Io.Name(c), clips, o.Aspect, o.Fit, o.Height, o.Fps, o.Fade, o.Captions, null,
            string.IsNullOrWhiteSpace(o.MusicPath) ? null : o.MusicPath, o.MusicVolume, null, 1, o.ExportMode);

        var dir = Io.OutputDir(c);
        var target = o.ExportMode == ExportMode.Individual
            ? Path.Combine(dir, $"{Io.Name(c)}_parts")
            : Io.UniquePath(dir, $"{Io.Name(c)}_review.mp4");
        var style = new SuperMOOO.Core.Models.SubtitleStyle(FontSize: Math.Max(16, o.Height / 27), PrimaryColor: o.CaptionColor, Outline: o.CardCaptions ? 0 : 3,
            PositionY: 88, Bold: true, BoxBackground: o.CardCaptions, BackColor: "#FFFFFF", BackOpacity: 0.85);
        var options = new RenderOptions((i, _, _) => Task.FromResult(i < narration.Count ? narration[i] : null), o.CardCaptions ? style with { PrimaryColor = "#111111" } : style);

        var result = await renderer.RenderAsync(project, target, options, c.Progress, ct);
        foreach (var f in result.Files) await outputs.RegisterOutputAsync(f, "review", ct);
        return Io.Out([("video", result.Files[0]), ("videos", string.Join('\n', result.Files)), ("scenes", scenes.Count.ToString(CultureInfo.InvariantCulture))], [.. result.Files]);
    }
}
