using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SuperMOOO.App.Shell;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S08;

[Screen("S08", "Reup tự động", ScreenRegistry.Workflow, 12)]
public partial class AutoReupView : UserControl, IAsyncScreen
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(5) };
    private Task _load = Task.CompletedTask;

    public AutoReupView(AutoReupViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S08"));
        DataContext = viewModel;
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
        _load = ((AutoReupViewModel)DataContext).LoadAsync();
        await _load;
    }
}
