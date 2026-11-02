using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S04;

[Screen("S04", "Review & tóm tắt phim", ScreenRegistry.Editing, 7)]
public partial class MovieReviewView : UserControl, IAsyncScreen
{
    private readonly MediaPreviewPane _result;
    private Task _load = Task.CompletedTask;

    public MovieReviewView(MovieReviewViewModel viewModel, MediaPreviewPane result, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S04"));
        DataContext = viewModel;
        _result = result;
        result.EmptyText = "Video review sẽ phát được ở đây khi xuất xong.";
        ResultHost.Content = result;
        viewModel.ResultReady += (_, path) => _result.Open(path);
        jobs.ResultSelected += (_, path) =>
        {
            viewModel.OutputVideo = path;
            _result.Open(path);
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) viewModel.AddMovies(paths);
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) => _result.Dispose();
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _load = ((MovieReviewViewModel)DataContext).LoadAsync();
        await _load;
    }
}
