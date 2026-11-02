using System.Windows.Controls;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("legacy", "Nhập dữ liệu cũ", 70)]
public partial class LegacyImportSection : UserControl
{
    public LegacyImportSection(LegacyImportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
