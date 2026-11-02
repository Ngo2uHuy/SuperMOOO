using System.Windows.Controls;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("livellm", "Livestream (LLM)", 5)]
public partial class LiveLlmSection : UserControl
{
    public LiveLlmSection(LiveLlmViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
