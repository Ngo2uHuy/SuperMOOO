using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S15;

[Service(ServiceLifetime.Transient)]
public sealed partial class JobsViewModel : ObservableObject
{
    public const string AllStatuses = "Tất cả";

    private readonly IJobQueue _queue;
    private readonly IDialogService _dialogs;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource? _watch;

    public JobsViewModel(IJobQueue queue, IDialogService dialogs)
    {
        _queue = queue;
        _dialogs = dialogs;
    }

    public IReadOnlyList<string> StatusFilters { get; } = [AllStatuses, .. Enum.GetNames<JobStatus>()];
    public ObservableCollection<JobRow> Jobs { get; } = [];

    [ObservableProperty]
    public partial string StatusFilter { get; set; } = AllStatuses;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(RetryCommand), nameof(RetryFromStartCommand))]
    public partial JobRow? Selected { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    partial void OnStatusFilterChanged(string value) => _ = LoadAsync();

    partial void OnSelectedChanged(JobRow? value) => Detail = value is null ? "" : Describe(value.Snapshot);

    public async Task LoadAsync()
    {
        var statuses = StatusFilter == AllStatuses ? null : new[] { Enum.Parse<JobStatus>(StatusFilter) };
        var jobs = await _queue.ListAsync(new JobQuery(Statuses: statuses));
        Jobs.Clear();
        foreach (var job in jobs) Jobs.Add(new JobRow(job));
    }

    /// <summary>Bắt đầu theo dõi thay đổi trạng thái; gọi lại khi màn hiện lại.</summary>
    public void StartWatching()
    {
        StopWatching();
        _watch = new CancellationTokenSource();
        _ = WatchAsync(_watch.Token);
    }

    public void StopWatching()
    {
        _watch?.Cancel();
        _watch = null;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (Selected is { } row) await Guard(() => _queue.CancelAsync(row.Id));
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (Selected is { } row) await Guard(() => _queue.RetryAsync(row.Id, resumeFromFailedStep: true));
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryFromStartAsync()
    {
        if (Selected is { } row) await Guard(() => _queue.RetryAsync(row.Id, resumeFromFailedStep: false));
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private bool CanCancel() => Selected?.Status is JobStatus.Queued or JobStatus.Running or JobStatus.WaitingManualRetry;

    private bool CanRetry() => Selected?.Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.WaitingManualRetry;

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            _dialogs.Error(ex.Message);
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var snapshot in _queue.WatchAsync(null, ct))
                await PostAsync(() => Apply(snapshot), ct);
        }
        catch (OperationCanceledException)
        {
            // dừng theo dõi khi rời màn
        }
    }

    public void Apply(JobSnapshot snapshot)
    {
        var row = Jobs.FirstOrDefault(j => j.Id == snapshot.Id);
        if (row is not null) row.Snapshot = snapshot;
        else if (StatusFilter == AllStatuses || StatusFilter == snapshot.Status.ToString()) Jobs.Insert(0, new JobRow(snapshot));

        if (Selected?.Id == snapshot.Id)
        {
            Detail = Describe(snapshot);
            CancelCommand.NotifyCanExecuteChanged();
            RetryCommand.NotifyCanExecuteChanged();
            RetryFromStartCommand.NotifyCanExecuteChanged();
        }
    }

    private Task PostAsync(Action action, CancellationToken ct)
    {
        if (_ui is null) { ct.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; }
            try { action(); done.TrySetResult(); }
            catch (Exception e) { done.TrySetException(e); }
        }, null);
        return done.Task.WaitAsync(ct);
    }

    private static readonly System.Text.Json.JsonSerializerOptions ResultJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Describe(JobSnapshot job)
    {
        var lines = new List<string> { $"{job.Title} ({job.Type}) · {job.Status} · lần chạy {job.Attempts}" };
        if (job.Error is not null) lines.Add($"Lỗi: {job.Error}");
        lines.AddRange(job.Steps.Select(s => $"  {s.Index + 1}. {s.StepId} [{s.Kind}] {s.Status}{(s.Error is null ? "" : " — " + s.Error)}"));
        if (job.Result is not null) lines.Add($"Kết quả: {job.Result.ToJsonString(ResultJson)}");
        return string.Join(Environment.NewLine, lines);
    }
}
