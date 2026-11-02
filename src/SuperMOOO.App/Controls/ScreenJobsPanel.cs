using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Workflows;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SuperMOOO.App.Controls;

/// <summary>Một dòng job trong bảng (S15 và khung tác vụ của từng màn); thay Snapshot để cập nhật tại chỗ.</summary>
public sealed partial class JobRow : ObservableObject
{
    public JobRow(JobSnapshot snapshot) => Snapshot = snapshot;

    public string Id => Snapshot.Id;

    [ObservableProperty]
    public partial JobSnapshot Snapshot { get; set; }

    public string Title => Snapshot.Title;
    public string Type => Snapshot.Type;
    public JobStatus Status => Snapshot.Status;
    public string StatusLabel => Labels.Job(Snapshot.Status);
    public double ProgressPercent => Math.Round(Snapshot.Progress * 100);
    public string Summary => Snapshot.Error ?? Snapshot.Message ?? "";

    /// <summary>Bước pipeline đang chạy ("2/5 Tạo ảnh"); trống khi job không chạy hoặc không có bước.</summary>
    public string Stage => Snapshot.Status == JobStatus.Running && Snapshot.Steps.FirstOrDefault(s => s.Status == JobStatus.Running) is { } step
        ? $"{step.Index + 1}/{Snapshot.Steps.Count} {Labels.Term(step.Kind)}"
        : "";
    public string Created => Snapshot.CreatedAt.ToLocalTime().ToString("dd/MM HH:mm:ss");

    partial void OnSnapshotChanged(JobSnapshot value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Stage));
    }
}

/// <summary>Job do một màn tạo (lọc theo owner, tuỳ chọn thêm theo loại job), cập nhật trực tiếp qua WatchAsync; huỷ/thử lại ngay tại màn.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class ScreenJobsViewModel(IJobQueue queue, IDialogService dialogs) : ObservableObject
{
    private const int Limit = 50;

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly List<JobRow> _all = [];
    private string? _owner;
    private string? _typePrefix;
    private string? _ownerPrefix;
    private CancellationTokenSource? _watch;

    public ObservableCollection<JobRow> Jobs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(RetryCommand))]
    public partial JobRow? Selected { get; set; }

    [ObservableProperty]
    public partial bool HideFinished { get; set; }

    [ObservableProperty]
    public partial string Counts { get; set; } = "";

    /// <summary>typePrefix: thêm cả job cùng loại do nơi khác xếp cho màn này (vd. job kiểm tra theo dõi có owner riêng "watch:{id}").</summary>
    /// <param name="ownerPrefix">Owner có tiền tố này cũng thuộc màn (vd. "campaign:" cho job của từng chiến dịch ở S22).</param>
    public void Initialize(string owner, string? typePrefix = null, string? ownerPrefix = null)
    {
        _owner = owner;
        _typePrefix = typePrefix;
        _ownerPrefix = ownerPrefix;
    }

    public bool Matches(JobSnapshot snapshot) =>
        _owner is not null && snapshot.Owner == _owner
        || _ownerPrefix is not null && snapshot.Owner?.StartsWith(_ownerPrefix, StringComparison.Ordinal) == true
        || _typePrefix is not null && snapshot.Type.StartsWith(_typePrefix, StringComparison.Ordinal);

    public async Task LoadAsync()
    {
        if (_owner is null) return;
        var jobs = (await queue.ListAsync(new JobQuery(Owner: _owner, Limit: Limit))).ToList();
        if (_typePrefix is not null) jobs.AddRange(await queue.ListAsync(new JobQuery(TypePrefix: _typePrefix, Limit: Limit)));
        if (_ownerPrefix is not null)
            jobs.AddRange((await queue.ListAsync(new JobQuery(Limit: Limit * 4))).Where(j => j.Owner?.StartsWith(_ownerPrefix, StringComparison.Ordinal) == true));

        _all.Clear();
        _all.AddRange(jobs.DistinctBy(j => j.Id).OrderByDescending(j => j.CreatedAt).Take(Limit).Select(j => new JobRow(j)));
        Rebuild();
    }

    /// <summary>Gọi khi khung hiện ra: theo dõi trước rồi mới nạp danh sách để ít lỡ thay đổi xen giữa.</summary>
    public async Task StartAsync()
    {
        Stop();
        _watch = new CancellationTokenSource();
        _ = WatchAsync(_watch.Token);
        await LoadAsync();
    }

    public void Stop()
    {
        _watch?.Cancel();
        _watch = null;
    }

    /// <summary>Job của màn vừa chuyển sang Succeeded (gọi trên UI thread): màn dùng để làm mới dữ liệu do job tạo ra.</summary>
    public event Action<JobSnapshot>? JobSucceeded;

    /// <summary>Người dùng chọn một job đã xong có file kết quả còn trên đĩa: màn mở file đó ở khung kết quả của mình.</summary>
    public event Action<JobSnapshot, string>? ResultSelected;

    /// <summary>File kết quả chính của job pipeline: video, rồi audio, rồi file đầu tiên trong danh sách kết quả.</summary>
    public static string? OutputOf(JobSnapshot job) =>
        PipelineResult.Value(job.Result, "video") ?? PipelineResult.Value(job.Result, "outputAudio") ?? PipelineResult.Files(job.Result).FirstOrDefault();

    partial void OnSelectedChanged(JobRow? value)
    {
        if (value?.Status == JobStatus.Succeeded && OutputOf(value.Snapshot) is { } path && System.IO.File.Exists(path))
            ResultSelected?.Invoke(value.Snapshot, path);
    }

    public void Apply(JobSnapshot snapshot)
    {
        if (!Matches(snapshot)) return;
        var row = _all.FirstOrDefault(j => j.Id == snapshot.Id);
        var succeededNow = snapshot.Status == JobStatus.Succeeded && row?.Status != JobStatus.Succeeded;
        if (row is null)
        {
            row = new JobRow(snapshot);
            _all.Insert(0, row);
            if (IsShown(row)) Jobs.Insert(0, row);
            if (_all.Count > Limit)
            {
                Jobs.Remove(_all[^1]);
                _all.RemoveAt(_all.Count - 1);
            }
        }
        else
        {
            row.Snapshot = snapshot;
            // Job đã xong không quay lại trạng thái khác, nên chỉ cần gỡ khi đang ẩn job xong.
            if (!IsShown(row)) Jobs.Remove(row);
        }

        if (Selected?.Id == snapshot.Id)
        {
            CancelCommand.NotifyCanExecuteChanged();
            RetryCommand.NotifyCanExecuteChanged();
        }

        UpdateCounts();
        if (succeededNow) JobSucceeded?.Invoke(snapshot);
    }

    partial void OnHideFinishedChanged(bool value) => Rebuild();

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (Selected is { } row) await Guard(() => queue.CancelAsync(row.Id));
    }

    [RelayCommand]
    private async Task CancelAllAsync()
    {
        foreach (var row in _all.Where(j => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.WaitingManualRetry).ToList())
            await Guard(() => queue.CancelAsync(row.Id));
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (Selected is { } row) await Guard(() => queue.RetryAsync(row.Id, resumeFromFailedStep: true));
    }

    private bool CanCancel() => Selected?.Status is JobStatus.Queued or JobStatus.Running or JobStatus.WaitingManualRetry;

    private bool CanRetry() => Selected?.Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.WaitingManualRetry;

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            dialogs.Error("Không sao chép được vì clipboard đang bận. Vui lòng thử lại.");
        }
    }

    private bool IsShown(JobRow row) => !HideFinished || row.Status != JobStatus.Succeeded;

    private void Rebuild()
    {
        var keep = Selected?.Id;
        Jobs.Clear();
        foreach (var row in _all.Where(IsShown)) Jobs.Add(row);
        Selected = Jobs.FirstOrDefault(j => j.Id == keep);
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        int Of(params JobStatus[] statuses) => _all.Count(j => statuses.Contains(j.Status));
        Counts = _all.Count == 0 ? "" :
            $"Đang chạy {Of(JobStatus.Running)} · Chờ {Of(JobStatus.Queued)} · Lỗi {Of(JobStatus.Failed, JobStatus.WaitingManualRetry)} · Xong {Of(JobStatus.Succeeded)}";
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var snapshot in queue.WatchAsync(null, ct))
                if (Matches(snapshot)) await PostAsync(() => Apply(snapshot), ct);
        }
        catch (OperationCanceledException)
        {
            // dừng theo dõi khi khung bị ẩn (rời màn/đổi tab)
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
}

/// <summary>
/// Khung "Tác vụ của màn này": bảng gọn job do màn tạo (tên, trạng thái, tiến độ, thông báo/lỗi), huỷ/thử lại, ẩn job xong, mở S15.
/// View gọi <see cref="Attach"/> trong constructor; khung tự theo dõi khi hiện và dừng khi ẩn.
/// </summary>
public sealed class ScreenJobsPanel : UserControl
{
    public ScreenJobsPanel()
    {
        MaxHeight = 260;

        var title = new TextBlock { Text = "Tác vụ của màn này", VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(StyleProperty, "CardTitle");
        title.Margin = new Thickness(0, 0, 12, 0);
        var counts = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        counts.SetResourceReference(StyleProperty, "Hint");
        counts.TextWrapping = TextWrapping.NoWrap;
        counts.SetBinding(TextBlock.TextProperty, new Binding(nameof(ScreenJobsViewModel.Counts)));

        var hide = new CheckBox { Content = "Ẩn đã xong", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        hide.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ScreenJobsViewModel.HideFinished)));
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                hide,
                Tool(SymbolRegular.Dismiss24, "Huỷ tác vụ đang chọn", nameof(ScreenJobsViewModel.CancelCommand)),
                Tool(SymbolRegular.DismissCircle24, "Huỷ tất cả tác vụ đang chạy/chờ của màn này", nameof(ScreenJobsViewModel.CancelAllCommand)),
                Tool(SymbolRegular.ArrowRepeatAll24, "Thử lại tác vụ đang chọn từ bước lỗi", nameof(ScreenJobsViewModel.RetryCommand)),
                Tool(SymbolRegular.TaskListLtr24, "Xem mọi tác vụ ở màn Tác vụ (S15)", null)
            }
        };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(tools, Dock.Right);
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(tools);
        head.Children.Add(title);
        head.Children.Add(counts);

        var grid = new System.Windows.Controls.DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            MinHeight = 110,
            Columns =
            {
                new DataGridTextColumn { Header = "Tác vụ", Binding = new Binding(nameof(JobRow.Title)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) },
                new DataGridTextColumn { Header = "Trạng thái", Binding = new Binding(nameof(JobRow.StatusLabel)), Width = 110 },
                new DataGridTextColumn { Header = "Giai đoạn", Binding = new Binding(nameof(JobRow.Stage)), Width = 130 },
                ProgressColumn(),
                MessageColumn()
            }
        };
        grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ScreenJobsViewModel.Jobs)));
        grid.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(ScreenJobsViewModel.Selected)) { Mode = BindingMode.TwoWay });
        grid.ContextMenu = new ContextMenu();
        grid.ContextMenuOpening += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source
                || ItemsControl.ContainerFromElement(grid, source) is not DataGridRow { Item: JobRow row }
                || ViewModel is not { } vm)
            {
                e.Handled = true;
                return;
            }

            var menu = grid.ContextMenu;
            menu.Items.Clear();
            void Add(string header, string text) => menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = header, Command = vm.CopyTextCommand, CommandParameter = text,
                IsEnabled = !string.IsNullOrEmpty(text)
            });
            Add("Sao chép tên / liên kết tác vụ", row.Title);
            Add("Sao chép thông báo / lỗi", row.Summary);
            Add("Sao chép toàn bộ thông tin", $"{row.Title} ({row.Type}) · {row.StatusLabel}\n"
                + $"Mã tác vụ: {row.Id}\nGiai đoạn: {row.Stage}\nTiến độ: {row.ProgressPercent}%\n{row.Summary}");
        };

        var empty = new EmptyState { Icon = SymbolRegular.TaskListLtr24, Text = "Chưa có tác vụ nào của màn này.", IsHitTestVisible = false };
        empty.SetBinding(VisibilityProperty, new Binding("Jobs.Count") { Converter = new EmptyToVisibilityConverter() });

        DockPanel.SetDock(head, Dock.Top);
        Content = Cards.Section(null, new DockPanel { Children = { head, new Grid { Children = { grid, empty } } } }, fill: true, gap: false);

        Loaded += async (_, _) =>
        {
            if (ViewModel is { } vm) await vm.StartAsync();
        };
        Unloaded += (_, _) => ViewModel?.Stop();
    }

    public ScreenJobsViewModel? ViewModel { get; private set; }

    /// <summary>Gắn VM và owner của màn (vd. <c>JobOwner.Screen("S01")</c>); gọi một lần trong constructor của view.</summary>
    public void Attach(ScreenJobsViewModel viewModel, string owner, string? typePrefix = null, string? ownerPrefix = null)
    {
        viewModel.Initialize(owner, typePrefix, ownerPrefix);
        DataContext = ViewModel = viewModel;
    }

    private static Button Tool(SymbolRegular icon, string tip, string? command)
    {
        var button = new Button { Icon = new SymbolIcon(icon), ToolTip = tip };
        button.SetResourceReference(StyleProperty, "IconButton");
        if (command is not null) button.SetBinding(ButtonBase.CommandProperty, new Binding(command));
        else
        {
            // MainWindow xử lý GoToPage để chuyển màn, giống lệnh Help của ScreenHeader.
            button.Command = NavigationCommands.GoToPage;
            button.CommandParameter = "S15";
        }

        return button;
    }

    private static DataGridTemplateColumn ProgressColumn()
    {
        var percent = new FrameworkElementFactory(typeof(TextBlock));
        percent.SetValue(DockPanel.DockProperty, Dock.Right);
        percent.SetValue(WidthProperty, 40d);
        percent.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
        percent.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        percent.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        percent.SetBinding(TextBlock.TextProperty, new Binding(nameof(JobRow.ProgressPercent)) { StringFormat = "{0}%" });

        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetValue(RangeBase.MinimumProperty, 0d);
        bar.SetValue(RangeBase.MaximumProperty, 100d);
        bar.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bar.SetBinding(RangeBase.ValueProperty, new Binding(nameof(JobRow.ProgressPercent)) { Mode = BindingMode.OneWay });

        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(MarginProperty, new Thickness(4, 0, 4, 0));
        dock.AppendChild(percent);
        dock.AppendChild(bar);
        return new DataGridTemplateColumn { Header = "Tiến độ", Width = 140, CellTemplate = new DataTemplate { VisualTree = dock } };
    }

    /// <summary>Thông báo/lỗi dài bị cắt trong ô, đọc đủ ở tooltip.</summary>
    private static DataGridTextColumn MessageColumn()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(JobRow.Summary))));
        var noTip = new DataTrigger { Binding = new Binding(nameof(JobRow.Summary)), Value = "" };
        noTip.Setters.Add(new Setter(ToolTipProperty, null));
        style.Triggers.Add(noTip);
        return new DataGridTextColumn
        {
            Header = "Thông báo / lỗi", Binding = new Binding(nameof(JobRow.Summary)), ElementStyle = style,
            Width = new DataGridLength(3, DataGridLengthUnitType.Star)
        };
    }
}
