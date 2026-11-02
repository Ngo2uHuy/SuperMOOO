using System.ComponentModel.DataAnnotations;
using SuperMOOO.Core.Operations;
using SuperMOOO.Workflows.Steps;

namespace SuperMOOO.Workflows.Shorts;

public sealed class ShortsRequest
{
    [Required, Field("Tệp video nguồn", Kind = FieldKind.File)] public string InputPath { get; init; } = "";
    [Field("Chế độ", Kind = FieldKind.Choice, Choices = ["Shorts", "Hook"], ChoiceLabels = ["Shorts (đoạn hay 15–60 giây)", "Hook (đoạn mở đầu gây chú ý, tối đa 10 giây)"],
        Help = "Hook bỏ qua độ dài tối thiểu/tối đa bên dưới.")] public string Mode { get; init; } = "Shorts";
    [Range(1, 20), Field("Số đoạn")] public int Count { get; init; } = 3;
    [Range(5, 120), Field("Độ dài tối thiểu (giây)")] public double MinSeconds { get; init; } = 15;
    [Range(10, 180), Field("Độ dài tối đa (giây)")] public double MaxSeconds { get; init; } = 60;
    [Field("Dùng AI chọn đoạn", Help = "Gọi LLM đã chọn ở Cài đặt > Model AI (có thể tính phí). Tắt hoặc chưa cấu hình thì chọn theo mật độ lời thoại.")] public bool UseLlm { get; init; } = true;
    [Field("Nhà cung cấp AI", Kind = FieldKind.Choice,
        Choices = ["", "openai", "gemini", "groq", "openrouter", "deepseek", "hf", "anthropic", "openai_compat", "local", "bundled"],
        ChoiceLabels = ["Theo Cài đặt > Model AI", "OpenAI", "Google Gemini", "Groq", "OpenRouter", "DeepSeek", "Hugging Face", "Anthropic", "OpenAI-compatible", "Máy chủ cục bộ", "Qwen3 chạy máy"])]
    public string ProviderId { get; init; } = "";
    [Field("Model AI", Help = "Để trống để dùng model mặc định của nhà cung cấp đã chọn.")] public string Model { get; init; } = "";
    [Field("Ngôn ngữ video nguồn", Example = "auto", Help = "Mã ngôn ngữ (vi, en, zh…) hoặc auto.")] public string Language { get; init; } = "auto";
    [Field("Ngôn ngữ tiêu đề / hook", Example = "vi")] public string TitleLanguage { get; init; } = "vi";
    [Field("Khung hình", Kind = FieldKind.Choice, Choices = ["Blur", "Fill"], ChoiceLabels = ["Giữ trọn khung hình, nền mờ", "Lấp đầy 9:16 (cắt giữa)"])] public string Fit { get; init; } = "Blur";
    [Field("Đốt phụ đề")] public bool Captions { get; init; } = true;
    [Field("Kiểu phụ đề", Kind = FieldKind.Choice, Choices = ["CapCutYellow", "NeonCyan", "ClassicWhite", "BlackBox"],
        ChoiceLabels = ["CapCut vàng", "Neon cyan", "Trắng cổ điển", "Nền đen"])] public string CaptionStyle { get; init; } = "CapCutYellow";
    [Field("Chữ tiêu đề / hook ở đầu video", Help = "Hiện 3,5 giây đầu.")] public bool TitleOverlay { get; init; } = true;
    [Field("Thư mục xuất", Kind = FieldKind.Folder, Help = "Để trống = Videos\\SuperMOOO\\MediaTools (đổi ở Cài đặt). Kết quả nằm trong thư mục con mới.")] public string? OutputFolder { get; init; }
}

public sealed record ShortsQueued(string JobId, string Message);

/// <summary>Xếp job pipeline `shorts` (nhận diện → chọn đoạn → dựng); tiến độ hiện ở khung Tác vụ của S13.</summary>
[Service]
[Operation("media.shorts_endpoint", "Cắt Shorts / Hook", OperationGroup.Media)]
public sealed class ShortsOperation(IJobQueue jobs, ISettingsStore settings) : IOperationHandler<ShortsRequest, ShortsQueued>
{
    public async Task<ShortsQueued> HandleAsync(ShortsRequest r, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!File.Exists(r.InputPath)) throw new FileNotFoundException("Chọn video nguồn hợp lệ.", r.InputPath);
        if (r.Mode != "Hook" && r.MaxSeconds < r.MinSeconds) throw new ArgumentException("Độ dài tối đa phải lớn hơn hoặc bằng tối thiểu.");

        var name = Path.GetFileNameWithoutExtension(r.InputPath);
        var root = !string.IsNullOrWhiteSpace(r.OutputFolder) ? r.OutputFolder
            : Path.Combine(settings.Get<PathsSettings>("paths").OutputRoot is { Length: > 0 } o ? o : AppPaths.DefaultOutputRoot, "MediaTools");
        var folder = Path.Combine(root, name + "_shorts");
        for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(root, $"{name}_shorts_{n}");

        var input = new JsonObject
        {
            ["source"] = r.InputPath,
            ["outputDir"] = folder,
            ["name"] = name,
            ["stepOptions"] = new JsonObject
            {
                ["asr"] = PipelineResult.Node(new AsrOptions { Language = r.Language.Trim().Length == 0 ? "auto" : r.Language.Trim(), WordTimestamps = true }),
                ["select"] = PipelineResult.Node(new ShortsSelectOptions
                {
                    Count = r.Count, MinSeconds = r.MinSeconds, MaxSeconds = Math.Max(r.MaxSeconds, r.MinSeconds), HookMode = r.Mode == "Hook",
                    UseLlm = r.UseLlm, Language = r.TitleLanguage,
                    ProviderId = string.IsNullOrWhiteSpace(r.ProviderId) ? null : r.ProviderId,
                    Model = string.IsNullOrWhiteSpace(r.Model) ? null : r.Model.Trim()
                }),
                ["render"] = PipelineResult.Node(new ShortsRenderOptionsStep
                {
                    BlurFill = r.Fit != "Fill", Captions = r.Captions, CaptionStyle = r.CaptionStyle, TitleOverlay = r.TitleOverlay
                })
            }
        };
        var id = await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Shorts: {name}", PipelineResult.Pipeline("shorts", input), JobLane.Media), ct);
        return new ShortsQueued(id, "Đã đưa vào hàng đợi; theo dõi ở khung Tác vụ của màn này.");
    }
}
