using System.Windows.Controls;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("ttslocal", "TTS local (C++)", 11)]
public partial class TtsLocalSection : UserControl
{
    public TtsLocalSection(TtsLocalViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
