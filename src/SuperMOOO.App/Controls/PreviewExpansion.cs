using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;
using Wpf.Ui.Controls;

namespace SuperMOOO.App.Controls;

/// <summary>Phóng to chính khung preview để giữ các overlay và binding chỉnh vị trí.</summary>
public static class PreviewExpansion
{
    public static void Show(Border host, string title)
    {
        if (host.Child is not FrameworkElement content) return;
        var owner = Window.GetWindow(host);
        var window = new FluentWindow
        {
            Owner = owner, Title = title, Width = 1200, Height = 800, WindowState = WindowState.Maximized,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            ExtendsContentIntoTitleBar = true, WindowBackdropType = ThemeService.Backdrop, DataContext = host.DataContext
        };
        var bar = new TitleBar { Title = title + " · Esc để thu nhỏ", ShowMinimize = false };
        var body = new Border { Margin = new Thickness(16), Background = host.Background };
        Grid.SetRow(body, 1);
        window.Content = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }, Children = { bar, body } };
        DialogKeys.CloseOnEscape(window);
        var previousScope = NameScope.GetNameScope(content);
        var scopeOwner = (DependencyObject?)host;
        while (scopeOwner is not null && NameScope.GetNameScope(scopeOwner) is null) scopeOwner = LogicalTreeHelper.GetParent(scopeOwner);
        var scope = scopeOwner is null ? null : NameScope.GetNameScope(scopeOwner);
        if (scope is not null) NameScope.SetNameScope(content, scope);
        host.Child = null;
        body.Child = content;
        try { window.ShowDialog(); }
        finally
        {
            body.Child = null;
            NameScope.SetNameScope(content, previousScope);
            host.Child = content;
        }
    }
}
