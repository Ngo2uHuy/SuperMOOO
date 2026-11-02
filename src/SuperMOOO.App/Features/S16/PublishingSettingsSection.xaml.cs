using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("publishing", "Đăng bài", 26)]
public partial class PublishingSettingsSection : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public PublishingSettingsSection(PublishingSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private PublishingSettingsViewModel ViewModel => (PublishingSettingsViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        await _load;
    }

    private async void OnSaveClient(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveClientAsync(ClientSecretBox.Password);
        ClientSecretBox.Clear();
    }

    private async void OnSaveHost(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveMediaHostAsync(HostSecretBox.Password);
        HostSecretBox.Clear();
    }

    private async void OnTestHost(object sender, RoutedEventArgs e) => await ViewModel.TestMediaHostAsync(HostSecretBox.Password);
}
