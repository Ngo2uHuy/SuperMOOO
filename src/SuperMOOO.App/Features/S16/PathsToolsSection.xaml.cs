using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("paths", "Đường dẫn & công cụ", 1)]
public partial class PathsToolsSection : UserControl
{
    public PathsToolsSection(PathsToolsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private PathsToolsViewModel ViewModel => (PathsToolsViewModel)DataContext;

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Chọn thư mục xuất" };
        if (dialog.ShowDialog() == true) ViewModel.OutputRoot = dialog.FolderName;
    }

    private void OnBrowseTool(object sender, RoutedEventArgs e)
    {
        var row = (ToolRow)((FrameworkElement)sender).Tag;
        var dialog = new OpenFileDialog { Title = $"Chọn {row.Label}", Filter = "Chương trình (*.exe)|*.exe|Tất cả|*.*" };
        if (dialog.ShowDialog() == true) row.Path = dialog.FileName;
    }
}
