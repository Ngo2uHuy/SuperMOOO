using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S18;

[Screen("S18", "Video vẽ tay AI", ScreenRegistry.Creative, 2)]
public partial class WhiteboardView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public WhiteboardView(WhiteboardViewModel viewModel, MediaPreviewPane clip, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S18"));
        DataContext = viewModel;
        clip.EmptyText = "Video vẽ tay phát được ở đây khi dựng xong (chọn job đã xong ở khung Tác vụ).";
        ClipHost.Content = clip;
        jobs.ResultSelected += (_, path) => clip.Open(path);
        Unloaded += (_, _) => clip.Dispose();
        Loaded += OnLoaded;
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _load = ((WhiteboardViewModel)DataContext).LoadAsync();
        await _load;
    }
}
