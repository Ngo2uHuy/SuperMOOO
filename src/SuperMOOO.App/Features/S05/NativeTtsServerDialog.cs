using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S16;
using SuperMOOO.App.Shell;
using Wpf.Ui.Controls;

namespace SuperMOOO.App.Features.S05;

/// <summary>Cấu hình/khởi động/nạp model TTS C++ local: dùng lại đúng mục "TTS local" của S16.</summary>
[Service(ServiceLifetime.Transient)]
public sealed class NativeTtsServerDialog : FluentWindow
{
    public NativeTtsServerDialog(TtsLocalSection section)
    {
        Title = "TTS C++ local · cấu hình & nạp model";
        Width = 820;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar = false;
        WindowBackdropType = ThemeService.Backdrop;
        DialogKeys.CloseOnEscape(this);

        var titleBar = new TitleBar { Title = Title, ShowMinimize = false, ShowMaximize = false };
        section.Margin = new Thickness(24, 8, 24, 24);
        Grid.SetRow(section, 1);
        var host = new ContentDialogHost();
        Grid.SetRowSpan(host, 2);
        Content = new Grid
        {
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() },
            Children = { titleBar, section, host }
        };
    }
}
