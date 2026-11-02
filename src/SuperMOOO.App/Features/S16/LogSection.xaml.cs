using System.Windows;
using System.Windows.Controls;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("logs", "Nhật ký", 50)]
public partial class LogSection : UserControl
{
    public LogSection(LogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Refresh();
    }
}
