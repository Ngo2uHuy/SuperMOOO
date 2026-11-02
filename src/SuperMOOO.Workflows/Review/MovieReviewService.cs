using System.Globalization;
using System.Text.RegularExpressions;
using SuperMOOO.AI.Asr;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;
using SuperMOOO.AI.Tts;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Auto;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Steps;
using SuperMOOO.Workflows.Subtitles;
using SuperMOOO.Workflows.TtsStudio;

namespace SuperMOOO.Workflows.Review;

public sealed record ScriptStyle(string Id, string Label, string Icon);

/// <summary>Toàn bộ lựa chọn của S04 (cũng là phần review/tóm tắt của S07). Âm lượng là hệ số tuyến tính.</summary>
public sealed class ReviewOptions
{
    public string Mode { get; set; } = "review";                    // review | summary | highlights
    public double TargetSeconds { get; set; } = 60;
    public bool Ai { get; set; } = true;
    public string Style { get; set; } = "Kịch tính";
    public string SourceLanguage { get; set; } = "auto";
    public string Language { get; set; } = "vi";
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    public bool Translate { get; set; }
    public bool Narration { get; set; } = true;
    public string VoiceId { get; set; } = "cpp_vieneu_ngoc_huyen";
    public string? DialogVoiceId { get; set; }
    public double Speed { get; set; } = 1.0;
    public string Aspect { get; set; } = "Original";
    public string Fit { get; set; } = "Contain";
    public int Height { get; set; } = 720;
    public double Fade { get; set; } = 0.25;
    public bool Captions { get; set; } = true;
    public string CaptionColor { get; set; } = "#FFCC00";
    public bool CardCaptions { get; set; } = true;
    public double Volume { get; set; } = 1.0;
    public string? MusicPath { get; set; }
    public double MusicVolume { get; set; } = 0.12;
    public ExportMode ExportMode { get; set; } = ExportMode.Montage;
    /// <summary>&gt; 0: chưa có thoại, chia nguồn thành các phần bằng nhau (giây/phần) thay vì nhận diện.</summary>
    public double SegmentSeconds { get; set; }
    /// <summary>SRT có sẵn thay cho nhận diện thoại.</summary>
    public string? SrtPath { get; set; }
    public string? OutputFolder { get; set; }
    public DeliveryOptions? Delivery { get; set; }
}

public sealed record ReviewExportRequest(string Movie, string Name, IReadOnlyList<ReviewScene> Scenes, ReviewOptions Options);

/// <summary>S04: kịch bản AI theo phong cách, sửa từng cảnh rồi xuất (đơn/hàng loạt), hoặc chạy tự động toàn dự án qua pipeline review/summary.</summary>
[Service]
public sealed class MovieReviewService(LlmProviderRegistry llm, AsrService asr, FfmpegRunner ffmpeg, IMediaProbe probe, TtsService tts,
    IJobQueue jobs, ProjectIndex projects, AppPaths paths)
{
    private static readonly Regex ListMarker = new(@"^[-•*\s]*(\d+[\.\):]\s*)?");

    public IReadOnlyList<ScriptStyle> Styles { get; } = Load<List<ScriptStyle>>("script_styles.json");

    public Task<IReadOnlyList<ProjectRecord>> ScanProjectsAsync(CancellationToken ct = default) => projects.ScanAsync(ProjectIndex.Review, ct);

    /// <summary>Lời thoại của phim: SRT nếu có, không thì tách âm thanh và nhận diện theo cấu hình ASR.</summary>
    public async Task<IReadOnlyList<SubtitleEntry>> TranscribeAsync(string movie, string? srtPath, string? language,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (srtPath is { Length: > 0 }) return SubtitleEntries.FromDocument(await SrtCodec.ParseFileAsync(srtPath, ct));
        var info = await probe.ProbeAsync(movie, ct);
        if (info.Audio is null) throw new InvalidOperationException("Phim không có âm thanh; nạp SRT để tiếp tục.");

        var wav = Path.Combine(paths.CacheDir("review"), $"{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
        try
        {
            await ffmpeg.RunAsync(new FfmpegJob(["-i", movie, "-vn", "-map", "0:a:0", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le"], wav, info.Duration, Label: "Tách âm thanh phim"), null, ct);
            var code = LanguageCatalog.LocaleCode(language ?? "auto").Split('-')[0];
            var transcript = await asr.TranscribeAsync(new AsrRequest(wav, code == "auto" ? null : code, WordTimestamps: false), progress, ct);
            var entries = SubtitleEntries.FromTranscript(transcript).Where(e => e.End > e.Start && e.Text.Length > 0).ToList();
            return entries.Count > 0 ? entries : throw new InvalidOperationException("Phim không có nội dung thoại để tóm tắt.");
        }
        finally
        {
            try { File.Delete(wav); } catch (IOException) { /* nằm trong cache, dọn sau */ }
        }
    }

    /// <summary>AI viết lời bình cho `numScenes` cảnh bám diễn biến trong thoại; mốc cảnh chia theo nhịp (không dài hơn phim).</summary>
    public async Task<IReadOnlyList<ReviewScene>> GenerateScriptAsync(string movieName, IReadOnlyList<SubtitleEntry> transcript, string style,
        int numScenes, double scenePaceSeconds, string language, double? movieSeconds, CancellationToken ct,
        string? providerId = null, string? model = null)
    {
        if (numScenes is < 1 or > 50) throw new ArgumentException("Số cảnh từ 1 đến 50.");
        if (transcript.Count == 0) throw new InvalidOperationException("Phim không có nội dung thoại để tóm tắt.");
        var text = string.Join('\n', transcript.Select(e => string.Create(CultureInfo.InvariantCulture, $"[{e.Start:0.0}-{e.End:0.0}] {e.Display}")));
        var prompt = $"Bạn là biên kịch review phim chuyên nghiệp hàng triệu view trên YouTube. Hãy tạo {numScenes} câu lời bình tóm tắt phim cực kỳ cuốn hút bằng {LanguageCatalog.TextLanguage(language)}, " +
                     $"phong cách: {style}. Mỗi câu tương ứng 1 phân cảnh khoảng {scenePaceSeconds:0} giây. Ngắn gọn, hấp dẫn, theo sát diễn biến phim. " +
                     "Trả đúng một JSON object {\"scenes\":[\"lời bình cảnh 1\", \"lời bình cảnh 2\"]}. Chỉ trả nội dung lời bình, không giải thích. " +
                     $"Tên phim: {movieName}. Chỉ dùng diễn biến có trong nội dung sau, không tự bịa:\n{text}";

        var raw = (await (providerId is null ? llm.Selected : llm.Get(providerId)).CompleteAsync(
            new LlmRequest([new ChatMessage(ChatRole.User, prompt)], Model: model, MaxTokens: 8192, Temperature: 0.7, JsonOutput: true), ct)).Text;
        var lines = ParseLines(raw).Take(numScenes).ToList();
        if (lines.Count == 0) throw new InvalidDataException("AI không tạo được kịch bản; thử lại hoặc đổi model.");

        var pace = movieSeconds is { } total ? Math.Min(scenePaceSeconds, total / lines.Count) : scenePaceSeconds;
        return [.. lines.Select((line, i) => new ReviewScene(i * pace, (i + 1) * pace, "", line))];
    }

    /// <summary>Chấp nhận cả JSON {"scenes":[...]} lẫn danh sách đánh số mỗi dòng một cảnh (model không tuân thủ JSON).</summary>
    internal static IEnumerable<string> ParseLines(string raw)
    {
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal)) json = json[(json.IndexOf('\n') + 1)..].TrimEnd('`', '\n', '\r', ' ');
        try
        {
            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("scenes");
            return [.. rows.EnumerateArray().Select(r => (r.ValueKind == JsonValueKind.String ? r.GetString() : r.GetProperty("narration").GetString())?.Trim() ?? "")
                .Where(s => s.Length > 0)];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [.. raw.Split('\n').Select(l => ListMarker.Replace(l.Trim(), "").Trim()).Where(l => l.Length > 12)];
        }
    }

    /// <summary>Nghe thử lời bình của một cảnh (đọc đúng chữ người dùng đã sửa).</summary>
    public async Task<string> PreviewNarrationAsync(string text, string voiceId, string? language, double speed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Lời bình rỗng.");
        Directory.CreateDirectory(paths.CacheDir("preview"));
        var output = Path.Combine(paths.CacheDir("preview"), $"review-{Guid.NewGuid():N}.wav");
        // Giọng native bỏ qua Rate nên chỉnh bằng atempo, giống NarrationStep khi chạy thật.
        var rate = tts.SupportsRate(voiceId) ? speed : 1.0;
        var path = (await tts.SynthesizeAsync(new TtsRequest(text, voiceId, output, language, rate), ct)).Path;
        return Math.Abs(rate - speed) < 0.001 ? path : await VoiceSpeed.ApplyAsync(ffmpeg, path, speed, ct);
    }

    /// <summary>Chạy tự động toàn dự án: nhận diện (hoặc chia đều) → chọn cảnh + lời bình → TTS → dựng.</summary>
    public Task<string> StartAsync(string source, ReviewOptions o, CancellationToken ct = default) =>
        EnqueueAsync(source, null, null, o, ct);

    /// <summary>Xuất đúng các cảnh đã sửa tay: bỏ qua nhận diện và chọn cảnh.</summary>
    public Task<string> ExportAsync(ReviewExportRequest request, CancellationToken ct = default)
    {
        if (request.Scenes.Count == 0) throw new InvalidOperationException("Chưa có cảnh nào để xuất.");
        if (!request.Scenes.Any(s => !string.IsNullOrWhiteSpace(s.Narration)) && request.Options.Narration)
            throw new InvalidOperationException("Kịch bản lời bình rỗng.");
        return EnqueueAsync(request.Movie, request.Name, request.Scenes, request.Options, ct);
    }

    public async Task<IReadOnlyList<string>> ExportBatchAsync(IReadOnlyList<ReviewExportRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count is 0 or > 50) throw new ArgumentException("Hàng loạt nhận từ 1 đến 50 dự án.");
        var ids = new List<string>();
        foreach (var r in requests) ids.Add(await ExportAsync(r, ct));
        return ids;
    }

    private async Task<string> EnqueueAsync(string source, string? name, IReadOnlyList<ReviewScene>? scenes, ReviewOptions o, CancellationToken ct)
    {
        var isUrl = SourceKinds.IsUrl(source);
        if (!isUrl && !File.Exists(source)) throw new FileNotFoundException("Chọn video phim nguồn hợp lệ để xuất review.", source);
        if (!StoryboardService.Modes.Contains(o.Mode)) throw new ArgumentException("Chọn kiểu dựng: review, summary hoặc highlights.");
        if (o.SegmentSeconds != 0 && o.SegmentSeconds is < 5 or > 600) throw new ArgumentException("Độ dài mỗi phần từ 5 đến 600 giây.");
        if (!string.IsNullOrWhiteSpace(o.SrtPath) && !File.Exists(o.SrtPath)) throw new FileNotFoundException("Không tìm thấy file SRT.", o.SrtPath);

        name ??= isUrl ? $"review_{DateTime.Now:yyyyMMdd_HHmmss}" : Path.GetFileNameWithoutExtension(source);
        var folder = o.OutputFolder is { Length: > 0 } f ? f
            : isUrl ? Path.Combine(projects.OutputRoot(), "Review", name) : await projects.FolderForAsync(ProjectIndex.Review, source, ct);
        var transcribe = scenes is null && o.SegmentSeconds == 0;
        var target = LanguageCatalog.LocaleCode(o.Language);

        var input = new JsonObject
        {
            ["source"] = source,
            ["outputDir"] = folder,
            ["name"] = name,
            ["transcribe"] = transcribe,
            ["segment"] = scenes is null && o.SegmentSeconds > 0,
            ["translate"] = transcribe && o.Translate,
            ["narrate"] = o.Narration,
            ["stepOptions"] = new JsonObject
            {
                ["segment"] = PipelineResult.Node(new SegmentSourceOptions { Seconds = o.SegmentSeconds > 0 ? o.SegmentSeconds : 60 }),
                ["asr"] = PipelineResult.Node(new AsrOptions { Language = LanguageCatalog.LocaleCode(o.SourceLanguage).Split('-')[0], WordTimestamps = false }),
                ["translate"] = PipelineResult.Node(new TranslateStepOptions { SourceLanguage = o.SourceLanguage, TargetLanguage = target }),
                ["storyboard"] = PipelineResult.Node(new StoryboardStepOptions
                {
                    Mode = o.Mode, TargetSeconds = o.TargetSeconds, Ai = o.Ai, Style = o.Style, Language = target, Narration = o.Narration,
                    ProviderId = o.ProviderId, Model = o.Model
                }),
                ["narration"] = PipelineResult.Node(new NarrationOptions { VoiceId = o.VoiceId, DialogVoiceId = o.DialogVoiceId, Language = target, Speed = o.Speed }),
                ["render"] = PipelineResult.Node(new RenderProjectOptions
                {
                    Aspect = o.Aspect, Fit = o.Fit, Height = o.Height, Fade = o.Fade, Captions = o.Captions, CaptionColor = o.CaptionColor,
                    CardCaptions = o.CardCaptions, Volume = o.Volume, MusicPath = o.MusicPath, MusicVolume = o.MusicVolume, ExportMode = o.ExportMode
                })
            }
        };
        if (scenes is not null) input["scenes"] = PipelineResult.Node(scenes);
        if (transcribe && !string.IsNullOrWhiteSpace(o.SrtPath))
        {
            var file = Path.Combine(folder, "subtitles.json");
            await SubtitleEntries.WriteAsync(file, SubtitleEntries.FromDocument(await SrtCodec.ParseFileAsync(o.SrtPath, ct)), ct);
            input["segmentsFile"] = file;
        }

        SourceKinds.MarkDownload(input);
        PipelineResult.AttachDelivery(input, o.Delivery);
        await projects.SaveAsync(ProjectIndex.Review, name, folder, s => s["source"] = source, ct);
        var pipeline = o.Mode == "summary" ? "summary" : "review";
        return await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Review: {name}", PipelineResult.Pipeline(pipeline, input), JobLane.Media), ct);
    }

    private static T Load<T>(string file)
    {
        var assembly = typeof(MovieReviewService).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
