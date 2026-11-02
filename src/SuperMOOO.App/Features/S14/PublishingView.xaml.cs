using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.App.Features.S14;

[Screen("S14", "Đăng bài & lịch đăng", ScreenRegistry.Release, 17)]
public partial class PublishingView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public PublishingView(PublishingViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S14"), PublishJobTypes.Upload);
        DataContext = viewModel;
        viewModel.Analytics.ChartChanged += (_, _) => AnalyticsChart.SetSamples(viewModel.Analytics.ChartSamples);
        Loaded += OnLoaded;
        Unloaded += async (_, _) => { ViewModel.StopWatching(); await ViewModel.Compose.AutoSaveAsync(); };
    }

    private PublishingViewModel ViewModel => (PublishingViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        ViewModel.StartWatching();
        await _load;
    }
}
