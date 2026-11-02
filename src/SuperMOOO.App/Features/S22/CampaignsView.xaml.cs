using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S22;

[Screen("S22", "Chiến dịch đăng tự động", ScreenRegistry.Release, 18)]
public partial class CampaignsView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public CampaignsView(CampaignsViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        // Job sản xuất của chiến dịch có owner "campaign:<id>" (mỗi chiến dịch một owner).
        ScreenJobs.Attach(jobs, JobOwner.Screen("S22"), ownerPrefix: "campaign:");
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private CampaignsViewModel ViewModel => (CampaignsViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        await _load;
    }
}
