using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S19;

[Screen("S19", "Avatar khớp môi", ScreenRegistry.Creative, 3)]
public partial class AvatarView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public AvatarView(AvatarViewModel viewModel, MediaPreviewPane clip, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S19"));
        DataContext = viewModel;
        clip.EmptyText = "Video avatar phát được ở đây khi dựng xong (chọn job đã xong ở khung Tác vụ).";
        ClipHost.Content = clip;
        jobs.ResultSelected += (_, path) => clip.Open(path);
        // Dò mặt xong thì cập nhật trạng thái avatar trong danh sách.
        jobs.JobSucceeded += _ => Application.Current?.Dispatcher.BeginInvoke(async () => await viewModel.OnJobSucceededAsync());
        Unloaded += (_, _) => clip.Dispose();
        Loaded += OnLoaded;
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _load = ((AvatarViewModel)DataContext).LoadAsync();
        await _load;
    }
}
