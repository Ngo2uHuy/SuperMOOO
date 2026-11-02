using SuperMOOO.Core.Publishing;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SuperMOOO.Data.Repositories.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Infrastructure.Jobs;

/// <summary>Hàng đợi job bền vững (SQLite): mỗi lane có N worker; huỷ = cancel token để ProcessRunner kill cả cây tiến trình.</summary>
[Service(As = [typeof(IJobQueue), typeof(IJobLaneLimits)])]
public sealed class JobQueue : IJobQueue, IJobLaneLimits, IHostedService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly JobRepository _repo;
    private readonly Dictionary<string, IJobHandler> _handlers;
    private readonly ISettingsStore _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<JobQueue> _log;
    private readonly IRuntimeResources _resources;
    private readonly IReadOnlyList<IJobPreRunCheck> _preRunChecks;
    private readonly Dictionary<JobLane, Channel<byte>> _wake = Enum.GetValues<JobLane>()
        .ToDictionary(l => l, _ => Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite }));
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, byte> _cancelRequested = new();
    private readonly ConcurrentDictionary<Guid, JobWatcher> _watchers = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private readonly object _lanesGate = new();
    private readonly Dictionary<JobLane, int> _laneDefaults = [];
    private readonly Dictionary<JobLane, int> _laneOverrides = [];
    private readonly Dictionary<JobLane, int> _laneSpawned = [];
    private readonly ConcurrentDictionary<JobLane, int> _laneParallel = new();

    public JobQueue(JobRepository repo, IEnumerable<IJobHandler> handlers, ISettingsStore settings, AppPaths paths, ILogger<JobQueue> log, IRuntimeResources resources,
        IEnumerable<IJobPreRunCheck> preRunChecks)
    {
        _preRunChecks = preRunChecks.ToList();
        _repo = repo;
        _settings = settings;
        _paths = paths;
        _log = log;
        _resources = resources;
        _handlers = handlers.GroupBy(h => h.Type).ToDictionary(g => g.Key, g => g.Count() == 1
            ? g.Single()
            : throw new InvalidOperationException($"Trùng IJobHandler cho loại tác vụ '{g.Key}'."));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var recovered = await _repo.RecoverInterruptedAsync(cancellationToken);
        if (recovered > 0) _log.LogInformation("Khôi phục {Count} tác vụ đang chạy dở khi khởi động", recovered);

        var limits = _settings.Get<JobsSettings>("jobs");
        var plan = _resources.Plan;
        if (plan.Automatic) limits = new JobsSettings { Media = plan.MediaParallel, Network = plan.NetworkParallel, AiWorker = 1, Cloud = 2, Publish = 2 };
        else limits.Media = plan.MediaParallel;
        foreach (var lane in Enum.GetValues<JobLane>())
        {
            var parallel = Math.Clamp(lane switch
            {
                JobLane.Media => limits.Media,
                JobLane.Network => limits.Network,
                JobLane.AiWorker => limits.AiWorker,
                JobLane.Cloud => limits.Cloud,
                _ => limits.Publish
            }, 1, 16);
            lock (_lanesGate)
            {
                _laneDefaults[lane] = parallel;
                ApplyParallel(lane);
            }
        }
    }

    public void SetParallel(JobLane lane, int parallel)
    {
        lock (_lanesGate)
        {
            if (parallel > 0) _laneOverrides[lane] = Math.Clamp(parallel, 1, 16);
            else _laneOverrides.Remove(lane);
            if (_laneDefaults.ContainsKey(lane)) ApplyParallel(lane);
        }
        _wake[lane].Writer.TryWrite(0);
    }

    // Gọi trong _lanesGate. Giảm giới hạn không dừng job đang chạy: vòng lặp vượt giới hạn chạy xong job hiện tại rồi chờ.
    private void ApplyParallel(JobLane lane)
    {
        var target = _laneOverrides.TryGetValue(lane, out var custom) ? custom : _laneDefaults[lane];
        _laneParallel[lane] = target;
        var spawned = _laneSpawned.GetValueOrDefault(lane);
        for (var slot = spawned; slot < target; slot++)
        {
            var s = slot;
            _loops.Add(Task.Run(() => LaneLoopAsync(lane, s, _stop.Token)));
        }
        _laneSpawned[lane] = Math.Max(spawned, target);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync();
        Task[] loops;
        lock (_lanesGate) loops = [.. _loops];
        await Task.WhenAll(loops).WaitAsync(cancellationToken);
    }

    public async Task<string> EnqueueAsync(JobRequest request, CancellationToken ct = default)
    {
        if (!_handlers.ContainsKey(request.Type))
            throw new ArgumentException($"Chưa có handler cho loại tác vụ '{request.Type}'.", nameof(request));

        if (request.Owner is null && JobOwner.Current is { } owner) request = request with { Owner = owner };
        var pipelineId = request.Type == JobTypes.Pipeline ? (request.Payload as JsonObject)?["pipeline"]?.GetValue<string>() : null;
        var id = await _repo.InsertAsync(request, pipelineId, ct);
        await _repo.AddEventAsync(id, "info", "Đã xếp hàng", ct);
        _wake[request.Lane].Writer.TryWrite(0);
        Notify(id);
        return id;
    }

    public async Task CancelAsync(string jobId)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync();
            return;
        }

        if (await _repo.TryCancelIdleAsync(jobId))
        {
            Notify(jobId);
            return;
        }

        // Job vừa được worker nhận nhưng chưa kịp đăng ký token: RunAsync sẽ kiểm tra cờ này.
        if ((await _repo.GetAsync(jobId))?.Status == JobStatus.Running) _cancelRequested[jobId] = 0;
    }

    public async Task<string> RetryAsync(string jobId, bool resumeFromFailedStep = true)
    {
        if (!await _repo.ResetForRetryAsync(jobId, resumeFromFailedStep))
            throw new InvalidOperationException("Chỉ thử lại được tác vụ đã lỗi, đã huỷ hoặc đang chờ thử lại.");

        await _repo.AddEventAsync(jobId, "info", resumeFromFailedStep ? "Thử lại từ bước lỗi" : "Thử lại từ đầu");
        foreach (var wake in _wake.Values) wake.Writer.TryWrite(0);
        Notify(jobId);
        return jobId;
    }

    public Task<JobSnapshot?> GetAsync(string jobId, CancellationToken ct = default) => _repo.GetAsync(jobId, ct);

    public Task<IReadOnlyList<JobSnapshot>> ListAsync(JobQuery query, CancellationToken ct = default) => _repo.ListAsync(query, ct);

    public async IAsyncEnumerable<JobSnapshot> WatchAsync(string? jobId, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = Guid.NewGuid();
        var watcher = new JobWatcher(jobId);
        _watchers[key] = watcher;
        try
        {
            await foreach (var changed in watcher.Changes.Reader.ReadAllAsync(ct))
            {
                // Bỏ dấu trước khi đọc DB để cập nhật tới trong lúc đọc vẫn được xếp lượt tiếp.
                watcher.Pending.TryRemove(changed, out _);
                if (await _repo.GetAsync(changed, ct) is { } snapshot) yield return snapshot;
            }
        }
        finally
        {
            _watchers.TryRemove(key, out _);
        }
    }

    private async Task LaneLoopAsync(JobLane lane, int slot, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (slot >= _laneParallel.GetValueOrDefault(lane))
                {
                    await Task.Delay(PollInterval, stop);
                    continue;
                }
                var job = await _repo.ClaimNextAsync(lane, stop);
                if (job is null) await WaitForWorkAsync(lane, stop);
                else await RunAsync(job, lane, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Lỗi vòng lặp hàng đợi lane {Lane}", lane);
                try { await Task.Delay(TimeSpan.FromSeconds(1), stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task WaitForWorkAsync(JobLane lane, CancellationToken stop)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        wait.CancelAfter(PollInterval);
        try
        {
            await _wake[lane].Reader.WaitToReadAsync(wait.Token);
            _wake[lane].Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // hết chu kỳ chờ: quay lại hỏi DB vì job có NotBefore tới hạn không phát tín hiệu
        }
    }

    private async Task RunAsync(ClaimedJob job, JobLane lane, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _running[job.Id] = cts;
        if (_cancelRequested.TryRemove(job.Id, out _)) await cts.CancelAsync();
        var progress = new JobProgress(_repo, job.Id, () => Notify(job.Id), _log);
        Notify(job.Id);

        try
        {
            if (!_handlers.TryGetValue(job.Type, out var handler))
                throw new InvalidOperationException($"Chưa có handler cho loại tác vụ '{job.Type}'.");

            var work = Directory.CreateDirectory(Path.Combine(_paths.CacheDir("jobs"), job.Id)).FullName;
            var payload = JsonNode.Parse(job.PayloadJson)!;
            var preRun = new JobPreRunContext(job.Id, job.Type, payload, job.Attempts, lane);
            foreach (var check in _preRunChecks.Where(c => c.AppliesTo(job.Type)))
                if (await check.CheckAsync(preRun, cts.Token) is { } decision)
                    throw new JobDeferredException(decision.DeferUntil, decision.Reason);

            var context = new JobContext(job.Id, payload, work, progress);
            var result = await handler.RunAsync(context, cts.Token);

            await progress.DrainAsync();
            await _repo.SetProgressAsync(job.Id, 1, null);
            await _repo.SetStatusAsync(job.Id, JobStatus.Succeeded, result: result);
        }
        catch (Exception ex)
        {
            await progress.DrainAsync();
            // Tắt app: để nguyên Running, lần khởi động sau RecoverInterruptedAsync sẽ xếp lại.
            if (stop.IsCancellationRequested) return;

            if (cts.IsCancellationRequested)
                await FinishAsync(job.Id, JobStatus.Cancelled, "Đã huỷ");
            else if (ex is JobDeferredException deferred)
            {
                await _repo.RequeueAsync(job.Id, deferred.Due, refundAttempt: true);
                await _repo.AddEventAsync(job.Id, "info", $"Dời tới {deferred.Due.ToLocalTime():dd/MM HH:mm:ss}: {deferred.Message}");
                _log.LogInformation("Tác vụ {Job} ({Type}) dời tới {Due}: {Reason}", job.Id, job.Type, deferred.Due, deferred.Message);
                return;
            }
            else if (ex is JobWaitingManualRetryException)
                await FinishAsync(job.Id, JobStatus.WaitingManualRetry, ex.Message);
            else if (ex is TransientJobException transient && job.Attempts < job.MaxAttempts)
            {
                var due = DateTimeOffset.UtcNow + RetryPolicy.Delay(job.Attempts, Random.Shared.NextDouble());
                if (transient.RetryAt is { } at && at > due) due = at;
                await _repo.RequeueAsync(job.Id, due, refundAttempt: false);
                await _repo.AddEventAsync(job.Id, "warn", $"Lần {job.Attempts} lỗi tạm thời, thử lại lúc {due.ToLocalTime():HH:mm:ss}: {ex.Message}");
            }
            else if (job.Attempts < job.MaxAttempts)
                await FinishAsync(job.Id, JobStatus.Queued, $"Lần {job.Attempts} lỗi, sẽ thử lại: {ex.Message}");
            else
                await FinishAsync(job.Id, JobStatus.Failed, ex.Message);

            _log.LogWarning(ex, "Tác vụ {Job} ({Type}) không thành công", job.Id, job.Type);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
            Notify(job.Id);
        }
    }

    private async Task FinishAsync(string id, JobStatus status, string message)
    {
        await _repo.SetStatusAsync(id, status, status == JobStatus.Cancelled ? null : message);
        await _repo.AddEventAsync(id, status == JobStatus.Failed ? "error" : "warn", message);
    }

    private void Notify(string jobId)
    {
        foreach (var watcher in _watchers.Values) watcher.Notify(jobId);
    }

    private sealed class JobWatcher(string? jobId)
    {
        public Channel<string> Changes { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        public ConcurrentDictionary<string, byte> Pending { get; } = new();

        public void Notify(string changed)
        {
            if (jobId is not null && changed != jobId) return;
            if (Pending.TryAdd(changed, 0)) Changes.Writer.TryWrite(changed);
        }
    }

    private sealed class JobProgress(JobRepository repo, string jobId, Action notify, ILogger log) : IProgress<ProgressInfo>
    {
        private static readonly long ThrottleTicks = TimeSpan.FromMilliseconds(250).Ticks;

        private readonly object _gate = new();
        private Task _tail = Task.CompletedTask;
        private long _last;

        public void Report(ProgressInfo value)
        {
            var now = DateTime.UtcNow.Ticks;
            lock (_gate)
            {
                if (now - _last < ThrottleTicks) return;
                _last = now;
                _tail = _tail.ContinueWith(_ => WriteAsync(value), TaskScheduler.Default).Unwrap();
            }
        }

        public Task DrainAsync()
        {
            lock (_gate) return _tail;
        }

        private async Task WriteAsync(ProgressInfo value)
        {
            try
            {
                await repo.SetProgressAsync(jobId, value.Fraction, value.Message);
                notify();
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Không ghi được tiến độ tác vụ {Job}", jobId);
            }
        }
    }
}
