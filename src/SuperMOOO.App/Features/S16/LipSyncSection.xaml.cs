using System.Windows.Controls;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("lipsync", "Avatar khớp môi", 12)]
public partial class LipSyncSection : UserControl
{
    public LipSyncSection(LipSyncViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadStatusAsync();
    }
}
