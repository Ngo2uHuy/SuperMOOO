using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SearchBox = Wpf.Ui.Controls.TextBox;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace SuperMOOO.App.Controls;

/// <summary>
/// `c:ComboSearch.IsEnabled="True"` trên ComboBox (bật cho mọi ComboBox qua style ngầm định ở Themes/Tokens.xaml):
/// danh sách xổ có ô "Tìm…" để lọc theo chữ đang hiển thị, không phân biệt hoa/thường và dấu; ô cho nhập tự do giữ editor riêng.
/// </summary>
public static class ComboSearch
{
    private const int MinItems = 1;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ComboSearch),
        new PropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty BoxProperty = DependencyProperty.RegisterAttached("Box", typeof(SearchBox), typeof(ComboSearch));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    /// <summary>Chữ thường, bỏ dấu tiếng Việt (kể cả đ) để so khớp "nhat" với "Tiếng Nhật".</summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(ch is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;
        combo.DropDownOpened -= OnOpened;
        combo.DropDownClosed -= OnClosed;
        combo.PreviewTextInput -= OnComboTextInput;
        if (e.NewValue is not true) return;
        combo.DropDownOpened += OnOpened;
        combo.DropDownClosed += OnClosed;
        combo.PreviewTextInput += OnComboTextInput;
    }

    private static void OnOpened(object? sender, EventArgs e)
    {
        var combo = (ComboBox)sender!;
        // ComboBox gõ tự do đã có ô nhập riêng.
        if ((combo.IsEditable && !combo.IsReadOnly) || EnsureBox(combo) is not { } box) return;
        box.Visibility = combo.Items.Count >= MinItems ? Visibility.Visible : Visibility.Collapsed;
        if (box.Visibility != Visibility.Visible) return;
        // Khi mở, ComboBox tự đưa focus vào mục đang chọn (ưu tiên Send); chuyển sang ô tìm sau bước đó.
        combo.Dispatcher.BeginInvoke(() =>
        {
            if (combo.IsDropDownOpen) box.Focus();
        }, DispatcherPriority.Input);
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        var combo = (ComboBox)sender!;
        if (combo.GetValue(BoxProperty) is not SearchBox box) return;
        if (box.IsKeyboardFocusWithin) combo.Focus();
        box.Text = "";
    }

    /// <summary>Đang ở trong danh sách mà gõ chữ thì đưa về ô tìm, thay cho nhảy theo chữ cái đầu của ComboBox.</summary>
    private static void OnComboTextInput(object sender, TextCompositionEventArgs e)
    {
        var combo = (ComboBox)sender;
        if (!combo.IsDropDownOpen || combo.GetValue(BoxProperty) is not SearchBox { IsVisible: true } box || box.IsKeyboardFocusWithin) return;
        box.Focus();
        box.Text += e.Text;
        box.CaretIndex = box.Text.Length;
        e.Handled = true;
    }

    /// <summary>
    /// Chèn ô tìm vào đầu danh sách xổ của template WPF-UI (Grid bọc ScrollViewer trong Popup). Lọc bằng cách ẩn ComboBoxItem
    /// thay vì đặt Filter: Filter làm Selector bỏ chọn mục bị lọc (đẩy null về ViewModel) và dùng chung view với ComboBox khác.
    /// </summary>
    private static SearchBox? EnsureBox(ComboBox combo)
    {
        if (Find<Popup>(combo) is not { Child: { } content } || Find<ScrollViewer>(content) is not { } list
            || VisualTreeHelper.GetParent(list) is not Grid host) return null;
        if (combo.GetValue(BoxProperty) is SearchBox existing && existing.Parent == host) return existing;

        var box = new SearchBox { PlaceholderText = "Tìm…", Icon = new SymbolIcon(SymbolRegular.Search24), Margin = new Thickness(8, 2, 8, 6) };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(list, 1);
        host.Children.Add(box);
        box.TextChanged += (_, _) => Filter(combo, box.Text);
        box.PreviewKeyDown += (_, args) => OnBoxKey(combo, box, args);
        combo.SetValue(BoxProperty, box);
        return box;
    }

    private static void Filter(ComboBox combo, string text)
    {
        var key = Fold(text.Trim());
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement container) continue;
            if (key.Length == 0) container.ClearValue(UIElement.VisibilityProperty);
            else container.Visibility = Fold(TextOf(combo, combo.Items[i], container)).Contains(key, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void OnBoxKey(ComboBox combo, SearchBox box, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                // Chưa gõ gì thì giữ lựa chọn cũ.
                if (box.Text.Trim().Length > 0 && Visible(combo).FirstOrDefault() is { } first)
                    combo.SetCurrentValue(Selector.SelectedItemProperty, combo.ItemContainerGenerator.ItemFromContainer(first));
                combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
                break;
            case Key.Escape:
                combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
                break;
            case Key.Down or Key.Up:
                // Vào danh sách; từ đó phím mũi tên/Enter do ComboBox tự xử lý như bình thường.
                var target = e.Key == Key.Down ? Visible(combo).FirstOrDefault() : Visible(combo).LastOrDefault();
                target?.Focus();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static IEnumerable<ComboBoxItem> Visible(ComboBox combo) =>
        Enumerable.Range(0, combo.Items.Count).Select(i => combo.ItemContainerGenerator.ContainerFromIndex(i))
            .OfType<ComboBoxItem>().Where(item => item.Visibility == Visibility.Visible && item.IsEnabled);

    /// <summary>Chữ người dùng nhìn thấy trên mục (DisplayMemberPath, ItemTemplate có converter nhãn…); mục chưa dựng giao diện thì lấy từ dữ liệu.</summary>
    private static string TextOf(ComboBox combo, object? item, DependencyObject container)
    {
        var builder = new StringBuilder();
        CollectText(container, builder);
        if (builder.Length > 0) return builder.ToString();
        if (combo.DisplayMemberPath is { Length: > 0 } path && item?.GetType().GetProperty(path)?.GetValue(item) is { } value) return value.ToString() ?? "";
        return item?.ToString() ?? "";
    }

    private static void CollectText(DependencyObject node, StringBuilder builder)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock { Text.Length: > 0 } text) builder.Append(text.Text).Append(' ');
            else CollectText(child, builder);
        }
    }

    private static T? Find<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if ((child as T ?? Find<T>(child)) is { } found) return found;
        }

        return null;
    }
}
