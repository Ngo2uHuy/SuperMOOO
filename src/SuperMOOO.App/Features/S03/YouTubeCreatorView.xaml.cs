using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S03;

[Screen("S03", "Video YouTube & Talkshow", ScreenRegistry.Creative, 1)]
public partial class YouTubeCreatorView : UserControl, IAsyncScreen
{
    private readonly MediaPreviewPane _youTubeClip;
    private readonly MediaPreviewPane _talkClip;
    private Task _load = Task.CompletedTask;

    public YouTubeCreatorView(CreatorViewModel viewModel, MediaPreviewPane youTubeClip, MediaPreviewPane talkClip, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S03"));
        DataContext = viewModel;
        _youTubeClip = youTubeClip;
        _talkClip = talkClip;
        youTubeClip.EmptyText = "Video YouTube sẽ phát được ở đây khi dựng xong.";
        ClipHost.Content = youTubeClip;
        viewModel.YouTube.ResultReady += (_, path) => youTubeClip.Open(path);
        Talk.Attach(viewModel.Talkshow, talkClip);
        // Một khung tác vụ chung cho hai tab: job talkshow có tiêu đề "Talkshow: …" (TalkshowService).
        jobs.ResultSelected += (job, path) =>
        {
            if (job.Title.StartsWith("Talkshow:", StringComparison.Ordinal)) viewModel.Talkshow.ShowResult(path);
            else viewModel.YouTube.ShowResult(path);
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _youTubeClip.Dispose();
            _talkClip.Dispose();
        };
    }

    public Task WhenLoadedAsync() => _load;

    private CreatorViewModel ViewModel => (CreatorViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _load = Task.WhenAll(ViewModel.YouTube.LoadAsync(), ViewModel.Talkshow.LoadAsync());
        await _load;
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        RunButton.Content = Tabs.SelectedIndex == 0 ? "Dựng video YouTube" : "Dựng talkshow";
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedIndex == 0) ViewModel.YouTube.StartCommand.Execute(null);
        else ViewModel.Talkshow.StartCommand.Execute(null);
    }

    private void OnSlidesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) ViewModel.YouTube.AddSlides(paths);
    }
}
