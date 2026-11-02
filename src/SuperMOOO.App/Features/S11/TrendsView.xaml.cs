using System.Windows.Controls;

namespace SuperMOOO.App.Features.S11;

/// <summary>Nội dung tab "Xu hướng" của S11 (do MonitorView chèn vào TabControl).</summary>
[Service(ServiceLifetime.Transient)]
public partial class TrendsView : UserControl
{
    public TrendsView(TrendsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;
    }

    public TrendsViewModel ViewModel { get; }
}
