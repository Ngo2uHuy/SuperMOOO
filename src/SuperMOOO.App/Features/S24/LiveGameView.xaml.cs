using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S24;

[Screen("S24", "Live tương tác", ScreenRegistry.Release, 19)]
public partial class LiveGameView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;
    public LiveGameView(LiveGameViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent(); DataContext = viewModel;
        ScreenJobs.Attach(jobs, JobOwner.Screen("S24"));
        Loaded += async (_, _) => { viewModel.Attach(); _load = viewModel.LoadAsync(); await _load; };
        Unloaded += (_, _) => viewModel.Detach();
    }
    public Task WhenLoadedAsync() => _load;
}
