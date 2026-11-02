using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using SuperMOOO.AI.Image;
using SuperMOOO.AI.Tts;
using SuperMOOO.Media.Branding;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Media.Whiteboard;
using SuperMOOO.Workflows.TtsStudio;
using SuperMOOO.Workflows.Whiteboard;

namespace SuperMOOO.Workflows.Steps;

internal sealed record SceneAudio(string Path, double Duration);

internal sealed record SceneClip(string Path, double Duration);

/// <summary>Đọc/ghi file trung gian của pipeline whiteboard trong thư mục làm việc (state chỉ giữ đường dẫn).</summary>
internal static class WhiteboardFiles
{
    public static (string Aspect, int Width, int Height) Frame(StepContext c)
    {
        var aspect = Io.Str(c, "aspect") ?? "16:9";
        var (w, h) = WhiteboardRenderer.FrameSize(aspect);
        return (aspect, w, h);
    }

    /// <summary>Mẫu thương hiệu chụp lúc xếp job (input.branding); job cũ không có thì dùng mặc định (phụ đề đốt, không watermark).</summary>
    public static BrandingPreset Branding(StepContext c) =>
        c.Input["branding"].Deserialize<BrandingPreset>(PipelineResult.Json) ?? new BrandingPreset();

    public static async Task<WhiteboardScript> ReadScriptAsync(StepContext c, CancellationToken ct) =>
        await Read<WhiteboardScript>(Io.State(c, "scriptFile"), ct);

    public static async Task<T> Read<T>(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), PipelineResult.Json) ?? throw new InvalidDataException($"File {Path.GetFileName(path)} rỗng.");

    public static Task Write<T>(string path, T value, CancellationToken ct) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, PipelineResult.Json), ct);

    public static string Dir(StepContext c, string name)
    {
        var dir = Path.Combine(c.WorkFolder, name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed class TopicScriptOptions
{
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    [Required] public string Topic { get; set; } = "";
    public string Language { get; set; } = "vi";
    [Range(1, 30)] public int SceneCount { get; set; } = 6;
    public string Tone { get; set; } = "friendly";
}

/// <summary>Chủ đề → kịch bản các cảnh (lời kể + prompt ảnh) và SEO bằng LLM. Chạy lại thì dùng kịch bản đã có để không gọi (tính phí) lần nữa.</summary>
public sealed class TopicScriptStep(WhiteboardScriptService scripts) : PipelineStep<TopicScriptOptions>
{
    public override string Kind => "topic_script";

    public override bool IsCloudBilled => true;

    protected override async Task<StepOutput> RunAsync(StepContext c, TopicScriptOptions o, CancellationToken ct)
    {
        var file = Io.Work(c, "script.json");
        WhiteboardScript script;
        if (File.Exists(file)) script = await WhiteboardFiles.Read<WhiteboardScript>(file, ct);
        else
        {
            script = await scripts.GenerateAsync(new WhiteboardScriptRequest(o.Topic, o.Language, o.SceneCount, o.Tone, WhiteboardFiles.Frame(c).Aspect, o.ProviderId, o.Model), ct);
            await WhiteboardFiles.Write(file, script, ct);
        }

        var caption = script.Seo.Description + (script.Seo.Hashtags.Count > 0 ? "\n\n" + string.Join(' ', script.Seo.Hashtags) : "");
        return Io.Out([("scriptFile", file), ("title", script.Title), ("sceneCount", script.Scenes.Count.ToString(CultureInfo.InvariantCulture)),
            ("seoTitle", script.Seo.Title), ("seoCaption", caption)]);
    }
}

public sealed class SceneImagesOptions
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
}

/// <summary>Ảnh từng cảnh theo nguồn ảnh trong Cài đặt (hoặc input). Cache ảnh theo prompt nên chạy lại không sinh (không trả phí) lại.</summary>
public sealed class SceneImagesStep(ImageProviderRegistry images, WhiteboardPreviewFeed preview) : PipelineStep<SceneImagesOptions>
{
    public override string Kind => "scene_images";

    public override bool IsCloudBilled => true;

    // Pollinations/SD WebUI miễn phí: lỗi thì cho tự thử lại bình thường, không chuyển sang chờ thử lại thủ công.
    protected override bool IsCloudBilledFor(SceneImagesOptions o) =>
        (o.Provider is { Length: > 0 } id ? images.Get(id) : images.Selected).RequiresKey;

    protected override async Task<StepOutput> RunAsync(StepContext c, SceneImagesOptions o, CancellationToken ct)
    {
        var script = await WhiteboardFiles.ReadScriptAsync(c, ct);
        var (aspect, _, _) = WhiteboardFiles.Frame(c);
        var (width, height) = aspect == "9:16" ? (864, 1536) : (1536, 864);
        var style = WhiteboardStyles.ArtPrompt(Io.Str(c, "style"));
        var provider = o.Provider is { Length: > 0 } id ? images.Get(id) : images.Selected;
        var dir = WhiteboardFiles.Dir(c, "images");
        var paths = new List<string>();

        for (var i = 0; i < script.Scenes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var scenePrompt = script.Scenes[i].ImagePrompt;
            var prompt = $"{scenePrompt}, {style}";
            // Seed theo prompt để tạo lại cùng cảnh ra cùng ảnh.
            var seed = (int)(BitConverter.ToUInt32(SHA1.HashData(Encoding.UTF8.GetBytes(scenePrompt)), 0) % int.MaxValue);
            var result = await provider.GenerateAsync(new ImageRequest(prompt, width, height, seed, null, o.Model), ct);
            var local = Path.Combine(dir, $"scene_{i + 1:000}{Path.GetExtension(result.Path)}");
            File.Copy(result.Path, local, overwrite: true);
            paths.Add(local);
            preview.Publish(local);
            c.Progress.Report(new ProgressInfo((i + 1.0) / script.Scenes.Count, $"ảnh {i + 1}/{script.Scenes.Count}"));
        }

        var file = Io.Work(c, "images.json");
        await WhiteboardFiles.Write(file, paths, ct);
        return Io.Out([("imagesFile", file)]);
    }
}

public sealed class SceneTtsOptions
{
    [Required] public string VoiceId { get; set; } = "cpp_vieneu_ngoc_huyen";
    public string? Language { get; set; }
    [Range(0.5, 2.0)] public double Speed { get; set; } = 1.0;
}

/// <summary>Đọc lời kể từng cảnh (TtsService, có cache) và ghi thời lượng thật để dựng cảnh khớp lời.</summary>
public sealed class SceneTtsStep(VoiceSpeed voice) : PipelineStep<SceneTtsOptions>
{
    public override string Kind => "scene_tts";

    protected override async Task<StepOutput> RunAsync(StepContext c, SceneTtsOptions o, CancellationToken ct)
    {
        var script = await WhiteboardFiles.ReadScriptAsync(c, ct);
        var dir = WhiteboardFiles.Dir(c, "audio");
        var audio = new List<SceneAudio>();

        for (var i = 0; i < script.Scenes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var wav = Path.Combine(dir, $"scene_{i + 1:000}.wav");
            var path = await voice.SynthesizeAsync(new TtsRequest(script.Scenes[i].Narration, o.VoiceId, wav, o.Language), o.Speed, ct);
            var seconds = WavFile.DescribeFile(path).Duration.TotalSeconds;
            if (seconds <= 0.05) throw new InvalidOperationException($"Cảnh {i + 1} không có âm thanh hợp lệ.");
            audio.Add(new SceneAudio(path, seconds));
            c.Progress.Report(new ProgressInfo((i + 1.0) / script.Scenes.Count, $"giọng {i + 1}/{script.Scenes.Count}"));
        }

        var file = Io.Work(c, "audio.json");
        await WhiteboardFiles.Write(file, audio, ct);
        return Io.Out([("audioFile", file), ("narrationSeconds", Io.Fmt(audio.Sum(a => a.Duration)))]);
    }
}

public sealed class WhiteboardRenderStepOptions
{
    [Range(15, 60)] public int Fps { get; set; } = 30;
    [Range(0.5, 3.0)] public double DrawSpeed { get; set; } = 1.0;
    public bool Subtitles { get; set; } = true;
    public string Encoder { get; set; } = "auto";
    public string Quality { get; set; } = "balanced";
    [Range(0, 3)] public double ColorFadeSec { get; set; } = 0.8;
    [Range(0, 2)] public double TailHoldSec { get; set; } = 0.4;
    public WhiteboardHandStyle Hand { get; set; } = WhiteboardHandStyle.GoldPen;
    public string? CustomHandPath { get; set; }
}

/// <summary>Dựng clip từng cảnh tuần tự (nét vẽ + bàn tay + lời đọc, phụ đề ASS đốt vào). Clip đã có thì bỏ qua nên chạy tiếp từ cảnh dang dở.</summary>
public sealed class WhiteboardRenderStep(WhiteboardRenderer renderer, WhiteboardPreviewFeed preview) : PipelineStep<WhiteboardRenderStepOptions>
{
    public override string Kind => "whiteboard_render";

    protected override async Task<StepOutput> RunAsync(StepContext c, WhiteboardRenderStepOptions o, CancellationToken ct)
    {
        var script = await WhiteboardFiles.ReadScriptAsync(c, ct);
        var images = await WhiteboardFiles.Read<List<string>>(Io.State(c, "imagesFile"), ct);
        var audio = await WhiteboardFiles.Read<List<SceneAudio>>(Io.State(c, "audioFile"), ct);
        var (aspect, width, height) = WhiteboardFiles.Frame(c);
        var dir = WhiteboardFiles.Dir(c, "clips");
        var options = new WhiteboardRenderOptions(o.DrawSpeed, o.ColorFadeSec, o.TailHoldSec, o.Hand, o.CustomHandPath, o.Encoder, o.Quality);
        var branding = WhiteboardFiles.Branding(c);
        var logo = BrandingFilters.Logo(branding);
        var burn = o.Subtitles && branding.BurnSubtitles;
        var clips = new List<SceneClip>();
        var cues = new List<SubtitleCue>();
        var offset = 0.0;

        // Fontsize ASS là chiều cao ô chữ nên đo ở cỡ em = Fontsize / CellRatio; viền (hoặc đệm hộp nền) trừ khỏi bề rộng cho phép.
        var sub = branding.Subtitle;
        var emPx = sub.FontSize / TextMeasure.CellRatio(sub.FontName);
        var maxTextW = width * 0.94 - (sub.BoxBackground ? emPx * 0.4 : sub.Outline) * 2;
        double measure(string line) => TextMeasure.Measure(branding.SubtitleUpperCase ? line.ToUpper(CultureInfo.CurrentCulture) : line, sub.FontName, emPx, sub.Bold).Width;

        for (var i = 0; i < script.Scenes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var n = i + 1;
            var duration = audio[i].Duration;
            var clip = Path.Combine(dir, $"scene_{n:000}.mp4");
            var sceneCues = WhiteboardCues.Build(script.Scenes[i].Narration, duration, branding.SubtitleMaxChars, measure, maxTextW);
            if (branding.SubtitleUpperCase) sceneCues = [.. sceneCues.Select(q => q with { Text = q.Text.ToUpper(CultureInfo.CurrentCulture) })];

            if (!File.Exists(clip))
            {
                var filters = new List<string>();
                if (burn && sceneCues.Count > 0)
                {
                    await AssWriter.WriteFileAsync(Path.Combine(dir, $"sub_{n:000}.ass"), new SubtitleDocument(sceneCues), branding.Subtitle, new AssOptions(width, height, "Output"), ct);
                    filters.Add($"ass=filename=sub_{n:000}.ass");
                }

                if (await BrandingFilters.WriteWatermarkAsync(dir, branding, width, height, ct) is { } watermark) filters.Add(watermark);

                // Ghi ra tên tạm rồi đổi tên: clip dở dang do huỷ/lỗi không bị coi là điểm lưu hợp lệ.
                var part = Path.Combine(dir, $"scene_{n:000}.part.mp4");
                var scaled = new Progress<ProgressInfo>(p => c.Progress.Report(new ProgressInfo((i + Math.Max(0, p.Fraction)) / script.Scenes.Count, $"cảnh {n}/{script.Scenes.Count}")));
                await renderer.RenderSceneAsync(new WhiteboardSceneRequest(images[i], duration, audio[i].Path, aspect, o.Fps, part, options, filters, dir, logo), scaled, ct);
                File.Move(part, clip, overwrite: true);
            }

            preview.Publish(images[i], clip);
            var total = duration + options.TailHoldSec;
            clips.Add(new SceneClip(clip, total));
            cues.AddRange(sceneCues.Select(q => q with { Index = cues.Count + 1, Start = q.Start + TimeSpan.FromSeconds(offset), End = q.End + TimeSpan.FromSeconds(offset) }));
            offset += total;
            c.Progress.Report(new ProgressInfo((double)n / script.Scenes.Count, $"cảnh {n}/{script.Scenes.Count}"));
        }

        var clipsFile = Io.Work(c, "clips.json");
        await WhiteboardFiles.Write(clipsFile, clips, ct);
        var srt = Io.Work(c, "subtitles.srt");
        await new SubtitleDocument(cues).SaveAsync(srt, new SrtFormat(), ct);
        return Io.Out([("clipsFile", clipsFile), ("srtFile", srt), ("videoSeconds", Io.Fmt(offset))]);
    }
}

public sealed class WhiteboardFinalizeOptions;

/// <summary>Nối clip cảnh (-c copy), tạo thumbnail, SEO.txt, subtitles.srt, meta.json vào thư mục của video và đăng ký kết quả "whiteboard".</summary>
public sealed class WhiteboardFinalizeStep(FfmpegRunner ffmpeg, WhiteboardThumbnail thumbnails, IOutputRegistry outputs) : PipelineStep<WhiteboardFinalizeOptions>
{
    public override string Kind => "whiteboard_finalize";

    protected override async Task<StepOutput> RunAsync(StepContext c, WhiteboardFinalizeOptions o, CancellationToken ct)
    {
        var script = await WhiteboardFiles.ReadScriptAsync(c, ct);
        var clips = await WhiteboardFiles.Read<List<SceneClip>>(Io.State(c, "clipsFile"), ct);
        var images = await WhiteboardFiles.Read<List<string>>(Io.State(c, "imagesFile"), ct);
        var (aspect, _, _) = WhiteboardFiles.Frame(c);
        var name = Io.Name(c);
        var folder = Path.Combine(Io.OutputDir(c), name);
        Directory.CreateDirectory(folder);

        // Còn intro/outro thì video thân nằm ở thư mục làm việc; bước intro_outro ghi ra finalPath và đăng ký kết quả.
        var introOutro = Io.Flag(c, "introOutro");
        var final = Io.OverwritablePath(folder, name + ".mp4");
        var joined = introOutro ? Io.Work(c, "joined.mp4") : final;

        var manifest = Io.Work(c, "concat.txt");
        await File.WriteAllTextAsync(manifest, string.Join('\n', clips.Select(p => "file '" + p.Path.Replace('\\', '/').Replace("'", "'\\''") + "'")), ct);
        var seconds = clips.Sum(p => p.Duration);
        await ffmpeg.RunAsync(new FfmpegJob(["-f", "concat", "-safe", "0", "-i", manifest, "-c", "copy", "-movflags", "+faststart"], joined,
            TimeSpan.FromSeconds(seconds), Label: "Nối các cảnh"), c.Progress, ct);

        var thumb = Path.Combine(folder, "thumbnail.png");
        var (tw, th) = aspect == "9:16" ? (1080, 1920) : (1280, 720);
        var branding = WhiteboardFiles.Branding(c);
        var thumbDir = WhiteboardFiles.Dir(c, "thumb");
        var thumbFilters = new List<string>();
        if (await BrandingFilters.WriteWatermarkAsync(thumbDir, branding, tw, th, ct) is { } watermark) thumbFilters.Add(watermark);
        await thumbnails.CreateAsync(new WhiteboardThumbnailRequest(images[0], script.ThumbnailText, tw, th, thumb, thumbFilters, thumbDir, BrandingFilters.Logo(branding)), ct);

        var scenesDir = Path.Combine(folder, "scenes");
        Directory.CreateDirectory(scenesDir);
        for (var i = 0; i < images.Count; i++)
            File.Copy(images[i], Path.Combine(scenesDir, $"scene_{i + 1:00}{Path.GetExtension(images[i])}"), overwrite: true);
        File.Copy(Io.State(c, "scriptFile"), Path.Combine(folder, "script.json"), overwrite: true);

        var seo = Path.Combine(folder, "SEO.txt");
        await File.WriteAllTextAsync(seo, SeoText(script), new UTF8Encoding(false), ct);
        // Chế độ "chỉ đốt" hoặc "tắt" thì không xuất file .srt rời.
        var srt = branding.WriteSrt ? Path.Combine(folder, "subtitles.srt") : null;
        if (srt is not null) File.Copy(Io.State(c, "srtFile"), srt, overwrite: true);

        var meta = new JsonObject
        {
            ["id"] = c.JobId,
            ["title"] = script.Title,
            ["topic"] = Io.Str(c, "topic"),
            ["aspect"] = aspect,
            ["scenes"] = script.Scenes.Count,
            ["durationSec"] = Math.Round(seconds, 2),
            ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["files"] = new JsonObject { ["video"] = Path.GetFileName(final), ["thumbnail"] = "thumbnail.png", ["seo"] = "SEO.txt" }
        };
        if (srt is not null) meta["files"]!["srt"] = "subtitles.srt";
        await File.WriteAllTextAsync(Path.Combine(folder, "meta.json"), meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false), ct);

        if (!introOutro) await outputs.RegisterOutputAsync(final, "whiteboard", ct);
        var values = new List<(string, string)> { ("video", joined), ("finalPath", final), ("outputKind", "whiteboard"), ("thumbnail", thumb), ("seoFile", seo) };
        if (srt is not null) values.Add(("subtitleFile", srt));
        var files = new List<string>();
        if (!introOutro) files.Add(final);
        files.AddRange([thumb, seo]);
        if (srt is not null) files.Add(srt);
        return Io.Out(values, [.. files]);
    }

    private static string SeoText(WhiteboardScript script) => string.Join('\n',
        "TIÊU ĐỀ:", script.Seo.Title, "",
        "MÔ TẢ:", script.Seo.Description, "",
        "TAGS (phân cách bằng dấu phẩy):", string.Join(", ", script.Seo.Tags), "",
        "HASHTAGS:", string.Join(' ', script.Seo.Hashtags), "");
}
