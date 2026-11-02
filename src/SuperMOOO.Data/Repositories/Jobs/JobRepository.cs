using System.Text.Json;
using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Jobs;

public sealed record ClaimedJob(string Id, string Type, string Title, string PayloadJson, int Attempts, int MaxAttempts);

public sealed record JobStepRecord(int Idx, string StepId, string Kind, JobStatus Status, bool CloudBilled,
    int Attempts, string? StateJson, string? Error);

[Service]
public sealed class JobRepository(IDbConnectionFactory factory)
{
    private sealed class JobRow
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public double Progress { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
        public int Attempts { get; set; }
        public long CreatedAt { get; set; }
        public long? FinishedAt { get; set; }
        public string? ResultJson { get; set; }
        public string? Owner { get; set; }
    }

    private sealed class StepRow
    {
        public string JobId { get; set; } = "";
        public int Idx { get; set; }
        public string StepId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Error { get; set; }
    }

    private sealed class ClaimRow
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Title { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        public int Attempts { get; set; }
        public int MaxAttempts { get; set; }
    }

    private sealed class StepFullRow
    {
        public int Idx { get; set; }
        public string StepId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Status { get; set; } = "";
        public int CloudBilled { get; set; }
        public int Attempts { get; set; }
        public string? StateJson { get; set; }
        public string? Error { get; set; }
    }

    private const string JobColumns =
        "id, type, title, status, progress, message, error, attempts, created_at, finished_at, result_json, owner";

    public async Task<string> InsertAsync(JobRequest request, string? pipelineId = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DapperSetup.NowMs;
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO job(id, type, title, lane, status, priority, payload_json, max_attempts, parent_id, pipeline_id,
                            owner, not_before, created_at, updated_at)
            VALUES (@id, @Type, @Title, @lane, @status, @Priority, @payload, @MaxAttempts, @ParentId, @pipelineId,
                    @Owner, @notBefore, @now, @now)
            """,
            new
            {
                id, request.Type, request.Title, lane = request.Lane.ToString(), status = nameof(JobStatus.Queued),
                request.Priority, payload = request.Payload.ToJsonString(), request.MaxAttempts, request.ParentId,
                pipelineId, request.Owner, notBefore = request.NotBefore?.ToUnixTimeMilliseconds(), now
            }, cancellationToken: ct));
        return id;
    }

    public async Task<JobSnapshot?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            $"SELECT {JobColumns} FROM job WHERE id = @id", new { id }, cancellationToken: ct));
        if (row is null) return null;
        var steps = await LoadStepsAsync(db, [id], ct);
        return ToSnapshot(row, steps);
    }

    public async Task<IReadOnlyList<JobSnapshot>> ListAsync(JobQuery query, CancellationToken ct = default)
    {
        var statuses = query.Statuses?.Select(s => s.ToString()).ToArray();
        // Chỉ ghép điều kiện có dùng: dạng "@x IS NULL OR ..." làm SQLite bỏ qua index và quét cả bảng + sắp xếp tạm.
        var filters = new List<string>();
        if (query.TypePrefix is not null) filters.Add("substr(type, 1, length(@prefix)) = @prefix");
        if (query.Owner is not null) filters.Add("owner = @owner");
        if (statuses is not null) filters.Add("status IN @statuses");
        if (query.Ids is not null) filters.Add("id IN @ids");
        var where = filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters);

        await using var db = factory.Open();
        var rows = (await db.QueryAsync<JobRow>(new CommandDefinition($"""
            SELECT {JobColumns} FROM job {where}
            ORDER BY created_at DESC LIMIT @limit
            """,
            new { prefix = query.TypePrefix, owner = query.Owner, statuses, ids = query.Ids?.ToArray(), limit = query.Limit },
            cancellationToken: ct))).ToList();

        var steps = await LoadStepsAsync(db, rows.Select(r => r.Id).ToArray(), ct);
        return rows.Select(r => ToSnapshot(r, steps)).ToList();
    }

    public async Task SetStatusAsync(string id, JobStatus status, string? error = null, JsonNode? result = null,
        bool countAttempt = false, CancellationToken ct = default)
    {
        var terminal = status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
        var now = DapperSetup.NowMs;
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE job SET status = @status, error = @error, result_json = COALESCE(@result, result_json),
              attempts = attempts + @attempt,
              started_at = CASE WHEN @status = 'Running' THEN @now ELSE started_at END,
              finished_at = CASE WHEN @terminal = 1 THEN @now ELSE NULL END,
              updated_at = @now
            WHERE id = @id
            """,
            new { id, status = status.ToString(), error, result = result?.ToJsonString(), attempt = countAttempt ? 1 : 0, terminal = terminal ? 1 : 0, now },
            cancellationToken: ct));
    }

    public async Task SetProgressAsync(string id, double fraction, string? message, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET progress = CASE WHEN @fraction < 0 THEN progress ELSE @fraction END, message = @message, updated_at = @now WHERE id = @id",
            new { id, fraction, message, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task UpsertStepAsync(string jobId, JobStepRecord step, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO job_step(job_id, idx, step_id, kind, status, cloud_billed, attempts, state_json, error, started_at, finished_at)
            VALUES (@jobId, @Idx, @StepId, @Kind, @status, @cloud, @Attempts, @StateJson, @Error, @now, NULL)
            ON CONFLICT(job_id, idx) DO UPDATE SET
              status = excluded.status, attempts = excluded.attempts, state_json = excluded.state_json, error = excluded.error,
              finished_at = CASE WHEN excluded.status IN ('Succeeded','Failed','Cancelled') THEN @now ELSE NULL END
            """,
            new { jobId, step.Idx, step.StepId, step.Kind, status = step.Status.ToString(), cloud = step.CloudBilled ? 1 : 0,
                  step.Attempts, step.StateJson, step.Error, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    /// <summary>Nhận job Queued kế tiếp của lane (số priority nhỏ chạy trước) và chuyển Running trong một câu lệnh nên không thể bị 2 worker cùng nhận.</summary>
    public async Task<ClaimedJob?> ClaimNextAsync(JobLane lane, CancellationToken ct = default)
    {
        var now = DapperSetup.NowMs;
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<ClaimRow>(new CommandDefinition("""
            UPDATE job SET status = 'Running', attempts = attempts + 1, error = NULL, finished_at = NULL, started_at = @now, updated_at = @now
            WHERE id = (SELECT id FROM job
                        WHERE status = 'Queued' AND lane = @lane AND (not_before IS NULL OR not_before <= @now)
                          AND NOT EXISTS (SELECT 1 FROM job r WHERE r.status = 'Running'
                                          AND json_extract(r.payload_json, '$.concurrencyKey') = json_extract(job.payload_json, '$.concurrencyKey'))
                        ORDER BY priority, created_at LIMIT 1)
            RETURNING id, type, title, payload_json, attempts, max_attempts
            """, new { lane = lane.ToString(), now }, cancellationToken: ct));
        return row is null ? null : new ClaimedJob(row.Id, row.Type, row.Title, row.PayloadJson, row.Attempts, row.MaxAttempts);
    }

    /// <summary>Đưa job đang Running về Queued, không chạy trước notBefore; refundAttempt trừ lại lượt thử mà ClaimNextAsync đã cộng (dời lịch không phải lỗi).</summary>
    public async Task RequeueAsync(string id, DateTimeOffset notBefore, bool refundAttempt, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE job SET status = 'Queued', error = NULL, finished_at = NULL, not_before = @notBefore,
              attempts = MAX(0, attempts - @refund), updated_at = @now
            WHERE id = @id
            """, new { id, notBefore = notBefore.ToUnixTimeMilliseconds(), refund = refundAttempt ? 1 : 0, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    /// <summary>Job còn Running sau khi app tắt đột ngột: về Queued, riêng job đang ở bước cloud tính phí thì chờ người dùng quyết định.</summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE job SET updated_at = @now,
              status = CASE WHEN EXISTS (SELECT 1 FROM job_step s WHERE s.job_id = job.id AND s.cloud_billed = 1 AND s.status = 'Running')
                            THEN 'WaitingManualRetry' ELSE 'Queued' END
            WHERE status = 'Running'
            """, new { now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<JobStepRecord>> GetStepsAsync(string jobId, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<StepFullRow>(new CommandDefinition(
            "SELECT idx, step_id, kind, status, cloud_billed, attempts, state_json, error FROM job_step WHERE job_id = @jobId ORDER BY idx",
            new { jobId }, cancellationToken: ct));
        return rows.Select(r => new JobStepRecord(r.Idx, r.StepId, r.Kind, Enum.Parse<JobStatus>(r.Status), r.CloudBilled != 0,
            r.Attempts, r.StateJson, r.Error)).ToList();
    }

    /// <summary>Huỷ job chưa chạy (Queued/WaitingManualRetry); false nếu job đang chạy hoặc đã kết thúc.</summary>
    public async Task<bool> TryCancelIdleAsync(string id, CancellationToken ct = default)
    {
        var now = DapperSetup.NowMs;
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("""
            UPDATE job SET status = 'Cancelled', finished_at = @now, updated_at = @now
            WHERE id = @id AND status IN ('Queued', 'WaitingManualRetry')
            """, new { id, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>Đưa job Failed/Cancelled/WaitingManualRetry về Queued, cho thêm ít nhất một lần chạy; keepSteps=false xoá checkpoint để chạy lại từ đầu.</summary>
    public async Task<bool> ResetForRetryAsync(string id, bool keepSteps, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        var changed = await db.ExecuteAsync(new CommandDefinition("""
            UPDATE job SET status = 'Queued', error = NULL, finished_at = NULL, max_attempts = MAX(max_attempts, attempts + 1),
              progress = CASE WHEN @keepSteps = 1 THEN progress ELSE 0 END, updated_at = @now
            WHERE id = @id AND status IN ('Failed', 'Cancelled', 'WaitingManualRetry')
            """, new { id, keepSteps = keepSteps ? 1 : 0, now = DapperSetup.NowMs }, tx, cancellationToken: ct));
        if (changed > 0 && !keepSteps)
            await db.ExecuteAsync(new CommandDefinition("DELETE FROM job_step WHERE job_id = @id", new { id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return changed > 0;
    }

    public async Task AddEventAsync(string jobId, string level, string message, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO job_event(job_id, at, level, message) VALUES (@jobId, @now, @level, @message)",
            new { jobId, level, message, now = DapperSetup.NowMs }, cancellationToken: ct));
    }

    private const string FinishedBefore =
        "status IN ('Succeeded', 'Failed', 'Cancelled') AND created_at < @cutoff AND COALESCE(finished_at, updated_at) < @cutoff";

    public async Task<bool> AnyFinishedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS (SELECT 1 FROM job WHERE {FinishedBefore})", new { cutoff = cutoff.ToUnixTimeMilliseconds() }, cancellationToken: ct));
    }

    /// <summary>Xoá lịch sử job đã kết thúc trước cutoff (job_step/job_event theo CASCADE, media_file/crawl_run chỉ bỏ liên kết); xoá theo lô để không giữ khoá ghi lâu.</summary>
    public async Task<int> DeleteFinishedBeforeAsync(DateTimeOffset cutoff, int batchSize = 500, CancellationToken ct = default)
    {
        // parent_id ON DELETE CASCADE kéo theo job con mọi cấp, nên bỏ qua job có con chưa kết thúc hoặc kết thúc sau cutoff.
        var sql = $"""
            DELETE FROM job WHERE id IN (
              SELECT p.id FROM job p WHERE {FinishedBefore}
                AND NOT EXISTS (
                  WITH RECURSIVE sub(id) AS (
                    SELECT c.id FROM job c WHERE c.parent_id = p.id
                    UNION
                    SELECT c.id FROM job c JOIN sub ON c.parent_id = sub.id)
                  SELECT 1 FROM sub JOIN job j ON j.id = sub.id
                  WHERE j.status NOT IN ('Succeeded', 'Failed', 'Cancelled') OR COALESCE(j.finished_at, j.updated_at) >= @cutoff)
              LIMIT @batchSize)
            """;
        var args = new { cutoff = cutoff.ToUnixTimeMilliseconds(), batchSize };
        var total = 0;
        await using var db = factory.Open();
        while (true)
        {
            var deleted = await db.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
            // Không dừng khi lô thiếu: job con nằm cùng lô với cha bị CASCADE xoá trước nên không được đếm.
            if (deleted == 0) return total;
            total += deleted;
        }
    }

    private static async Task<ILookup<string, JobStepSnapshot>> LoadStepsAsync(DbConnection db, string[] jobIds, CancellationToken ct)
    {
        if (jobIds.Length == 0) return Array.Empty<JobStepSnapshot>().ToLookup(_ => "");
        var rows = await db.QueryAsync<StepRow>(new CommandDefinition(
            "SELECT job_id, idx, step_id, kind, status, error FROM job_step WHERE job_id IN @jobIds ORDER BY job_id, idx",
            new { jobIds }, cancellationToken: ct));
        return rows.ToLookup(r => r.JobId,
            r => new JobStepSnapshot(r.Idx, r.StepId, r.Kind, Enum.Parse<JobStatus>(r.Status), r.Error));
    }

    private static JobSnapshot ToSnapshot(JobRow r, ILookup<string, JobStepSnapshot> steps) =>
        new(r.Id, r.Type, r.Title, Enum.Parse<JobStatus>(r.Status), r.Progress, r.Message, r.Error, r.Attempts,
            DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAt),
            r.FinishedAt is { } f ? DateTimeOffset.FromUnixTimeMilliseconds(f) : null,
            steps[r.Id].ToList(), r.ResultJson is null ? null : JsonNode.Parse(r.ResultJson), r.Owner);
}
