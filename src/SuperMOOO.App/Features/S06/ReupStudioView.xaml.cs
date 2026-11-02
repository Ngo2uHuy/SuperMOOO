using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S06;

[Screen("S06", "Reup Studio", ScreenRegistry.Editing, 8)]
public partial class ReupStudioView : UserControl, IAsyncScreen
{
    private readonly MediaPreviewPane _clip;
    private Task _load = Task.CompletedTask;

    public ReupStudioView(ReupStudioViewModel viewModel, MediaPreviewPane clip, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S06"));
        DataContext = viewModel;
        _clip = clip;
        clip.EmptyText = "Bấm \"Xem đoạn 6 s\" để xem thử video với thiết lập hiện tại.";
        ClipHost.Content = clip;
        viewModel.PreviewClipReady += (_, path) => _clip.Open(path, autoPlay: true);
        jobs.ResultSelected += (_, path) =>
        {
            viewModel.OutputVideo = path;
            _clip.Open(path);
        };
        viewModel.PropertyChanged += OnChanged;
        Drop += OnDrop;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _clip.Dispose();
    }

    private void ExpandFrameClick(object sender, RoutedEventArgs e)
    {
        if (Frame.Source is null) return;
        ExpandFrameButton.Visibility = Visibility.Collapsed;
        try { PreviewExpansion.Show(FrameContainer, "Khung xem trước"); }
        finally { ExpandFrameButton.Visibility = Visibility.Visible; }
    }

    public Task WhenLoadedAsync() => _load;

    private ReupStudioViewModel ViewModel => (ReupStudioViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _load = ViewModel.LoadAsync();
        await _load;
    }

    private void OnDownloadedDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DownloadedList.SelectedItem is DownloadedVideo video) ViewModel.AddSources([video.Path]);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) ViewModel.AddSources(paths);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReupStudioViewModel.PreviewFramePath) || ViewModel.PreviewFramePath is not { } path || !File.Exists(path)) return;
        // OnLoad để không giữ khoá file khung hình.
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        Frame.Source = image;
        FrameEmpty.Visibility = Visibility.Collapsed;
    }
}
