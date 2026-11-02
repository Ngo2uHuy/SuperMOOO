namespace SuperMOOO.Core.Jobs;

public enum JobLane { Media, Network, AiWorker, Cloud, Publish }       // song song mặc định: 1, 3, 1, 2, 1 (cấu hình được)
public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled, WaitingManualRetry }

public sealed record JobRequest(string Type, string Title, JsonNode Payload, JobLane Lane, int Priority = 5,
    int MaxAttempts = 1, DateTimeOffset? NotBefore = null, string? ParentId = null, string? Owner = null);
public sealed record JobStepSnapshot(int Index, string StepId, string Kind, JobStatus Status, string? Error);
public sealed record JobSnapshot(string Id, string Type, string Title, JobStatus Status, double Progress, string? Message, string? Error,
    int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, IReadOnlyList<JobStepSnapshot> Steps, JsonNode? Result, string? Owner = null);
public sealed record JobQuery(string? TypePrefix = null, JobStatus[]? Statuses = null, string? Owner = null, int Limit = 200, IReadOnlyCollection<string>? Ids = null);

public interface IJobQueue
{
    Task<string> EnqueueAsync(JobRequest request, CancellationToken ct = default);
    Task CancelAsync(string jobId);
    Task<string> RetryAsync(string jobId, bool resumeFromFailedStep = true);
    Task<JobSnapshot?> GetAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<JobSnapshot>> ListAsync(JobQuery query, CancellationToken ct = default);
    IAsyncEnumerable<JobSnapshot> WatchAsync(string? jobId, CancellationToken ct);
}

/// <summary>Đổi số job chạy song song của một lane khi app đang chạy (vd. số upload đồng thời ở S16); 0 = về mặc định lúc khởi động.</summary>
public interface IJobLaneLimits
{
    void SetParallel(JobLane lane, int parallel);
}

public sealed record JobContext(string JobId, JsonNode Payload, string WorkFolder, IProgress<ProgressInfo> Progress);

public interface IJobHandler
{
    string Type { get; }                        // JobTypes.Pipeline | Download | Crawl | Publish | ReupPoll | WatchCheck | …
    Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct);
}
