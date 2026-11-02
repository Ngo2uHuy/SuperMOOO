using SuperMOOO.Core.Publishing;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Talkshow;
using SuperMOOO.Workflows.Whiteboard;

namespace SuperMOOO.Workflows.Library;

/// <summary>Chọn owner của job sản xuất: có chiến dịch thì "campaign:&lt;id&gt;" (tồn kho/lịch của chiến dịch theo dõi), không thì màn tương ứng.</summary>
internal static class ProducerOwner
{
    public static IDisposable Begin(ProductionOrder order, string screenId) =>
        JobOwner.Begin(order.CampaignId is { Length: > 0 } id ? "campaign:" + id : JobOwner.Screen(screenId));

    public static string OutputFolder(ProjectIndex projects, ProductionOrder order) => Path.Combine(projects.OutputRoot(), "Production", order.RequestId);
}

/// <summary>Producer Talkshow (AI viết kịch bản → pipeline `talkshow`), chỉ 16:9. Yêu cầu đã có job thì trả lại job cũ, không xếp thêm.</summary>
[Service(As = [typeof(IContentProducer)])]
public sealed class TalkshowProducer(TalkshowService talkshow, ProjectIndex projects, IProductionRepository productions) : IContentProducer
{
    private const int DefaultMinutes = 3;

    public string Id => Producers.AIVideoStudio;
    public string DisplayName => "Talkshow (AI viết kịch bản)";
    public IReadOnlyList<string> SupportedAspects { get; } = [ProductionAspects.Landscape];

    public async Task<string> EnqueueAsync(ProductionOrder order, CancellationToken ct)
    {
        if (order.Aspect != ProductionAspects.Landscape) throw new InvalidOperationException("Luồng Talkshow chỉ xuất 16:9.");
        if (await productions.GetAsync(order.RequestId, ct) is { ProducerJobId: { Length: > 0 } existing }) return existing;

        var minutes = order.TargetDurationSec is { } s ? Math.Max(1, (s + 59) / 60) : DefaultMinutes;
        var prompt = string.IsNullOrWhiteSpace(order.Notes) ? "" : order.Notes;
        using var _ = ProducerOwner.Begin(order, "S03");
        var turns = await talkshow.GenerateScriptAsync(order.Topic, prompt, minutes, order.Language, null, ct);
        var project = new TalkshowProject
        {
            Title = order.Topic.Length > 60 ? order.Topic[..60] : order.Topic, Topic = order.Topic, Prompt = prompt, Language = order.Language, Turns = [.. turns],
            OutputFolder = ProducerOwner.OutputFolder(projects, order)
        };
        return await talkshow.StartAsync(project, ct, order.RequestId);
    }
}

/// <summary>Producer Whiteboard (pipeline `whiteboard`) 16:9 hoặc 9:16; số cảnh/giọng/phong cách lấy mặc định của S18.</summary>
[Service(As = [typeof(IContentProducer)])]
public sealed class WhiteboardProducer(WhiteboardService whiteboard, ProjectIndex projects, IProductionRepository productions) : IContentProducer
{
    public string Id => Producers.VideoC;
    public string DisplayName => "Video vẽ tay (whiteboard)";
    public IReadOnlyList<string> SupportedAspects { get; } = [ProductionAspects.Landscape, ProductionAspects.Portrait];

    public async Task<string> EnqueueAsync(ProductionOrder order, CancellationToken ct)
    {
        if (!SupportedAspects.Contains(order.Aspect)) throw new InvalidOperationException("Tỉ lệ khung hình không được hỗ trợ.");
        if (await productions.GetAsync(order.RequestId, ct) is { ProducerJobId: { Length: > 0 } existing }) return existing;

        var options = new WhiteboardOptions { Language = order.Language, Aspect = order.Aspect, OutputFolder = ProducerOwner.OutputFolder(projects, order) };
        using var _ = ProducerOwner.Begin(order, "S18");
        var ids = await whiteboard.StartBatchAsync([order.Topic], options, ct, order.RequestId);
        return ids.Count > 0 ? ids[0] : throw new InvalidOperationException("Không tạo được job.");
    }
}
