using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("platforms", "Nền tảng", 20)]
public partial class PlatformsSection : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public PlatformsSection(PlatformsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlatformsViewModel.Value) && viewModel.Value.Length == 0) ValueBox.Clear();
        };
    }

    private PlatformsViewModel ViewModel => (PlatformsViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        await _load;
    }

    private void OnValueChanged(object sender, RoutedEventArgs e) => ViewModel.Value = ValueBox.Password;
}
