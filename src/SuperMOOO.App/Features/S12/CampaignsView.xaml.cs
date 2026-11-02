using System.Windows.Controls;

namespace SuperMOOO.App.Features.S12;

/// <summary>Nội dung tab "Chiến dịch" của S12 (do LibraryView nhúng vào TabControl).</summary>
[Service(ServiceLifetime.Transient)]
public partial class CampaignsView : UserControl
{
    public CampaignsView(CampaignsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;
    }

    public CampaignsViewModel ViewModel { get; }

    public Task LoadAsync() => ViewModel.LoadAsync();
}
