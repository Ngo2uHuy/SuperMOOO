using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S07;

[Screen("S07", "Auto A→Z", ScreenRegistry.Workflow, 11)]
public partial class AutoWorkflowView : UserControl, IAsyncScreen
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private Task _load = Task.CompletedTask;

    public AutoWorkflowView(AutoWorkflowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) viewModel.AddSources(paths);
        };
        _refresh.Tick += async (_, _) =>
        {
            try { await viewModel.RefreshAsync(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* lượt sau thử lại */ }
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) => _refresh.Stop();
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _refresh.Start();
        if (_load != Task.CompletedTask) return;
        _load = ((AutoWorkflowViewModel)DataContext).LoadAsync();
        await _load;
    }
}
