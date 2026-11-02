using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Controls;

namespace SuperMOOO.App.Shell;

/// <summary>Màn chưa triển khai: cùng đầu màn và trạng thái rỗng như các màn thật.</summary>
public sealed class PlaceholderView : UserControl
{
    public PlaceholderView(ScreenDescriptor screen)
    {
        var header = new ScreenHeader { Title = screen.Title, Description = screen.Description };
        DockPanel.SetDock(header, Dock.Top);
        var empty = new EmptyState { Icon = screen.Icon, Text = "Màn này đang được xây dựng và sẽ có trong bản cập nhật tới." };
        var root = new DockPanel { Children = { header, empty } };
        root.SetResourceReference(MarginProperty, "PagePadding");
        Content = root;
    }
}
