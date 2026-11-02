using Quartz;

namespace SuperMOOO.Infrastructure.Operations;

[Service(As = [typeof(IJobHandler)])]
public sealed class BackupCreateJobHandler(BackupService backups) : IJobHandler
{
    public string Type => BackupJobTypes.Create;

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        var includeMedia = context.Payload["includeMedia"]?.GetValue<bool>() ?? false;
        var kind = context.Payload["kind"]?.GetValue<string>() ?? "manual";
        var info = await backups.CreateAsync(includeMedia, context.Payload["targetPath"]?.GetValue<string>(), kind, context.Progress, ct);
        return new JsonObject { ["path"] = info.Path, ["sizeBytes"] = info.SizeBytes };
    }
}

[Service(As = [typeof(IJobHandler)])]
public sealed class BackupStageJobHandler(BackupService backups) : IJobHandler
{
    public string Type => BackupJobTypes.Stage;

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        var path = context.Payload["path"]?.GetValue<string>() ?? throw new ArgumentException("Payload thiếu path.");
        var info = await backups.StageRestoreAsync(path, context.Progress, ct);
        return new JsonObject { ["path"] = info.Path, ["warnings"] = new JsonArray(info.Warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()) };
    }
}

/// <summary>Mỗi giờ: bật "sao lưu tự động" và đã quá chu kỳ kể từ bản auto gần nhất thì xếp job sao lưu (giữ N bản mới nhất).</summary>
[ScheduledJob("0 20 * * * ?")]
[DisallowConcurrentExecution]
public sealed class AutoBackupJob(BackupService backups, ISettingsStore settings, IJobQueue queue) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var cfg = settings.Get<StorageSettings>("storage");
        if (!cfg.AutoBackup) return;
        if (backups.LastAutoBackupUtc() is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(Math.Max(1, cfg.AutoBackupEveryHours))) return;
        if ((await queue.ListAsync(new SuperMOOO.Core.Jobs.JobQuery(TypePrefix: "backup.", Statuses: [JobStatus.Queued, JobStatus.Running], Limit: 1), cancellationToken)).Count > 0) return;

        await queue.EnqueueAsync(new JobRequest(BackupJobTypes.Create, "Sao lưu tự động",
            new JsonObject { ["includeMedia"] = cfg.AutoBackupIncludeMedia, ["kind"] = "auto" }, JobLane.Cloud,
            Owner: JobOwner.Screen("S16")), cancellationToken);
    }
}
