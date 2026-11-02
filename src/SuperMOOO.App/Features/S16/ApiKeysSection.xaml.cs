using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("apikeys", "API key", 2)]
public partial class ApiKeysSection : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public ApiKeysSection(ApiKeysViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private ApiKeysViewModel ViewModel => (ApiKeysViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        await _load;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddAsync(KeyBox.Password);
        KeyBox.Clear();
    }
}
