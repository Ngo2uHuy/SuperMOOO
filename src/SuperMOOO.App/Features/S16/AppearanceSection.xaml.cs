using System.Windows.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("appearance", "Giao diện", 0)]
public partial class AppearanceSection : UserControl
{
    public AppearanceSection(ThemeService theme)
    {
        InitializeComponent();
        var modes = new Dictionary<RadioButton, string> { [SystemMode] = ThemeService.System, [LightMode] = ThemeService.Light, [DarkMode] = ThemeService.Dark };
        foreach (var (radio, mode) in modes)
        {
            radio.IsChecked = theme.Mode == mode;
            radio.Checked += async (_, _) => await theme.SetModeAsync(mode);
        }
    }
}
