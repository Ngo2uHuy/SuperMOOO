using SuperMOOO.AI.Catalog;
using SuperMOOO.Media.Effects;
using SuperMOOO.Workflows.Creator;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Localize;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Reup;
using SuperMOOO.Workflows.Review;
using SuperMOOO.Workflows.Steps;
using SuperMOOO.Workflows.TtsStudio;

namespace SuperMOOO.Workflows.Auto;

public static class AutoModes
{
    public const string Localize = "localize";
    public const string Subtitles = "subtitles";
    public const string Script = "script";
    public const string Review = "review";
    public const string Summary = "summary";
    public const string Reup = "reup";
    public const string Voice = "voice";
    public const string Audio = "audio";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Localize, "Lồng tiếng video"), (Subtitles, "Tạo & dịch phụ đề"), (Script, "Kịch bản → video"), (Review, "Review phim"),
        (Summary, "Tóm tắt phim"), (Reup, "Video → bản xuất mới"), (Voice, "Văn bản → giọng đọc"), (Audio, "Xử lý audio")
    ];

    /// <summary>Mode của bản cũ không còn: lý do hiển thị cho người dùng.</summary>
    public static readonly IReadOnlyDictionary<string, string> Removed = new Dictionary<string, string>
    {
        ["ai_video"] = "Chế độ \"Mô tả → video AI\" đã bỏ vì ArtCraft đảm nhiệm việc sinh video. Xuất video từ ArtCraft rồi đưa file vào bất kỳ chế độ nào, hoặc dùng \"Kịch bản → video\" với video nền có sẵn.",
        ["timeline"] = "Chế độ \"Dự án timeline → video\" đã bỏ khỏi Auto A→Z. Dựng và xuất dự án timeline ở màn S17 \"Dựng video / Timeline\"; Review/Tóm tắt phim tự dựng video từ nguồn."
    };
}

/// <summary>Yêu cầu Auto A→Z; trường nào không thuộc mode đang chọn thì bị bỏ qua. Âm lượng là hệ số tuyến tính.</summary>
public sealed class AutoRequest
{
    public string Mode { get; set; } = AutoModes.Localize;
    /// <summary>File, thư mục (quét theo loại file của mode) hoặc URL video/audio.</summary>
    public List<string> Sources { get; set; } = [];
    /// <summary>Kịch bản/văn bản đọc (mode script, voice) khi không dùng file.</summary>
    public string Text { get; set; } = "";
    public string? SrtPath { get; set; }
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "vi";
    public bool Translate { get; set; }
    public bool Ai { get; set; } = true;
    public bool Narration { get; set; } = true;
    public double TargetSeconds { get; set; } = 60;
    public string Style { get; set; } = "Kịch tính";
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    public string VoiceId { get; set; } = "cpp_vieneu_ngoc_huyen";
    public double Speed { get; set; } = 1.0;
    public string Aspect { get; set; } = "16:9";
    public int Resolution { get; set; } = 720;
    public string Fit { get; set; } = "contain";
    public double Volume { get; set; } = 0.15;
    public string? MusicPath { get; set; }
    public double MusicVolume { get; set; } = 0.12;
    public bool Captions { get; set; } = true;
    /// <summary>Mode script: ảnh nền, hoặc video có sẵn (ưu tiên) thay cho slide ảnh.</summary>
    public string? Slide { get; set; }
    public string? BackgroundVideo { get; set; }
    public string ReupPreset { get; set; } = "tiktok";
    public string Operation { get; set; } = "trim_silence";             // trim_silence | denoise | vad
    public string? OutputFolder { get; set; }
    public DeliveryOptions? Delivery { get; set; }
}

public sealed record AutoJob(string JobId, string Source, string Mode);

/// <summary>Danh sách job do Auto xếp (S07 hiển thị); lưu bền để vẫn còn sau khi mở lại app.</summary>
public sealed class AutoJobsSettings
{
    public List<string> Ids { get; set; } = [];
}

/// <summary>S07: 8 chế độ → pipeline tương ứng. Nguồn URL tự thêm bước tải; mọi mode nhận video có sẵn (vd từ ArtCraft); mode script có thể dùng video nền thay slide.</summary>
[Service]
public sealed class AutoWorkflowService(LocalizeService localize, ReupStudioService reup, MovieReviewService review, TtsStudioService voice,
    ScriptLibrary scripts, YouTubeCreatorService creator, DeliveryService delivery, IJobQueue jobs, ProjectIndex projects, ISettingsStore settings)
{
    public const int MaxSources = 50;
    private const int KeepJobs = 300;
    private const string SettingsKey = "auto.jobs";

    private static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".webm", ".avi", ".wav", ".mp3", ".m4a" };

    private static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".m4a", ".flac", ".ogg" };

    public async Task<IReadOnlyList<AutoJob>> StartAsync(AutoRequest r, CancellationToken ct = default)
    {
        var sources = await PreflightAsync(r, ct);
        var started = new List<AutoJob>();
        var singleText = sources.Count == 0;
        foreach (var source in singleText ? [""] : sources)
        {
            var id = r.Mode switch
            {
                AutoModes.Localize => await localize.StartAsync(source, LocalizeFor(r), ct),
                AutoModes.Subtitles => await localize.StartSubtitlesAsync(source, LocalizeFor(r), ct),
                AutoModes.Review or AutoModes.Summary => await review.StartAsync(source, ReviewFor(r), ct),
                AutoModes.Reup => await reup.StartExportAsync(new ReupExportRequest(source, ReupProfile(r), r.OutputFolder, null, r.Delivery), ct),
                AutoModes.Script => await StartScriptAsync(r, source, ct),
                AutoModes.Voice => await StartVoiceAsync(r, source, ct),
                _ => await StartAudioAsync(r, source, ct)
            };
            started.Add(new AutoJob(id, source, r.Mode));
        }

        await RememberAsync(started.Select(j => j.JobId), ct);
        return started;
    }

    /// <summary>Kiểm tra trước khi xếp job và trả danh sách nguồn đã mở rộng (thư mục → file, bỏ trùng). Mode đã bỏ bị từ chối kèm lý do.</summary>
    public async Task<IReadOnlyList<string>> PreflightAsync(AutoRequest r, CancellationToken ct = default)
    {
        if (AutoModes.Removed.TryGetValue(r.Mode, out var why)) throw new NotSupportedException(why);
        if (AutoModes.All.All(m => m.Key != r.Mode)) throw new ArgumentException($"Chế độ \"{r.Mode}\" không tồn tại.");

        var textMode = r.Mode is AutoModes.Script or AutoModes.Voice;
        var sources = Expand(r);
        if (sources.Count == 0 && !(textMode && !string.IsNullOrWhiteSpace(r.Text)))
            throw new InvalidOperationException(textMode ? "Nhập văn bản hoặc chọn file TXT/DOCX." : "Thêm video/audio hoặc thư mục nguồn.");

        if (r.Delivery is not null)
        {
            if (r.Mode is AutoModes.Subtitles or AutoModes.Voice or AutoModes.Audio)
                throw new InvalidOperationException("Lịch đăng chỉ nhận chế độ xuất video, không nhận audio/phụ đề riêng.");
            await delivery.ValidateAsync(r.Delivery, ct);
        }

        foreach (var path in new[] { r.MusicPath, r.Slide, r.BackgroundVideo, r.SrtPath }.Where(p => !string.IsNullOrWhiteSpace(p)))
            if (!File.Exists(path)) throw new FileNotFoundException($"Không tìm thấy file: {path}", path);
        if (r.SrtPath is not null && sources.Count > 1) throw new InvalidOperationException("SRT có sẵn chỉ dùng cho một nguồn.");
        if (LanguageCatalog.LocaleCode(r.TargetLanguage) == "auto") throw new ArgumentException("Chọn ngôn ngữ đích.");
        return sources;
    }

    /// <summary>Job Auto gần nhất trước.</summary>
    public async Task<IReadOnlyList<JobSnapshot>> ListJobsAsync(CancellationToken ct = default)
    {
        var ids = settings.Get<AutoJobsSettings>(SettingsKey).Ids;
        if (ids.Count == 0) return [];
        var found = (await jobs.ListAsync(new JobQuery(Limit: ids.Count, Ids: ids), ct)).ToDictionary(j => j.Id);
        return [.. ids.AsEnumerable().Reverse().Where(found.ContainsKey).Select(id => found[id])];
    }

    public Task CancelAsync(string jobId) => jobs.CancelAsync(jobId);

    public Task<string> RetryAsync(string jobId, bool resumeFromFailedStep = true) => jobs.RetryAsync(jobId, resumeFromFailedStep);

    private List<string> Expand(AutoRequest r)
    {
        var textMode = r.Mode is AutoModes.Script or AutoModes.Voice;
        var extensions = textMode ? ScriptLibrary.Extensions : r.Mode == AutoModes.Audio ? AudioExtensions : VideoExtensions;
        var result = new List<string>();
        foreach (var raw in r.Sources.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()))
        {
            if (SourceKinds.IsUrl(raw))
            {
                if (textMode) throw new InvalidOperationException("Kịch bản cần file TXT/DOCX hoặc văn bản; URL dùng cho video/audio.");
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0) throw new ArgumentException($"URL nguồn không hợp lệ: {raw}");
                result.Add(raw);
            }
            else if (Directory.Exists(raw))
                result.AddRange(Directory.EnumerateFiles(raw).Where(f => extensions.Contains(Path.GetExtension(f))).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(raw)) result.Add(Path.GetFullPath(raw));
            else throw new FileNotFoundException($"Không tìm thấy nguồn: {raw}", raw);
        }

        result = [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
        if (result.Count > MaxSources || (r.Sources.Count > 0 && result.Count == 0))
            throw new InvalidOperationException($"Hàng đợi từ 1–{MaxSources} file được hỗ trợ.");
        return result;
    }

    private async Task RememberAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var state = settings.Get<AutoJobsSettings>(SettingsKey);
        state.Ids = [.. state.Ids.Concat(ids).TakeLast(KeepJobs)];
        await settings.SetAsync(SettingsKey, state, ct);
    }

    private static LocalizeOptions LocalizeFor(AutoRequest r) => new()
    {
        SourceLanguage = r.SourceLanguage, TargetLanguage = r.TargetLanguage, Translate = r.Translate, Dub = r.Mode == AutoModes.Localize,
        VoiceId = r.VoiceId, Speed = r.Speed, BurnSubtitles = r.Captions, AspectRatio = r.Aspect.ToLowerInvariant(), OutputResolution = r.Resolution,
        Fit = r.Fit, OriginalVolume = Math.Min(r.Volume, 1), BgmEnabled = !string.IsNullOrWhiteSpace(r.MusicPath),
        BgmFiles = string.IsNullOrWhiteSpace(r.MusicPath) ? [] : [r.MusicPath], BgmVolume = r.MusicVolume,
        UseAsr = string.IsNullOrWhiteSpace(r.SrtPath), UseEditedSubtitles = false, SrtPath = r.SrtPath, Delivery = r.Delivery
    };

    private static ReviewOptions ReviewFor(AutoRequest r) => new()
    {
        Mode = r.Mode, TargetSeconds = r.TargetSeconds, Ai = r.Ai, Style = r.Style, SourceLanguage = r.SourceLanguage, Language = r.TargetLanguage,
        ProviderId = r.ProviderId, Model = r.Model,
        Translate = r.Translate, Narration = r.Narration, VoiceId = r.VoiceId, Speed = r.Speed, Aspect = r.Aspect, Fit = r.Fit, Height = r.Resolution,
        Captions = r.Captions, Volume = Math.Min(r.Volume, 1), MusicPath = r.MusicPath, MusicVolume = r.MusicVolume, SrtPath = r.SrtPath,
        OutputFolder = r.OutputFolder, Delivery = r.Delivery
    };

    private ReupEffectProfile ReupProfile(AutoRequest r)
    {
        var p = reup.DefaultProfile(r.ReupPreset);
        return p with
        {
            Canvas = p.Canvas with { Aspect = r.Aspect.Equals("original", StringComparison.OrdinalIgnoreCase) ? "Original" : r.Aspect, Fit = char.ToUpperInvariant(r.Fit[0]) + r.Fit[1..].ToLowerInvariant() },
            Video = p.Video with { Speed = r.Speed },
            Audio = p.Audio with { OriginalVolume = r.Volume, BgmPath = r.MusicPath, BgmVolume = r.MusicVolume },
            Output = p.Output with { Height = r.Resolution }
        };
    }

    private async Task<string> StartScriptAsync(AutoRequest r, string source, CancellationToken ct)
    {
        var name = source.Length > 0 ? Path.GetFileNameWithoutExtension(source) : $"script_{DateTime.Now:yyyyMMdd_HHmmss}";
        var folder = r.OutputFolder is { Length: > 0 } f ? f : Path.Combine(projects.OutputRoot(), "Auto", "Script");
        var background = !string.IsNullOrWhiteSpace(r.BackgroundVideo);
        var slide = background ? null : string.IsNullOrWhiteSpace(r.Slide) ? await creator.DefaultSlideAsync(ct) : r.Slide;

        var input = new JsonObject
        {
            ["outputDir"] = folder,
            ["name"] = name,
            ["slideshow"] = !background,
            ["background"] = background,
            ["burn"] = r.Captions,
            ["stepOptions"] = new JsonObject
            {
                ["tts"] = PipelineResult.Node(new TtsStepOptions { VoiceId = r.VoiceId, Language = LanguageCatalog.LocaleCode(r.TargetLanguage), Speed = r.Speed }),
                ["slides"] = PipelineResult.Node(new SlideshowStepOptions { Height = r.Resolution }),
                ["background"] = PipelineResult.Node(new BackgroundVideoOptions { Height = r.Resolution })
            }
        };
        if (slide is not null) input["slide"] = slide;
        if (background) input["backgroundVideo"] = r.BackgroundVideo!;
        if (source.Length > 0) input["source"] = source;
        else input["script"] = r.Text;
        PipelineResult.AttachDelivery(input, r.Delivery);
        return await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Kịch bản → video: {name}", PipelineResult.Pipeline("script_to_video", input), JobLane.Media), ct);
    }

    private async Task<string> StartVoiceAsync(AutoRequest r, string source, CancellationToken ct)
    {
        var text = source.Length > 0 ? await scripts.ReadAsync(source, ct) : r.Text;
        return await voice.StartAsync(new TtsStudioRequest(text, r.VoiceId, LanguageCatalog.LocaleCode(r.TargetLanguage), r.Speed,
            OutputFolder: r.OutputFolder, Name: source.Length > 0 ? Path.GetFileNameWithoutExtension(source) : null), ct);
    }

    private async Task<string> StartAudioAsync(AutoRequest r, string source, CancellationToken ct)
    {
        if (r.Operation is not ("trim_silence" or "denoise" or "vad")) throw new ArgumentException("Thao tác audio phải là trim_silence, denoise hoặc vad.");
        var name = SourceKinds.NameOf(source);
        var folder = r.OutputFolder is { Length: > 0 } f ? f : Path.Combine(projects.OutputRoot(), "Auto", "Audio");
        var input = new JsonObject
        {
            ["source"] = source,
            ["outputDir"] = folder,
            ["name"] = name,
            ["denoise"] = r.Operation == "denoise",
            ["vad"] = r.Operation == "vad",
            ["trim"] = r.Operation == "trim_silence",
            ["export"] = r.Operation != "vad",
            ["stepOptions"] = new JsonObject { ["vad"] = PipelineResult.Node(new VadOptions { Export = true }) }
        };
        SourceKinds.MarkDownload(input);
        return await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Audio: {name}", PipelineResult.Pipeline("audio", input), JobLane.Media), ct);
    }
}
