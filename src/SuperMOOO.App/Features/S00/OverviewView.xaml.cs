using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S00;

[Screen("S00", "Tổng quan", ScreenRegistry.Overview, 0)]
public partial class OverviewView : UserControl, IAsyncScreen
{
    private readonly DispatcherTimer _usage = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _jobs = new() { Interval = TimeSpan.FromSeconds(4) };
    private Task _load = Task.CompletedTask;

    public OverviewView(OverviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _usage.Tick += (_, _) => ViewModel.RefreshUsage();
        _jobs.Tick += async (_, _) => await RefreshJobsAsync();
        Loaded += OnLoaded;
        // Chỉ cập nhật khi màn đang hiện để không tốn tài nguyên lúc làm việc ở màn khác.
        Unloaded += (_, _) =>
        {
            _usage.Stop();
            _jobs.Stop();
        };
    }

    private OverviewViewModel ViewModel => (OverviewViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        _usage.Start();
        _jobs.Start();
        await _load;
    }

    private async Task RefreshJobsAsync()
    {
        _jobs.Stop();
        try
        {
            await ViewModel.RefreshJobsAsync();
        }
        finally
        {
            if (IsLoaded) _jobs.Start();
        }
    }
}
