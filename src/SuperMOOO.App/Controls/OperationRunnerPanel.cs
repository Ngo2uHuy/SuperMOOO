using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using SuperMOOO.Core.Operations;
using SuperMOOO.Infrastructure.Operations;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace SuperMOOO.App.Controls;

public sealed record OperationItem(OperationDescriptor Descriptor)
{
    public string Display => Descriptor.Label;

    public string Key => Descriptor.Key;
}

public delegate bool RequestProvider(out object? request);

/// <summary>Chạy một operation của một nhóm (Crawl/Monitor…): chọn operation, điền form động, xem kết quả trong bảng.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class OperationRunnerViewModel(OperationRegistry registry, OperationInvoker invoker, IDialogService dialogs) : ObservableObject
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private CancellationTokenSource? _cts;

    public ObservableCollection<OperationItem> Operations { get; } = [];
    private IReadOnlyList<OperationItem> _all = [];
    public ObservableCollection<string> Groups { get; } = [];
    public bool HasGroups => Groups.Count > 0;
    public bool CanSelect => !IsRunning;
    [ObservableProperty] public partial string? SelectedGroup { get; set; }

    /// <summary>Form gắn vào đây để VM lấy request đã kiểm hợp lệ.</summary>
    public RequestProvider? RequestSource { get; set; }

    public event EventHandler<JsonNode?>? Completed;
    public event EventHandler? OperationChanged;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial OperationItem? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    public void Initialize(OperationGroup group, string? preselectKey = null)
    {
        _all = registry.All.Where(d => d.Group == group).Select(d => new OperationItem(d)).OrderBy(o => o.Display).ToArray();
        Groups.Clear();
        if (group == OperationGroup.Crawl)
            foreach (var category in Features.S10.CrawlOperationGroups.All.Where(c => _all.Any(o => Features.S10.CrawlOperationGroups.For(o.Descriptor) == c))) Groups.Add(category);
        OnPropertyChanged(nameof(HasGroups));
        var selected = _all.FirstOrDefault(o => o.Key == preselectKey) ?? _all.FirstOrDefault();
        SelectedGroup = HasGroups && selected is not null ? Features.S10.CrawlOperationGroups.For(selected.Descriptor) : null;
        FilterOperations();
        Selected = Operations.FirstOrDefault(o => o.Key == selected?.Key) ?? Operations.FirstOrDefault();
    }

    partial void OnSelectedGroupChanged(string? value) => FilterOperations();
    private void FilterOperations()
    {
        Operations.Clear();
        foreach (var item in _all.Where(o => !HasGroups || Features.S10.CrawlOperationGroups.For(o.Descriptor) == SelectedGroup)) Operations.Add(item);
        Selected = Operations.FirstOrDefault();
    }

    partial void OnSelectedChanged(OperationItem? value) => OperationChanged?.Invoke(this, EventArgs.Empty);

    private bool CanRun() => Selected is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RunAsync()
    {
        object? request = null;
        if (Selected is null || RequestSource is null || !RequestSource(out request) || request is null) return;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Progress = 0;
        Status = "Đang chạy…";
        dialogs.Info("Đã bắt đầu", $"{Selected.Display}. Kết quả hiện ở bảng Kết quả khi xong.");
        try
        {
            var body = JsonSerializer.SerializeToNode(request, Selected.Descriptor.RequestType, Json) as JsonObject ?? [];
            var progress = new Progress<ProgressInfo>(p =>
            {
                Status = p.Message;
                Progress = Math.Max(0, p.Fraction);
            });
            var result = await invoker.InvokeAsync(Selected.Descriptor.Key, body, progress, _cts.Token);
            Status = result is JsonArray rows ? $"Xong: {rows.Count} dòng." : "Xong.";
            Completed?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
            Status = "Đã huỷ.";
        }
        catch (Exception ex) when (ex is ProviderException or ArgumentException or ValidationException or NotSupportedException
                                       or KeyNotFoundException or IOException or InvalidOperationException)
        {
            Status = ex is ProviderException p ? $"Lỗi ({p.Kind}): {p.Message}" : $"Lỗi: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();
}

/// <summary>Trái: chọn tác vụ + form + tiến độ; phải: bảng kết quả. Màn tự đặt nút chạy ở đầu màn hoặc bật <see cref="ShowRunButton"/>.</summary>
[Service(ServiceLifetime.Transient)]
public sealed class OperationRunnerPanel : UserControl
{
    private readonly DynamicOperationForm _form = new();
    private readonly Button _run = new() { Content = "Chạy tác vụ", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Play24), Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };

    public OperationRunnerPanel(OperationRunnerViewModel viewModel, ResultGrid grid)
    {
        DataContext = ViewModel = viewModel;
        Grid = grid;
        viewModel.RequestSource = (out object? request) => _form.TryGetRequest(out request);
        viewModel.OperationChanged += (_, _) => BindForm();
        viewModel.Completed += (_, result) =>
        {
            grid.ViewModel.LoadJson(result);
        };

        var picker = new ComboBox { DisplayMemberPath = nameof(OperationItem.Display), HorizontalAlignment = HorizontalAlignment.Stretch };
        // Key kỹ thuật chỉ hiện ở tooltip.
        var itemStyle = new Style(typeof(ComboBoxItem), (Style)Application.Current.FindResource(typeof(ComboBoxItem)));
        itemStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(OperationItem.Key))));
        picker.ItemContainerStyle = itemStyle;
        picker.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(OperationRunnerViewModel.Operations)));
        picker.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(OperationRunnerViewModel.Selected)) { Mode = BindingMode.TwoWay });
        _run.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(nameof(OperationRunnerViewModel.RunCommand)));

        var formFooter = new StackPanel { Children = { _run } };
        DockPanel.SetDock(formFooter, Dock.Bottom);
        var formBody = new DockPanel { Children = { formFooter, _form } };

        var categoryPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        categoryPicker.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(OperationRunnerViewModel.Groups)));
        categoryPicker.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(OperationRunnerViewModel.SelectedGroup)) { Mode = BindingMode.TwoWay });
        var categoryField = new Field { Header = "Nhóm tác vụ", Content = categoryPicker };
        categoryField.SetBinding(VisibilityProperty, new Binding(nameof(OperationRunnerViewModel.HasGroups)) { Converter = new BooleanToVisibilityConverter() });
        var pickers = new StackPanel { Children = { categoryField, new Field { Header = "Tác vụ", Content = picker } } };
        pickers.SetBinding(IsEnabledProperty, new Binding(nameof(OperationRunnerViewModel.CanSelect)));
        var pick = Cards.Section("Chọn tác vụ", pickers);
        DockPanel.SetDock(pick, Dock.Top);
        var status = new JobStatusBar { Maximum = 1 };
        DockPanel.SetDock(status, Dock.Bottom);
        var settings = new DockPanel { Children = { pick, status, Cards.Section("Thiết lập", formBody, fill: true) } };

        Content = new SplitView { Primary = settings, Secondary = Cards.Section("Kết quả", grid, fill: true, gap: false), PrimaryRatio = 0.7 };
    }

    public OperationRunnerViewModel ViewModel { get; }

    public ResultGrid Grid { get; }

    /// <summary>Hiện nút "Chạy tác vụ" dưới form (khi panel nằm trong tab, đầu màn không có nút chạy riêng).</summary>
    public bool ShowRunButton
    {
        get => _run.Visibility == Visibility.Visible;
        set => _run.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Initialize(OperationGroup group, string? preselectKey = null)
    {
        ViewModel.Initialize(group, preselectKey);
        BindForm();
    }

    private void BindForm()
    {
        if (ViewModel.Selected is not { } item) return;
        _form.Bind(item.Descriptor.RequestType, Activator.CreateInstance(item.Descriptor.RequestType),
            Social.Crawlers.PlatformCategories.PlatformOf(item.Key));
    }
}
