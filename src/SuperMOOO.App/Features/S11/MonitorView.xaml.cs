using SuperMOOO.Core.Operations;
using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S11;

[Screen("S11", "Theo dõi & Social Listening", ScreenRegistry.Workflow, 15)]
public partial class MonitorView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public MonitorView(MonitorViewModel viewModel, OperationRunnerPanel listening, ScreenJobsViewModel jobs, TrendsView trends)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S11"), JobTypes.WatchCheck);
        DataContext = viewModel;
        Tabs.Items.Insert(1, new TabItem { Header = "Xu hướng", Content = trends });
        trends.ViewModel.WatchAdded += async (_, _) => await viewModel.RefreshWatchAsync();
        listening.Initialize(OperationGroup.Monitor, "social_listening.get_youtube_channel_details_endpoint");
        listening.ShowRunButton = true;
        listening.Grid.ViewModel.JobsOwner = JobOwner.Screen("S11");
        listening.Margin = new Thickness(0, 12, 0, 0);
        Tabs.Items.Add(new TabItem { Header = "Social listening", Content = listening });
        viewModel.ChartChanged += (_, _) => Chart.SetSamples(viewModel.ChartSamples);
        Loaded += OnLoaded;
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ((MonitorViewModel)DataContext).LoadAsync();
        await _load;
    }
}
