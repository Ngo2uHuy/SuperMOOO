using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S12;

/// <summary>S12: tab "Thư viện" và "Lịch sử tải"; tab Chiến dịch (P7) chèn vào giữa TabControl <c>Tabs</c>.</summary>
[Screen("S12", "Thư viện tham khảo & bình luận", ScreenRegistry.Workflow, 16)]
public partial class LibraryView : UserControl, IAsyncScreen
{
    private readonly CampaignsView _campaigns;
    private Task _load = Task.CompletedTask;

    public LibraryView(LibraryViewModel viewModel, CampaignsView campaigns)
    {
        InitializeComponent();
        DataContext = viewModel;
        _campaigns = campaigns;
        Tabs.Items.Insert(1, new TabItem { Header = "Chiến dịch", Content = campaigns });
        Loaded += OnLoaded;
    }

    public Task WhenLoadedAsync() => _load;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _load = Task.WhenAll(((LibraryViewModel)DataContext).LoadAsync(), _campaigns.LoadAsync());
        await _load;
    }
}
