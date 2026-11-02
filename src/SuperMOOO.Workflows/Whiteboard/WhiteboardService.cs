using System.Text.RegularExpressions;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Steps;

namespace SuperMOOO.Workflows.Whiteboard;

public sealed class WhiteboardOptions
{
    public string? ScriptProvider { get; set; }
    public string? ScriptModel { get; set; }
    public string Style { get; set; } = "whiteboard";
    public string Tone { get; set; } = "friendly";
    public string Language { get; set; } = "vi";
    public int SceneCount { get; set; } = 6;
    public string Aspect { get; set; } = "16:9";
    public string VoiceId { get; set; } = "cpp_vieneu_ngoc_huyen";
    public double Speed { get; set; } = 1.0;
    public string? ImageProvider { get; set; }
    public string? OutputFolder { get; set; }
    public BrandingPreset? Branding { get; set; }
    public string? IntroPath { get; set; }
    public string? OutroPath { get; set; }
    public DeliveryOptions? Delivery { get; set; }
    /// <summary>Thông số dựng (fps, chất lượng, encoder, tốc độ vẽ, fade, giữ cuối cảnh, kiểu tay); mặc định như VideoC.</summary>
    public WhiteboardRenderStepOptions Render { get; set; } = new();
}

/// <summary>S18: mỗi chủ đề một job `whiteboard` (kịch bản → ảnh → giọng → dựng từng cảnh → xuất). Lane Cloud để job sau làm kịch bản/ảnh/giọng khi job trước đang dựng.</summary>
[Service]
public sealed partial class WhiteboardService(IJobQueue jobs, ProjectIndex projects, WhiteboardScriptService scripts, DeliveryService delivery)
{
    private const int MaxTopics = 50;

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidName();

    public Task<IReadOnlyList<string>> SuggestTopicsAsync(string niche, string language, int count = 10, CancellationToken ct = default, string? providerId = null, string? model = null) =>
        scripts.SuggestTopicsAsync(niche, Math.Clamp(count, 1, 20), language, ct, providerId, model);

    public async Task<IReadOnlyList<string>> StartBatchAsync(IReadOnlyList<string> topics, WhiteboardOptions o, CancellationToken ct = default, string? requestId = null)
    {
        var list = topics.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (list.Count is 0 or > MaxTopics) throw new ArgumentException($"Nhập từ 1 đến {MaxTopics} chủ đề.");
        if (o.SceneCount is < 1 or > 30) throw new ArgumentException("Số cảnh từ 1 đến 30.");
        if (o.Aspect is not ("16:9" or "9:16")) throw new ArgumentException("Tỉ lệ khung phải là 16:9 hoặc 9:16.");
        if (o.Speed is < 0.5 or > 2.0) throw new ArgumentException("Tốc độ đọc từ 0.5 đến 2.0.");
        if (o.Render.CustomHandPath is { Length: > 0 } hand && !File.Exists(hand)) throw new FileNotFoundException("Không tìm thấy ảnh tay vẽ tuỳ chọn.", hand);
        if (string.IsNullOrWhiteSpace(o.VoiceId)) throw new ArgumentException("Chưa chọn giọng đọc.");

        foreach (var file in new[] { o.IntroPath, o.OutroPath }.Where(f => !string.IsNullOrWhiteSpace(f)))
            if (!File.Exists(file)) throw new FileNotFoundException("Không tìm thấy intro/outro.", file);
        if (o.Delivery is not null) await delivery.ValidateAsync(o.Delivery, ct);
        var introOutro = !string.IsNullOrWhiteSpace(o.IntroPath) || !string.IsNullOrWhiteSpace(o.OutroPath);

        var folder = o.OutputFolder is { Length: > 0 } f ? f : Path.Combine(projects.OutputRoot(), "Whiteboard");
        var stamp = DateTime.Now.ToString("yyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var ids = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            var topic = list[i];
            var safe = InvalidName().Replace(topic.Length > 50 ? topic[..50] : topic, "_").Trim(' ', '.');
            var name = $"{(safe.Length == 0 ? "video" : safe)}_{stamp}" + (list.Count > 1 ? $"_{i + 1}" : "");

            var input = new JsonObject
            {
                ["outputDir"] = folder,
                ["name"] = name,
                ["topic"] = topic,
                ["aspect"] = o.Aspect,
                ["style"] = o.Style,
                ["introOutro"] = introOutro,
                ["stepOptions"] = new JsonObject
                {
                    ["script"] = PipelineResult.Node(new TopicScriptOptions { Topic = topic, Language = o.Language, SceneCount = o.SceneCount, Tone = o.Tone, ProviderId = o.ScriptProvider, Model = o.ScriptModel }),
                    ["images"] = PipelineResult.Node(new SceneImagesOptions { Provider = o.ImageProvider }),
                    ["voice"] = PipelineResult.Node(new SceneTtsOptions { VoiceId = o.VoiceId, Language = o.Language, Speed = o.Speed }),
                    ["render"] = PipelineResult.Node(o.Render),
                    ["introOutro"] = PipelineResult.Node(new IntroOutroOptions { IntroPath = o.IntroPath, OutroPath = o.OutroPath })
                }
            };
            if (o.Branding is not null) input["branding"] = PipelineResult.Node(o.Branding);
            PipelineResult.AttachDelivery(input, o.Delivery);
            if (requestId is not null)
            {
                input["requestId"] = requestId;
                input["language"] = o.Language;                              // kho thành phẩm lấy language từ đây
            }
            var title = topic.Length > 60 ? topic[..60] + "…" : topic;
            ids.Add(await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Vẽ tay: {title}", PipelineResult.Pipeline("whiteboard", input), JobLane.Cloud), ct));
        }

        return ids;
    }
}
