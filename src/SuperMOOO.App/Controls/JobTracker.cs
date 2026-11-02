using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Controls;

/// <summary>Chờ một job kết thúc và báo tiến độ cho màn đã tự xếp job (poll thay vì WatchAsync để không lỡ job xong trước khi đăng ký).</summary>
[Service]
public sealed class JobTracker(IJobQueue jobs)
{
    public static bool IsFinished(JobStatus status) =>
        status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled or JobStatus.WaitingManualRetry;

    public async Task<JobSnapshot> WaitAsync(string jobId, Action<JobSnapshot>? onUpdate, CancellationToken ct, TimeSpan? interval = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await jobs.GetAsync(jobId, ct) is { } snapshot)
            {
                onUpdate?.Invoke(snapshot);
                if (IsFinished(snapshot.Status)) return snapshot;
            }

            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(400), ct);
        }
    }

    public Task CancelAsync(string jobId) => jobs.CancelAsync(jobId);
}
