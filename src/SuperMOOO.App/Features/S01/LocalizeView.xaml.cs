using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S01;

[Screen("S01", "Lồng tiếng video", ScreenRegistry.Editing, 5)]
public partial class LocalizeView : UserControl, IAsyncScreen
{
    private readonly MediaPreviewPane _result;
    private Task _load = Task.CompletedTask;

    public LocalizeView(LocalizeViewModel viewModel, MediaPreviewPane result, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S01"));
        DataContext = viewModel;
        _result = result;
        result.EmptyText = "Video lồng tiếng sẽ phát được ở đây khi chạy xong.";
        ResultHost.Content = result;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.ResultReady += (_, path) =>
        {
            if (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) _result.Open(path);
        };
        jobs.ResultSelected += (_, path) => viewModel.ShowResult(path);
        Loaded += OnLoaded;
        Unloaded += (_, _) => _result.Dispose();
    }

    private void ExpandFrameClick(object sender, RoutedEventArgs e)
    {
        if (Frame.Source is null) return;
        ExpandFrameButton.Visibility = Visibility.Collapsed;
        try { PreviewExpansion.Show(FrameContainer, "Khung xem trước"); }
        finally { ExpandFrameButton.Visibility = Visibility.Visible; }
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _load = ((LocalizeViewModel)DataContext).LoadAsync();
        await _load;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizeViewModel.PreviewFramePath)) return;
        var path = ((LocalizeViewModel)DataContext).PreviewFramePath;
        if (path is null || !File.Exists(path)) return;
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
