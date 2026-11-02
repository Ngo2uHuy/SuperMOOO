using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S21;

[Screen("S21", "Kho thành phẩm", ScreenRegistry.Release, 18)]
public partial class ContentLibraryView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;

    public ContentLibraryView(ContentLibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private ContentLibraryViewModel ViewModel => (ContentLibraryViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        await _load;
    }
}
