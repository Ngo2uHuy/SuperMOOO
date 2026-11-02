using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("health", "Kiểm tra hệ thống", 60)]
public partial class HealthSection : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public HealthSection(HealthViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ((HealthViewModel)DataContext).RefreshAsync();
        await _load;
    }
}
