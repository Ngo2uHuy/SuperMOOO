using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.Publishing.Scheduling;

/// <summary>
/// Xếp job upload (lane Publish) cho publication tới mốc điều phối. Bài chỉ có job khi tới giờ: đổi giờ/huỷ bài hẹn xa không phải dọn job.
/// Job gắn vào bài bằng CAS job_id; job thừa tự bỏ qua trong handler nên xếp trùng không gây đăng trùng.
/// </summary>
[Service]
public sealed class PublishDispatcher(
    IPublicationRepository publications,
    IDestinationRepository destinations,
    IConnectionRepository connections,
    IServiceProvider services,
    IClock clock,
    ILogger<PublishDispatcher> log)
{
    // IJobQueue phụ thuộc mọi IJobHandler (pipeline → deliver → PublicationService → service này) nên phải lấy lười để tránh vòng DI.
    private IJobQueue jobs => services.GetRequiredService<IJobQueue>();

    public const int MaxAttempts = 8;
    private const int Batch = 50;

    private static readonly PublicationStatus[] Dispatchable = [PublicationStatus.Scheduled, PublicationStatus.RetryScheduled];
    private static readonly JobStatus[] Ended = [JobStatus.Failed, JobStatus.Cancelled, JobStatus.Succeeded];

    /// <summary>Xếp ngay job upload cho một bài (Đăng ngay / Thử lại). Trả id job, null nếu bài không còn chờ hoặc đã có job.</summary>
    public async Task<string?> DispatchAsync(string publicationId, bool manual, CancellationToken ct)
    {
        var pub = await publications.GetAsync(publicationId, ct);
        return pub is null ? null : await EnqueueAsync(pub, manual, ct);
    }

    /// <summary>Bài hẹn giờ/thử lại đã tới mốc: xếp job (chạy theo cờ tạm dừng tự động).</summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var pub in await publications.ListDispatchableAsync(clock.UtcNow, Batch, ct))
            if (await EnqueueAsync(pub, manual: false, ct) is not null) count++;
        return count;
    }

    /// <summary>
    /// Job upload đã dừng (lỗi không thử lại, hết lượt, người dùng huỷ, mất job) mà bài vẫn chờ: quyết định theo việc video có thể đã lên
    /// hay chưa (Processing / Unknown / Failed), không để bài kẹt với job đã chết.
    /// </summary>
    public async Task<int> RecoverEndedJobsAsync(PublishJobHandler handler, CancellationToken ct)
    {
        var count = 0;
        foreach (var status in new[] { PublicationStatus.Scheduled, PublicationStatus.RetryScheduled, PublicationStatus.Uploading })
        {
            var (rows, _) = await publications.QueryAsync(new PublicationQuery(status, null, null, null, 1, PublishingJson.MaxPageSize), ct);
            foreach (var pub in rows.Select(r => r.Publication).Where(p => p.JobId is not null))
            {
                var job = await jobs.GetAsync(pub.JobId!, ct);
                if (job is not null && !Ended.Contains(job.Status)) continue;
                var message = job?.Status == JobStatus.Cancelled ? "Tác vụ upload đã bị huỷ." : job?.Error ?? "Tác vụ upload đã dừng.";
                await handler.OnJobEndedAsync(pub.Id, pub.JobId, message, ct);
                count++;
            }
        }
        return count;
    }

    private async Task<string?> EnqueueAsync(Publication pub, bool manual, CancellationToken ct)
    {
        if (!Dispatchable.Contains(pub.Status) || pub.JobId is not null) return null;
        var destination = await destinations.GetAsync(pub.DestinationId, ct);
        var connection = destination is null ? null : await connections.GetAsync(destination.ConnectionId, ct);
        if (connection is null) return null;   // handler/khôi phục báo lỗi khi bài chạy; ở đây không xếp job vô ích

        var concurrencyKey = "publish-account:" + connection.Id;
        var payload = JsonSerializer.SerializeToNode(
            new PublishJobPayload(pub.Id, connection.Id, connection.Platform.ToString(), !manual, concurrencyKey), PublishingJson.Options)!;
        var owner = pub.CampaignId is { } campaign ? "campaign:" + campaign : JobOwner.Current ?? JobOwner.Screen("S14");
        var title = pub.Title.Length <= 60 ? pub.Title : pub.Title[..57] + "...";
        var jobId = await jobs.EnqueueAsync(new JobRequest(PublishJobTypes.Upload, $"Đăng {destination!.Platform}: {title}", payload,
            JobLane.Publish, Priority: manual ? 3 : 5, MaxAttempts: MaxAttempts, Owner: owner), ct);
        if (await publications.TryBindJobAsync(pub.Id, jobId, ct)) return jobId;

        // Bài vừa được job khác giữ hoặc đổi trạng thái: bỏ job thừa (nếu nó kịp chạy thì handler cũng tự bỏ qua).
        await jobs.CancelAsync(jobId);
        log.LogDebug("Bỏ job upload thừa {Job} của publication {Id}", jobId, pub.Id);
        return null;
    }
}

/// <summary>Trước khi chạy job upload: cờ tạm dừng đăng tự động, ngắt mạch tài khoản/nền tảng, ổ dữ liệu sắp đầy → dời job (không tính lượt).</summary>
[Service(As = [typeof(IJobPreRunCheck)])]
public sealed class PublishPreRunCheck(IPublishSettingsStore settings, ICircuitBreaker breaker, AppPaths paths, IClock clock) : IJobPreRunCheck
{
    public const long DefaultMinFreeMb = 1024;

    public bool AppliesTo(string jobType) => jobType == PublishJobTypes.Upload;

    public async Task<JobPreRunDecision?> CheckAsync(JobPreRunContext job, CancellationToken ct)
    {
        var payload = PublishJobHandler.Payload(job.Payload);
        if (payload is null) return null;   // handler báo lỗi payload
        var now = clock.UtcNow;

        if (await settings.GetAsync("account.paused:" + payload.ConnectionId, ct) == "true")
            return new JobPreRunDecision(now + TimeSpan.FromMinutes(5), "Tài khoản đang tạm dừng đăng bài.");

        if (payload.Automatic && await settings.GetAsync(SettingKeys.AutomationPaused, ct) == "true")
            return new JobPreRunDecision(now + TimeSpan.FromMinutes(5), "Đăng tự động đang tạm dừng.");

        var circuit = breaker.Peek(payload.Platform, payload.ConnectionId);
        if (!circuit.Allowed)
            return new JobPreRunDecision(circuit.RetryAt ?? now + TimeSpan.FromMinutes(1), circuit.Reason ?? "Tài khoản đang tạm ngắt.");

        var minFreeMb = long.TryParse(await settings.GetAsync(SettingKeys.DiskMinFreeMb, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v : DefaultMinFreeMb;
        if (minFreeMb > 0 && Path.GetPathRoot(paths.DataRoot) is { Length: > 0 } root)
        {
            var free = new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
            if (free < minFreeMb)
                return new JobPreRunDecision(now + TimeSpan.FromMinutes(10), $"Ổ dữ liệu còn {free} MB (tối thiểu {minFreeMb} MB); tạm dừng upload.");
        }
        return null;
    }
}

[ScheduledJob("0/15 * * * * ?")]
[DisallowConcurrentExecution]
public sealed class PublicationDispatchJob(PublishDispatcher dispatcher, PublishJobHandler handler, PlatformConnectorHost platforms,
    ILogger<PublicationDispatchJob> log) : IJob
{
    private static int _loaded;
    private static int _ticks;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        // Connector Instagram/TikTok dựng theo cấu hình người dùng: nạp một lần trước lượt đầu.
        if (Interlocked.Exchange(ref _loaded, 1) == 0) await platforms.ReloadAsync(cancellationToken);
        var dispatched = await dispatcher.DispatchDueAsync(cancellationToken);
        if (dispatched > 0) log.LogInformation("Đã xếp {Count} bài tới giờ đăng", dispatched);
        // Quét job chết thưa hơn (mỗi phút) vì phải hỏi trạng thái từng job.
        if (Interlocked.Increment(ref _ticks) % 4 == 1) await dispatcher.RecoverEndedJobsAsync(handler, cancellationToken);
    }
}

/// <summary>Kiểm tra trạng thái bài đã upload theo next_check_at (nhịp giãn dần), không chiếm lane Publish.</summary>
[ScheduledJob("5/15 * * * * ?")]
[DisallowConcurrentExecution]
public sealed class PublicationStatusJob(IPublicationRepository publications, PublishJobHandler handler, IClock clock) : IJob
{
    private const int Batch = 20;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        foreach (var pub in await publications.ListDueStatusChecksAsync(clock.UtcNow, Batch, cancellationToken))
            await handler.CheckStatusAsync(pub, cancellationToken);
    }
}

/// <summary>Theo dõi hạn token/thu hồi quyền của các tài khoản (Meta Page token không tự làm mới, TikTok refresh 365 ngày).</summary>
[ScheduledJob("0 17 * * * ?")]
[DisallowConcurrentExecution]
public sealed class TokenHealthJob(TokenHealthMonitor monitor) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => await monitor.CheckAllAsync(cancellationToken);
}
