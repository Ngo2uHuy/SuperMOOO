using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S20;

[Screen("S20", "Livestream bán hàng", ScreenRegistry.Release, 18)]
public partial class LivestreamView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public LivestreamView(LivestreamViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S20"));
        DataContext = viewModel;
        // Clip dựng xong đã được pipeline ghi vào DB: nạp lại lên form.
        jobs.JobSucceeded += _ => Application.Current?.Dispatcher.BeginInvoke(async () => await viewModel.OnJobSucceededAsync());
        Loaded += OnLoaded;
        Unloaded += (_, _) => ViewModel.Detach();
    }

    private LivestreamViewModel ViewModel => (LivestreamViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Attach();
        _load = ViewModel.LoadAsync();
        await _load;
    }
}
