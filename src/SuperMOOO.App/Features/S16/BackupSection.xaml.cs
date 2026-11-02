using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("backup", "Sao lưu & lưu trữ", 28)]
public partial class BackupSection : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public BackupSection(BackupViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S16"), "backup.");
        jobs.JobSucceeded += viewModel.OnJobSucceeded;
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ((BackupViewModel)DataContext).LoadAsync();
        await _load;
    }
}
