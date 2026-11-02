using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SuperMOOO.App.Controls;

/// <summary>
/// Đặt tên truy cập (UI Automation / đọc màn hình) tự động cho điều khiển chưa có tên: nút icon lấy từ tooltip,
/// ô nhập/ComboBox/Slider lấy từ tooltip, gợi ý mờ hoặc nhãn đứng ngay trước
/// (không để lộ ToString() của model kỹ thuật).
/// </summary>
public static class AccessibleNames
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Register(typeof(ButtonBase), element => ByToolTip(element));
        Register(typeof(TextBoxBase), element => FieldHeader(element) ?? ByToolTip(element) ?? LabelBefore(element) ?? Placeholder(element));
        Register(typeof(Selector), element => FieldHeader(element) ?? ByToolTip(element) ?? LabelBefore(element));
        Register(typeof(Slider), element => FieldHeader(element) ?? ByToolTip(element) ?? LabelBefore(element));
        Register(typeof(ListBoxItem), RowText);
        Register(typeof(DataGridRow), RowText);
        Register(typeof(PasswordBox), element => FieldHeader(element) ?? ByToolTip(element) ?? LabelBefore(element) ?? Placeholder(element));
    }

    /// <summary>Đặt tên cho <paramref name="element"/> nếu chưa có (dùng cho Field khi nội dung đổi).</summary>
    public static void Apply(FrameworkElement element, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(AutomationProperties.GetName(element))) return;
        AutomationProperties.SetName(element, name.Trim());
    }

    internal static string? InputLabel(FrameworkElement element) => FieldHeader(element) ?? LabelBefore(element)
        ?? (AutomationProperties.GetName(element) is { Length: > 0 } name ? name : null)
        ?? ByToolTip(element);

    private static void Register(Type type, Func<FrameworkElement, string?> name) =>
        EventManager.RegisterClassHandler(type, FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) =>
        {
            var element = (FrameworkElement)s;
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)) && !HasText(element)) Apply(element, name(element));
        }), handledEventsToo: true);

    // Dùng SizeChanged thay cho Loaded: Loaded không tới được Slider và Button của WPF-UI (đã đo bằng log).
    // Nút có chữ hiển thị đã tự có tên.
    private static bool HasText(FrameworkElement element) => element is ContentControl { Content: string { Length: > 0 } };

    private static string? ByToolTip(FrameworkElement element) => element.ToolTip is string { Length: > 0 } tip ? tip : null;

    private static string? Placeholder(FrameworkElement element) =>
        element.GetType().GetProperty("PlaceholderText")?.GetValue(element) as string is { Length: > 0 } text ? text : null;

    /// <summary>Tên mục danh sách/dòng bảng = chữ hiển thị đầu tiên (ô của dòng có thể dựng sau nên thử lại khi rảnh).</summary>
    private static string? RowText(FrameworkElement row)
    {
        if (FirstText(row) is { } text) return text;
        row.Dispatcher.BeginInvoke(() => Apply(row, FirstText(row)), System.Windows.Threading.DispatcherPriority.Background);
        return null;
    }

    private static string? FirstText(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock text && text.Text.Any(char.IsLetterOrDigit)) return text.Text;   // bỏ qua glyph icon (vùng ký tự riêng)
            if (FirstText(child) is { } found) return found;
        }

        return null;
    }

    /// <summary>Nhãn của <see cref="Field"/> chứa ô nhập (tối đa 3 tầng bọc, ví dụ ô + nút chọn file trong DockPanel).</summary>
    private static string? FieldHeader(FrameworkElement element)
    {
        DependencyObject? current = element;
        for (var depth = 0; depth < 3 && current is not null; depth++, current = LogicalTreeHelper.GetParent(current))
            if (LogicalTreeHelper.GetParent(current) is Field { Header: string header }) return header.TrimEnd(' ', '*');
        return null;
    }

    private static string? LabelBefore(FrameworkElement element)
    {
        DependencyObject current = element;
        for (var depth = 0; depth < 3 && current is not null; depth++)
        {
            if (VisualTreeHelper.GetParent(current) is Panel panel)
            {
                // Chỉ nhận nhãn đứng liền trước; bỏ qua chữ hiển thị giá trị ("1.05x", "30 s") cạnh thanh trượt.
                for (var i = panel.Children.IndexOf((UIElement)current) - 1; i >= 0; i--)
                {
                    if (panel.Children[i] is not TextBlock { Visibility: Visibility.Visible } label) break;
                    if (label.Text.Length > 0 && !double.TryParse(label.Text.TrimEnd('x', 's', '%', ' '), out _)) return label.Text;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

}
