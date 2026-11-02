using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("background", "Chạy nền", 27)]
public partial class BackgroundSection : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public BackgroundSection(BackgroundViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ((BackgroundViewModel)DataContext).LoadAsync();
        await _load;
    }
}
