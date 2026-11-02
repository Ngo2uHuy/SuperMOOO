using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S15;

[Screen("S15", "Tác vụ (Jobs)", ScreenRegistry.Release, 19)]
public partial class JobsView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public JobsView(JobsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += (_, _) => ViewModel.StopWatching();
    }

    private JobsViewModel ViewModel => (JobsViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        ViewModel.StartWatching();
        await _load;
    }
}
