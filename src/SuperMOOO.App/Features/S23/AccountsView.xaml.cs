using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S23;

[Screen("S23", "Tài khoản & kênh", ScreenRegistry.Release, 18)]
public partial class AccountsView : UserControl, IAsyncScreen
{
    private Task _load = Task.CompletedTask;
    private readonly System.Windows.Threading.DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _refreshing;

    public AccountsView(AccountsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _refresh.Stop();
        _refresh.Tick += async (_, _) =>
        {
            if (ViewModel.SelectedTabIndex == AccountsViewModel.BrowserTabIndex)
            {
                ViewModel.RefreshBrowserStates();
                return;
            }
            if (_refreshing || ViewModel.IsBusy || ViewModel.SelectedTabIndex != 0) return;
            _refreshing = true;
            try { await ViewModel.LoadAsync(); }
            finally { _refreshing = false; }
        };
    }

    private AccountsViewModel ViewModel => (AccountsViewModel)DataContext;

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = ViewModel.LoadAsync();
        await _load;
        if (IsLoaded) _refresh.Start();
    }
}
