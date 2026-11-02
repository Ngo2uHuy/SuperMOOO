using Microsoft.Extensions.Logging;
using Quartz;
using SuperMOOO.Publishing.Production;

namespace SuperMOOO.Publishing.Automation;

/// <summary>Lập lịch chiến dịch mỗi phút: bài trễ → điều kiện tự dừng → chọn nội dung/slot → ghi bài (tôn trọng automation_paused).</summary>
[ScheduledJob("10 * * * * ?")]
[DisallowConcurrentExecution]
public sealed class CampaignPlannerJob(CampaignPlanner planner, ILogger<CampaignPlannerJob> log) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await planner.RunAsync(cancellationToken);
        if (result.Created > 0) log.LogInformation("Chiến dịch: đã tạo {Count} bài", result.Created);
    }
}

/// <summary>Đồng bộ trạng thái yêu cầu sản xuất theo job, rồi đặt sản xuất thêm cho chiến dịch có tồn kho thấp.</summary>
[ScheduledJob("20 0/2 * * * ?")]
[DisallowConcurrentExecution]
public sealed class CampaignProductionJob(ProductionService production, ProductionOrderPlanner orders, ILogger<CampaignProductionJob> log) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await production.SyncAsync(cancellationToken);
        var result = await orders.RunAsync(cancellationToken);
        if (result.Created > 0) log.LogInformation("Chiến dịch: đã đặt {Count} yêu cầu sản xuất", result.Created);
    }
}
