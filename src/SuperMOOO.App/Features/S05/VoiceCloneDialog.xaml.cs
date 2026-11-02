using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using Wpf.Ui.Controls;

namespace SuperMOOO.App.Features.S05;

[Service(ServiceLifetime.Transient)]
public partial class VoiceCloneDialog : FluentWindow
{
    public VoiceCloneDialog(VoiceCloneViewModel viewModel)
    {
        InitializeComponent();
        WindowBackdropType = ThemeService.Backdrop;
        DialogKeys.CloseOnEscape(this);
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }
}
