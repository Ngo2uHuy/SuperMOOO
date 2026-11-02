using Microsoft.Extensions.Logging;
using Quartz;

namespace SuperMOOO.Publishing.Analytics;

/// <summary>Xếp job đồng bộ thống kê (lane Network, ưu tiên thấp), gộp snapshot cũ và quét sức khỏe kênh. Không gọi mạng trực tiếp.</summary>
[ScheduledJob("0 3/10 * * * ?")]
[DisallowConcurrentExecution]
public sealed class AnalyticsPlanJob(AnalyticsPlanner planner, AccountHealthService health, ILogger<AnalyticsPlanJob> log) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var plan = await planner.RunAsync(cancellationToken);
        if (plan.JobsQueued > 0) log.LogInformation("Đã xếp {Jobs} job thống kê ({Posts} bài)", plan.JobsQueued, plan.PostsQueued);
        var events = await health.EvaluateAsync(cancellationToken);
        if (events > 0) log.LogInformation("Phát hiện {Count} dấu hiệu bất thường của kênh", events);
    }
}
