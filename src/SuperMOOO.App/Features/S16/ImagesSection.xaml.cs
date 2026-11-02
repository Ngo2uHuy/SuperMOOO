using System.Windows.Controls;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("images", "Ảnh AI", 4)]
public partial class ImagesSection : UserControl
{
    public ImagesSection(ImagesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
