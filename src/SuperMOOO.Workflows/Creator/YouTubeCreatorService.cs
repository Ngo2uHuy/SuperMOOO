using System.Text.RegularExpressions;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Steps;

namespace SuperMOOO.Workflows.Creator;

/// <summary>Một kịch bản: văn bản dán trực tiếp hoặc file TXT/DOCX (đọc ở bước script_read).</summary>
public sealed record YouTubeScript(string Name, string? Text = null, string? Path = null);

public sealed class YouTubeCreatorOptions
{
    public string VoiceId { get; set; } = "cpp_vieneu_ngoc_huyen";
    public string? Language { get; set; }
    public double Speed { get; set; } = 1.0;
    public int Height { get; set; } = 1080;                  // 720 | 1080 | 1440
    public bool KenBurns { get; set; } = true;
    public bool Fade { get; set; } = true;
    public bool Waveform { get; set; } = true;
    public string WaveformColor { get; set; } = "#00FFCC";
    public string? IntroPath { get; set; }
    public string? OutroPath { get; set; }
    public SlideOrder SlideOrder { get; set; } = SlideOrder.Random;
    public string? OutputFolder { get; set; }
}

/// <summary>S03 YouTube: mỗi kịch bản một job `youtube_creator` (đọc → TTS từng câu → ghép giọng → slide Ken Burns → intro/outro). Giọng đọc giống nhau được dùng lại nhờ cache TTS.</summary>
[Service]
public sealed partial class YouTubeCreatorService(IJobQueue jobs, ProjectIndex projects, FfmpegRunner ffmpeg, AppPaths paths)
{
    private const int MaxScripts = 200;

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidName();

    public async Task<IReadOnlyList<string>> StartBatchAsync(IReadOnlyList<YouTubeScript> scripts, IReadOnlyList<string> slides,
        YouTubeCreatorOptions o, CancellationToken ct = default)
    {
        if (scripts.Count is 0 or > MaxScripts) throw new ArgumentException($"Chọn từ 1 đến {MaxScripts} kịch bản.");
        if (o.Height is not (720 or 1080 or 1440)) throw new ArgumentException("Độ phân giải 720p, 1080p hoặc 1440p.");
        if (o.Speed is < 0.5 or > 2.0) throw new ArgumentException("Tốc độ đọc từ 0.5 đến 2.0.");
        foreach (var file in new[] { o.IntroPath, o.OutroPath }.Where(f => !string.IsNullOrWhiteSpace(f)))
            if (!File.Exists(file)) throw new FileNotFoundException("Không tìm thấy intro/outro.", file);

        var folder = o.OutputFolder is { Length: > 0 } f ? f : Path.Combine(projects.OutputRoot(), "YouTube");
        var pool = slides.Count > 0 ? slides : [await DefaultSlideAsync(ct)];
        var introOutro = !string.IsNullOrWhiteSpace(o.IntroPath) || !string.IsNullOrWhiteSpace(o.OutroPath);

        var ids = new List<string>();
        for (var i = 0; i < scripts.Count; i++)
        {
            var script = scripts[i];
            var name = InvalidName().Replace(script.Name ?? "", "_").Trim(' ', '.');
            if (name.Length == 0) name = $"Project_{i + 1}";
            if (string.IsNullOrWhiteSpace(script.Text) && !File.Exists(script.Path)) throw new FileNotFoundException($"Kịch bản {name} rỗng hoặc không còn trên máy.", script.Path);

            var input = new JsonObject
            {
                ["outputDir"] = folder,
                ["name"] = name,
                ["slide"] = SlideLibrary.Pick(pool, o.SlideOrder, i),
                ["introOutro"] = introOutro,
                ["stepOptions"] = new JsonObject
                {
                    ["tts"] = PipelineResult.Node(new TtsStepOptions { VoiceId = o.VoiceId, Language = o.Language, Speed = o.Speed }),
                    ["slides"] = PipelineResult.Node(new SlideshowStepOptions
                    {
                        Height = o.Height, KenBurns = o.KenBurns, Fade = o.Fade, Waveform = o.Waveform, WaveformColor = o.WaveformColor
                    }),
                    ["introOutro"] = PipelineResult.Node(new IntroOutroOptions { IntroPath = o.IntroPath, OutroPath = o.OutroPath })
                }
            };
            if (!string.IsNullOrWhiteSpace(script.Text)) input["script"] = script.Text;
            else input["source"] = script.Path!;

            await projects.SaveAsync(ProjectIndex.Youtube, name, Path.Combine(folder, name), s => s["script"] = script.Path ?? "(văn bản dán)", ct);
            ids.Add(await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"YouTube: {name}", PipelineResult.Pipeline("youtube_creator", input), JobLane.Media), ct));
        }

        return ids;
    }

    /// <summary>Không có slide nào: nền tối đơn sắc 1920x1080 sinh một lần trong cache.</summary>
    public async Task<string> DefaultSlideAsync(CancellationToken ct)
    {
        var path = Path.Combine(paths.CacheDir("youtube"), "default_background.jpg");
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ffmpeg.RunAsync(new FfmpegJob(["-f", "lavfi", "-i", "color=c=0x111827:s=1920x1080:d=1", "-frames:v", "1"], path, Label: "Nền mặc định"), null, ct);
        return path;
    }
}
