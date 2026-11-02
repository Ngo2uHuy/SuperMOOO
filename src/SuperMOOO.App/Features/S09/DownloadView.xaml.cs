using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S09;

[Screen("S09", "Tải video", ScreenRegistry.Workflow, 13)]
public partial class DownloadView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public DownloadView(DownloadViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S09"));
        jobs.JobSucceeded += _ => viewModel.RefreshCommand.Execute(null);
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ((DownloadViewModel)DataContext).LoadAsync();
        await _load;
    }
}
