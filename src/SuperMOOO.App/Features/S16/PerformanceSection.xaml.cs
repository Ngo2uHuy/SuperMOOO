using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("performance", "Hiệu năng & phần cứng", 15)]
public partial class PerformanceSection : UserControl, IAsyncScreen
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private Task _load = Task.CompletedTask;
    public PerformanceSection(PerformanceViewModel vm)
    {
        InitializeComponent(); DataContext = vm; Loaded += OnLoaded;
        _refresh.Tick += (_, _) => vm.ShowLoad();
        Loaded += (_, _) => _refresh.Start();
        Unloaded += (_, _) => _refresh.Stop();
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _load = ((PerformanceViewModel)DataContext).RefreshAsync();
        await _load;
    }
    public Task WhenLoadedAsync() => _load;
}
