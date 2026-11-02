using System.ComponentModel.DataAnnotations;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Shorts;
using SuperMOOO.Workflows.Subtitles;

namespace SuperMOOO.Workflows.Steps;

public sealed class ShortsSelectOptions
{
    [Range(1, 20)] public int Count { get; set; } = 3;
    [Range(5, 120)] public double MinSeconds { get; set; } = 15;
    [Range(10, 180)] public double MaxSeconds { get; set; } = 60;
    public bool HookMode { get; set; }
    public bool UseLlm { get; set; } = true;
    public string Language { get; set; } = "vi";
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
}

/// <summary>Chọn đoạn hay từ lời thoại (đã nhận diện ở bước asr).</summary>
public sealed class ShortsSelectStep(ShortsPlanner planner) : PipelineStep<ShortsSelectOptions>
{
    public override string Kind => "shorts_select";

    // Lời gọi LLM đám mây tính phí: lỗi thì chờ người dùng chủ động thử lại. Tắt AI thì không gọi gì.
    public override bool IsCloudBilled => true;
    protected override bool IsCloudBilledFor(ShortsSelectOptions options) => options.UseLlm;

    protected override async Task<StepOutput> RunAsync(StepContext c, ShortsSelectOptions o, CancellationToken ct)
    {
        var entries = await SubtitleEntries.ReadAsync(Io.State(c, "segmentsFile"), ct);
        var plan = await planner.PlanAsync(entries, Io.Num(c, "duration"),
            new ShortsPlanOptions(o.Count, o.MinSeconds, o.MaxSeconds, o.HookMode, o.UseLlm, o.Language, o.ProviderId, o.Model), ct);

        var file = Io.Work(c, "shorts.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(plan.Clips, SubtitleEntries.Json), ct);
        var values = new List<(string, string)> { ("shortsFile", file), ("shortsMethod", plan.Method) };
        if (plan.Warning is not null) values.Add(("warnings", Io.Warnings(c, [plan.Warning])));
        return Io.Out(values, file);
    }
}

public sealed class ShortsRenderOptionsStep
{
    [Range(540, 1080)] public int Width { get; set; } = 1080;
    /// <summary>true: giữ trọn khung hình trên nền mờ; false: lấp đầy 9:16, cắt giữa.</summary>
    public bool BlurFill { get; set; } = true;
    public bool Captions { get; set; } = true;
    public string CaptionStyle { get; set; } = "CapCutYellow";
    [Range(16, 120)] public int FontSize { get; set; } = 56;
    public bool TitleOverlay { get; set; } = true;
}

/// <summary>Cắt + dựng khung dọc từng đoạn; xuất file và danh sách tiêu đề (shorts_titles.txt) vào thư mục kết quả.</summary>
public sealed class ShortsRenderStep(ShortsRenderer renderer, IOutputRegistry outputs) : PipelineStep<ShortsRenderOptionsStep>
{
    public override string Kind => "shorts_render";

    protected override async Task<StepOutput> RunAsync(StepContext c, ShortsRenderOptionsStep o, CancellationToken ct)
    {
        var source = Io.Source(c);
        var clips = JsonSerializer.Deserialize<List<ShortClip>>(await File.ReadAllTextAsync(Io.State(c, "shortsFile"), ct), SubtitleEntries.Json) ?? [];
        if (clips.Count == 0) throw new InvalidOperationException("Chưa có đoạn nào để cắt.");
        var entries = await SubtitleEntries.ReadAsync(Io.State(c, "segmentsFile"), ct);
        var folder = Io.OutputDir(c);
        var name = Io.Name(c);
        var work = Path.Combine(c.WorkFolder, "shorts");
        var options = new ShortsRenderOptions(o.Width, o.BlurFill, o.Captions, o.CaptionStyle, o.FontSize, o.TitleOverlay);

        var files = new List<string>();
        var list = new System.Text.StringBuilder();
        for (var i = 0; i < clips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var clip = clips[i];
            var index = i;
            var part = new Progress<ProgressInfo>(p => c.Progress.Report(new ProgressInfo((index + p.Fraction) / clips.Count, $"Short {index + 1}/{clips.Count}")));
            var output = Io.UniquePath(folder, $"{name}_short_{i + 1:00}.mp4");
            await renderer.RenderAsync(source, clip, entries, options, output, work, part, ct);
            await outputs.RegisterOutputAsync(output, "shorts", ct);
            files.Add(output);
            list.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{Path.GetFileName(output)} | {clip.Start:0.0}s-{clip.End:0.0}s | {clip.Title}{(clip.Hook.Length > 0 ? " | Hook: " + clip.Hook : "")}"));
        }

        var titles = Io.UniquePath(folder, $"{name}_shorts_titles.txt");
        await File.WriteAllTextAsync(titles, list.ToString(), new System.Text.UTF8Encoding(false), ct);
        files.Add(titles);
        return Io.Out([("video", files[0]), ("shortsCount", clips.Count.ToString(CultureInfo.InvariantCulture)), ("titlesFile", titles)], [.. files]);
    }
}
