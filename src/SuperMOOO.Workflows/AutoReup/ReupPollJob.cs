using Quartz;

namespace SuperMOOO.Workflows.AutoReup;

/// <summary>Nhịp của Auto Reup: theo dõi job xử lý, xếp item chờ và quét luồng đến hạn.</summary>
[ScheduledJob("0/20 * * * * ?")]
[DisallowConcurrentExecution]
public sealed class ReupPollJob(AutoReupEngine engine) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => await engine.TickAsync(cancellationToken);
}
