using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SuperMOOO.App.Controls;

/// <summary>Thẻ nhóm thiết lập cho view dựng bằng code, cùng quy ước với style "Section" trong Themes/Tokens.xaml.</summary>
public static class Cards
{
    public static Card Section(string? title, UIElement content, bool fill = false, bool gap = true)
    {
        var body = content;
        if (title is not null)
        {
            var head = new TextBlock { Text = title };
            head.SetResourceReference(FrameworkElement.StyleProperty, "CardTitle");
            DockPanel.SetDock(head, Dock.Top);
            body = new DockPanel { Children = { head, content } };
        }

        return new Card
        {
            Content = body,
            Padding = new Thickness(16),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = fill ? VerticalAlignment.Stretch : VerticalAlignment.Top,
            VerticalContentAlignment = fill ? VerticalAlignment.Stretch : VerticalAlignment.Top,
            Margin = gap ? new Thickness(0, 0, 0, 12) : new Thickness(0)
        };
    }
}
