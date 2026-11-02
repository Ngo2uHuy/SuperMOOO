using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("network", "Mạng", 40)]
public partial class NetworkSection : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public NetworkSection(NetworkViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private NetworkViewModel ViewModel => (NetworkViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        await _load;
    }

    private async void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveProfileAsync(PasswordBox.Password);
        PasswordBox.Clear();
    }
}
