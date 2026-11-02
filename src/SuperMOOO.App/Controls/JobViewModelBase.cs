using System.IO;
using System.Text.Json.Nodes;
using SuperMOOO.Core.Jobs;
using System.Collections.ObjectModel;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.App.Controls;

/// <summary>Khung chung cho màn xếp job pipeline rồi theo dõi: tiến độ, huỷ, lỗi qua IDialogService (VM không gọi MessageBox).</summary>
public abstract partial class JobViewModelBase(JobTracker tracker, IDialogService dialogs) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Log { get; } = [];

    protected IDialogService Dialogs => dialogs;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(ShowStatusBar))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusBar))]
    public partial string? Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusBar))]
    private partial bool IsQueuedRun { get; set; }

    /// <summary>
    /// Màn có khung "Tác vụ của màn này" đã hiện tiến độ/kết quả job xếp hàng, nên JobStatusBar chỉ hiện khi chạy thao tác
    /// trực tiếp (xem trước, đọc thử…) hoặc có thông báo ngoài job; tránh hai thanh tiến độ cho cùng một job.
    /// </summary>
    public bool ShowStatusBar => JobsOwner is null || (IsRunning ? !IsQueuedRun : !string.IsNullOrEmpty(Status));

    [ObservableProperty]
    public partial string? CurrentJobId { get; set; }

    /// <summary>Owner gắn cho job màn này xếp (vd. <c>JobOwner.Screen("S01")</c>) để khung "Tác vụ của màn này" lọc được.</summary>
    protected virtual string? JobsOwner => null;

    /// <summary>Bọc lệnh xếp job hàng loạt (không qua RunJobAsync) để job mang owner của màn.</summary>
    protected IDisposable? OwnJobs() => JobsOwner is { } owner ? JobOwner.Begin(owner) : null;

    /// <summary>Lỗi người dùng/IO/ffmpeg/nhà cung cấp là lỗi đã biết; còn lại (bug) để nổi lên.</summary>
    public static bool IsExpected(Exception ex) => ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException
        or FormatException or InvalidDataException or MediaProbeException or FfmpegException or ProviderException;

    /// <summary>Xếp job, chờ xong. Trả snapshot khi job thành công; huỷ/lỗi đã được báo nên trả null.</summary>
    protected async Task<JobSnapshot?> RunJobAsync(Func<CancellationToken, Task<string>> enqueue, string label)
    {
        if (IsRunning) return null;
        IsQueuedRun = true;
        IsRunning = true;
        Progress = 0;
        Status = label;
        Log.Clear();
        _cts = new CancellationTokenSource();
        try
        {
            using (OwnJobs()) CurrentJobId = await enqueue(_cts.Token);
            // Job chạy nền lâu: báo ngay là đã nhận lệnh để người dùng không bấm lại.
            dialogs.Info("Đã bắt đầu", $"{label.TrimEnd('…', '.')}. Theo dõi tiến độ ở khung Tác vụ của màn này.");
            var done = await tracker.WaitAsync(CurrentJobId, Show, _cts.Token);
            return Finish(done);
        }
        catch (OperationCanceledException)
        {
            if (CurrentJobId is not null) await tracker.CancelAsync(CurrentJobId);
            Status = "Đã huỷ.";
            return null;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Status = "Lỗi: " + ErrorText.Clean(ex.Message);
            dialogs.Error(ex.Message);
            return null;
        }
        finally
        {
            // Kết quả job đã nằm ở khung tác vụ (lỗi/xong còn báo qua dialog); xoá trước khi hết IsRunning để thanh không hiện lại.
            if (JobsOwner is not null) Status = null;
            IsRunning = false;
            IsQueuedRun = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Thao tác trực tiếp (xem trước, TTS kịch bản…) có tiến độ + huỷ như job; trả true nếu xong không lỗi.</summary>
    protected async Task<bool> RunDirectAsync(Func<IProgress<ProgressInfo>, CancellationToken, Task> work, string label)
    {
        if (IsRunning) return false;
        IsRunning = true;
        Progress = 0;
        Status = label;
        CurrentJobId = null;
        _cts = new CancellationTokenSource();
        try
        {
            var ok = await GuardAsync(() => work(new Progress<ProgressInfo>(p => { if (p.Fraction >= 0) Progress = Math.Round(p.Fraction * 100); }), _cts.Token));
            if (ok) Progress = 100;
            return ok;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Chạy một thao tác thường (không qua job) với cùng cách báo lỗi.</summary>
    protected async Task<bool> GuardAsync(Func<Task> work)
    {
        try
        {
            await work();
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = "Đã huỷ.";
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Status = "Lỗi: " + ErrorText.Clean(ex.Message);
            dialogs.Error(ex.Message);
        }

        return false;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    private void Show(JobSnapshot s)
    {
        if (s.Progress >= 0) Progress = Math.Round(s.Progress * 100);
        if (!string.IsNullOrEmpty(s.Message) && Log.LastOrDefault() != s.Message)
        {
            Log.Add(s.Message);
            Status = s.Message;
        }
    }

    private JobSnapshot? Finish(JobSnapshot done)
    {
        switch (done.Status)
        {
            case JobStatus.Succeeded:
                Progress = 100;
                Status = "Xong.";
                dialogs.Info("Hoàn tất", "Tác vụ đã xong; kết quả nằm ở khung Kết quả của màn này.");
                foreach (var w in PipelineResultWarnings(done)) Log.Add("Cảnh báo: " + w);
                return done;
            case JobStatus.Cancelled:
                Status = "Đã huỷ.";
                return null;
            default:
                Status = "Lỗi: " + done.Error;
                dialogs.Error(done.Error ?? "Tác vụ thất bại.");
                return null;
        }
    }

    private static IEnumerable<string> PipelineResultWarnings(JobSnapshot s) =>
        s.Result?["state"]?["warnings"] is JsonValue v && v.TryGetValue<string>(out var text)
            ? text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
}
