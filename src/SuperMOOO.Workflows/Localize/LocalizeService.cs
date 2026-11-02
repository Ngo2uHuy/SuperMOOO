using SuperMOOO.AI.Catalog;
using SuperMOOO.Media.Compose;
using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Tools;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Auto;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Steps;
using SuperMOOO.Workflows.Subtitles;

namespace SuperMOOO.Workflows.Localize;

/// <summary>Toàn bộ lựa chọn của S01. Âm lượng là hệ số tuyến tính (UI đổi từ dB).</summary>
public sealed class LocalizeOptions
{
    // Ngôn ngữ & luồng chạy
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "vi";
    public string? Context { get; set; }
    public bool UseAsr { get; set; } = true;
    public bool UseEditedSubtitles { get; set; } = true;
    public bool Translate { get; set; } = true;
    public bool Dub { get; set; } = true;
    public bool BurnSubtitles { get; set; } = true;
    public bool Mirror { get; set; }
    public bool CutMeta { get; set; } = true;
    public bool ReuseCache { get; set; } = true;
    public string Denoise { get; set; } = "Off";          // Off | Fast | Ai
    public string? AsrModel { get; set; }
    /// <summary>SRT có sẵn thay cho nhận diện (Auto).</summary>
    public string? SrtPath { get; set; }
    /// <summary>Xuất video sang lịch đăng sau khi xong (Auto).</summary>
    public DeliveryOptions? Delivery { get; set; }

    // Giọng
    public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    public double Speed { get; set; } = 1.0;
    public bool AutoFit { get; set; } = true;
    public bool SkipFailedSentences { get; set; }

    // Âm thanh
    public bool MuteOriginal { get; set; }
    public double OriginalVolume { get; set; } = 0.15;
    public double VoiceVolume { get; set; } = 1.0;
    public bool SmartAudio { get; set; } = true;
    /// <summary>Tách giọng gốc: tiếng gốc khi trộn là nền đã bỏ lời (giữ nhạc + hiệu ứng).</summary>
    public bool SeparateVocals { get; set; }
    public string SeparationQuality { get; set; } = Core.Models.SeparationQuality.Default;
    public bool BgmEnabled { get; set; }
    public List<string> BgmFiles { get; set; } = [];
    public double BgmVolume { get; set; } = 0.12;

    // Hình
    public string AspectRatio { get; set; } = "original";
    public int? OutputResolution { get; set; }
    public string Fit { get; set; } = "contain";
    public PercentBox? BlurBox { get; set; }
    public int BlurStrength { get; set; } = 25;
    public int BlurFeather { get; set; } = 10;
    public bool BlurOnlyDuringSubtitles { get; set; } = true;
    /// <summary>Làm mờ theo lúc có chữ trong vùng (dò bằng AI worker) thay cho mốc phụ đề mới.</summary>
    public bool BlurOnText { get; set; }
    /// <summary>Xoá phụ đề gốc trong vùng bằng inpaint (chậm) thay cho làm mờ.</summary>
    public bool InpaintSubtitles { get; set; }
    /// <summary>Inpaint nhanh: dùng lại phần đã vẽ cho 2 khung kế (~2× nhanh hơn, nền động có thể hơi vệt).</summary>
    public bool InpaintFast { get; set; }
    public OverlayAsset? Logo { get; set; }
    public OverlayAsset? Canvas { get; set; }
    public OverlayAsset? Pip { get; set; }
    public string? OutroFile { get; set; }

    // Phụ đề
    public SubtitleStyle SubtitleStyle { get; set; } = new(FontSize: 33, PositionY: 82, Bold: true);
    public bool Karaoke { get; set; } = true;
    public string KaraokeColor { get; set; } = "#FF6600";

    // Chế độ "Chỉ phụ đề": che vùng chữ cũ rồi gắn phụ đề mới
    public MaskRegionPreset MaskRegion { get; set; } = MaskRegionPreset.Bottom;
    public MaskStyle MaskStyle { get; set; } = MaskStyle.Blur;
    public bool Mask { get; set; }
    public string SubtitleStylePreset { get; set; } = "CapCutYellow";
    public int SubtitleFontSize { get; set; } = 48;
}

/// <summary>S01: đưa video vào pipeline `localize` / `subtitles` (đơn và theo thư mục), dùng lại phụ đề đã chỉnh ở S02.</summary>
[Service]
public sealed class LocalizeService(IJobQueue jobs, ProjectIndex projects)
{
    public static readonly IReadOnlySet<string> MediaExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi", ".ts", ".flv", ".webm", ".mp3", ".wav" };

    /// <summary>Quét video trong thư mục (recursive = "Quét 2 lớp"), sắp theo tên.</summary>
    public static IReadOnlyList<string> ScanSources(string folder, bool recursive)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Thư mục không tồn tại: {folder}");
        return Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => MediaExtensions.Contains(Path.GetExtension(f))).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<string> StartAsync(string source, LocalizeOptions options, CancellationToken ct = default) =>
        EnqueueAsync("localize", "Lồng tiếng", source, options, ct);

    /// <summary>Mỗi file một job, dùng chung cấu hình (S15 theo dõi từng file).</summary>
    public async Task<IReadOnlyList<string>> StartBatchAsync(IReadOnlyList<string> sources, LocalizeOptions options, CancellationToken ct = default)
    {
        if (sources.Count == 0) throw new ArgumentException("Không có video nào để xử lý.");
        var ids = new List<string>();
        foreach (var source in sources) ids.Add(await StartAsync(source, options, ct));
        return ids;
    }

    /// <summary>Chế độ "Chỉ phụ đề": ASR → [dịch] → SRT/VTT → [che vùng cũ] → [gắn phụ đề].</summary>
    public Task<string> StartSubtitlesAsync(string source, LocalizeOptions options, CancellationToken ct = default) =>
        EnqueueAsync("subtitles", "Phụ đề", source, options, ct);

    /// <summary>Phụ đề đã lưu cho nguồn này (S02 "Áp dụng vào dự án lồng tiếng"), null nếu chưa có.</summary>
    public async Task<IReadOnlyList<SubtitleEntry>?> LoadSavedSubtitlesAsync(string source, CancellationToken ct = default)
    {
        var state = await projects.GetStateAsync(await projects.FolderForAsync(ProjectIndex.Localize, source, ct), ct);
        return state["segmentsFile"]?.GetValue<string>() is { } file && File.Exists(file) ? await SubtitleEntries.ReadAsync(file, ct) : null;
    }

    private async Task<string> EnqueueAsync(string pipeline, string label, string source, LocalizeOptions o, CancellationToken ct)
    {
        var isUrl = SourceKinds.IsUrl(source);
        if (!isUrl && !File.Exists(source)) throw new FileNotFoundException("Không tìm thấy file nguồn.", source);
        var target = LanguageCatalog.LocaleCode(o.TargetLanguage);
        if (target == "auto") throw new ArgumentException("Chọn ngôn ngữ đích.");

        var folder = await projects.FolderForAsync(ProjectIndex.Localize, source, ct);
        var saved = o.UseEditedSubtitles ? await LoadSavedSubtitlesAsync(source, ct) : null;
        if (!string.IsNullOrWhiteSpace(o.SrtPath)) saved = SubtitleEntries.FromDocument(await SrtCodec.ParseFileAsync(o.SrtPath, ct));
        if (!o.UseAsr && saved is null)
            throw new InvalidOperationException("Đã tắt ASR: hãy nhập hoặc lưu phụ đề cho video này (S02) trước.");

        var name = SourceKinds.NameOf(source);
        var input = new JsonObject
        {
            ["source"] = source,
            ["outputDir"] = folder,
            ["name"] = name,
            ["translate"] = o.Translate,
            ["dub"] = o.Dub,
            ["denoise"] = o.Denoise != "Off",
            ["separate"] = o.SeparateVocals && o.Dub && !o.MuteOriginal,
            ["mask"] = o.Mask,
            ["burn"] = o.BurnSubtitles,
            ["burnedText"] = o.BlurBox is not null && (o.BlurOnText || o.InpaintSubtitles),
            ["stepOptions"] = StepOptions(pipeline, o, target)
        };
        if (saved is not null)
            input["segmentsFile"] = await SaveSegmentsAsync(folder, saved, ct);

        SourceKinds.MarkDownload(input);
        PipelineResult.AttachDelivery(input, o.Delivery);
        await projects.SaveAsync(ProjectIndex.Localize, name, folder, s => s["source"] = source, ct);
        return await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"{label}: {name}", PipelineResult.Pipeline(pipeline, input), JobLane.Media), ct);
    }

    private static async Task<string> SaveSegmentsAsync(string folder, IReadOnlyList<SubtitleEntry> entries, CancellationToken ct)
    {
        var file = Path.Combine(folder, "subtitles.json");
        await SubtitleEntries.WriteAsync(file, entries, ct);
        return file;
    }

    private static JsonObject StepOptions(string pipeline, LocalizeOptions o, string target)
    {
        var sourceCode = LanguageCatalog.LocaleCode(o.SourceLanguage).Split('-')[0];
        var steps = new JsonObject
        {
            ["denoise"] = PipelineResult.Node(new DenoiseOptions { Mode = o.Denoise == "Ai" ? "Ai" : "Fast" }),
            ["separate"] = PipelineResult.Node(new SeparateVocalsOptions { Quality = o.SeparationQuality }),
            ["asr"] = PipelineResult.Node(new AsrOptions { Language = sourceCode, Model = o.AsrModel, WordTimestamps = o.Karaoke, ReuseCache = o.ReuseCache }),
            ["translate"] = PipelineResult.Node(new TranslateStepOptions { SourceLanguage = o.SourceLanguage, TargetLanguage = target, Context = o.Context }),
            ["subs"] = PipelineResult.Node(new ExportSubtitlesOptions { Language = o.Translate ? target : sourceCode == "auto" ? "src" : sourceCode })
        };

        if (pipeline == "subtitles")
        {
            steps["mask"] = PipelineResult.Node(new MaskRegionOptions { Region = o.MaskRegion, Style = o.MaskStyle });
            steps["burn"] = PipelineResult.Node(new BurnSubtitlesOptions { StylePreset = o.SubtitleStylePreset, FontSize = o.SubtitleFontSize });
            return steps;
        }

        steps["warm"] = PipelineResult.Node(new WarmTtsOptions { VoiceId = o.VoiceId, Language = o.Translate ? target : null });
        steps["tts"] = PipelineResult.Node(new TtsStepOptions
        {
            VoiceId = o.VoiceId, Language = o.Translate ? target : null, Speed = o.Speed, SkipFailed = o.SkipFailedSentences, DeferSpeed = true
        });
        steps["fit"] = PipelineResult.Node(new FitTempoOptions { AutoFit = o.AutoFit });
        steps["mix"] = PipelineResult.Node(new MixAudioOptions
        {
            OriginalVolume = o.OriginalVolume, VoiceVolume = o.VoiceVolume, MuteOriginal = o.MuteOriginal, SmartDucking = o.SmartAudio,
            BgmPath = PickBgm(o), BgmVolume = o.BgmVolume
        });
        steps["text"] = PipelineResult.Node(new BurnedTextOptions { Box = o.BlurBox, Inpaint = o.InpaintSubtitles, KeyframeEvery = o.InpaintFast ? 3 : 1 });
        steps["compose"] = PipelineResult.Node(new ComposeOptions
        {
            AspectRatio = o.AspectRatio, OutputResolution = o.OutputResolution, Mirror = o.Mirror, Fit = o.Fit,
            Blur = o.BlurBox is { } box ? new BlurRegion(box, o.BlurStrength, o.BlurFeather, o.BlurOnlyDuringSubtitles) : null,
            Canvas = o.Canvas, Logo = o.Logo, Pip = o.Pip, BurnSubtitles = o.BurnSubtitles, Style = o.SubtitleStyle,
            KaraokeColor = o.Karaoke ? o.KaraokeColor : null, CutMeta = o.CutMeta, OutroFile = o.OutroFile
        });
        return steps;
    }

    // Chọn nhạc nền ngẫu nhiên ngay lúc xếp job để retry/resume dùng đúng bài đã chọn.
    private static string? PickBgm(LocalizeOptions o)
    {
        if (!o.BgmEnabled) return null;
        var valid = o.BgmFiles.Where(File.Exists).ToList();
        return valid.Count == 0 ? null : valid[Random.Shared.Next(valid.Count)];
    }
}
