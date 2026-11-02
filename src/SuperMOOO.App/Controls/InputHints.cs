using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace SuperMOOO.App.Controls;

/// <summary>Gợi ý cho cả XAML và form dựng động; placeholder không trở thành giá trị của ô nhập.</summary>
public static class InputHints
{
    private static bool _installed;
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(InputHints),
        new PropertyMetadata(null, (d, _) => { if (d.GetValue(StateProperty) is HintState state) state.Refresh(); }));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(HintState), typeof(InputHints));
    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        // DatePicker đã có watermark ngày của template; không vẽ chồng lên watermark đó.
        foreach (var type in new[] { typeof(TextBox), typeof(PasswordBox), typeof(ComboBox) })
        {
            EventManager.RegisterClassHandler(type, FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Attach((Control)s)), true);
            EventManager.RegisterClassHandler(type, FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) => Attach((Control)s)), true);
        }
    }

    private static void Attach(Control control)
    {
        // Các PART của ComboBox/DatePicker/NumberBox đã có gợi ý ở control cha.
        if (!control.IsLoaded || control is TextBox { IsReadOnly: true }
            || control.TemplatedParent is TextBox or PasswordBox or ComboBox or DatePicker) return;
        if (control.GetValue(StateProperty) is not HintState state)
        {
            state = new HintState(control);
            control.SetValue(StateProperty, state);
        }
        state.Refresh();
    }

    private static string Hint(Control control)
    {
        if (GetText(control) is { Length: > 0 } text) return text;
        var label = AccessibleNames.InputLabel(control)?.Trim().TrimEnd(':', ' ', '*');
        if (label?.StartsWith('{') == true || label?.Length > 100) label = null;
        if (control is PasswordBox) return string.IsNullOrWhiteSpace(label) ? "Nhập mật khẩu hoặc khoá bí mật…" : $"Nhập {label}…";
        if (control is DatePicker) return "Chọn ngày (ngày/tháng/năm)";
        if (control is ComboBox combo)
            return string.IsNullOrWhiteSpace(label) ? (combo.IsEditable && !combo.IsReadOnly ? "Chọn hoặc nhập giá trị…" : "Chọn giá trị…") : $"Chọn {label}…";
        var path = BindingOperations.GetBinding(control, TextBox.TextProperty)?.Path?.Path?.Split('.').LastOrDefault() ?? "";
        if (path.Contains("Color", StringComparison.OrdinalIgnoreCase)) return "#RRGGBB, ví dụ #FFFFFF";
        if (path is "ScheduleTime" or "ScheduleEnd") return "HH:mm, ví dụ 09:00";
        if (path.EndsWith("Url", StringComparison.OrdinalIgnoreCase) || path.EndsWith("Uri", StringComparison.OrdinalIgnoreCase)) return "Nhập địa chỉ đầy đủ…";
        if (path.Contains("BoxText", StringComparison.Ordinal)) return "x, y, rộng, cao (0–1)";
        if (path is "PolicyPinnedIp") return "Ví dụ: 203.0.113.10";
        if (path is "TagsText" or "Tags" or "EditTags") return "Nhập các nhãn, cách nhau bằng dấu phẩy";
        if (path.EndsWith("Folder", StringComparison.Ordinal) || path.EndsWith("Root", StringComparison.Ordinal)) return "Chọn hoặc nhập đường dẫn thư mục…";
        if (path.EndsWith("Path", StringComparison.Ordinal) || path.EndsWith("File", StringComparison.Ordinal)) return "Chọn hoặc nhập đường dẫn tệp…";
        if (!string.IsNullOrWhiteSpace(label)) return $"Nhập {label}…";
        return path switch
        {
            "ScriptText" or "ProductScript" => "Nhập kịch bản lời thoại…",
            "Description" or "Caption" => "Nhập nội dung mô tả…",
            "Title" or "Name" => "Nhập tên hoặc tiêu đề…",
            "Time" => "Nhập thời gian…",
            _ => control is Wpf.Ui.Controls.NumberBox ? "Nhập số…" : "Nhập nội dung…"
        };
    }

    private sealed class HintState
    {
        private readonly Control _control;
        private HintAdorner? _adorner;
        private AdornerLayer? _layer;
        private string? _generated;

        public HintState(Control control)
        {
            _control = control;
            if (control is TextBox box) box.TextChanged += (_, _) => Refresh();
            if (control is PasswordBox password) password.PasswordChanged += (_, _) => Refresh();
            if (control is ComboBox combo) combo.SelectionChanged += (_, _) => Refresh();
            if (control is ComboBox) control.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Refresh()));
            control.DataContextChanged += (_, _) => Refresh();
            control.Unloaded += (_, _) => Detach();
        }

        public void Refresh()
        {
            if (!_control.IsLoaded) return;
            var hint = Hint(_control);
            if (_control is Wpf.Ui.Controls.TextBox box)
            {
                if (string.IsNullOrWhiteSpace(box.PlaceholderText) || box.PlaceholderText == _generated)
                {
                    _generated = hint;
                    box.SetCurrentValue(Wpf.Ui.Controls.TextBox.PlaceholderTextProperty, hint);
                }
                return;
            }
            var empty = _control switch
            {
                TextBox text => !text.IsReadOnly && text.Text.Length == 0,
                PasswordBox password => IsPasswordEmpty(password),
                ComboBox combo => combo.SelectedItem is null && string.IsNullOrEmpty(combo.Text),
                DatePicker date => date.SelectedDate is null && string.IsNullOrEmpty(date.Text),
                _ => false
            };
            if (!empty) { Detach(); return; }
            if (_adorner is null)
            {
                _layer = AdornerLayer.GetAdornerLayer(_control);
                if (_layer is null) return;
                _adorner = new HintAdorner(_control);
                _layer.Add(_adorner);
            }
            _adorner.Hint = hint;
            _adorner.InvalidateVisual();
        }

        private static bool IsPasswordEmpty(PasswordBox password)
        {
            using var value = password.SecurePassword;
            return value.Length == 0;
        }

        private void Detach()
        {
            if (_adorner is not null) _layer?.Remove(_adorner);
            _adorner = null;
            _layer = null;
        }
    }

    private sealed class HintAdorner(Control control) : Adorner(control)
    {
        public string Hint { get; set; } = "";

        protected override void OnRender(DrawingContext context)
        {
            IsHitTestVisible = false;
            var left = Math.Max(8, control.Padding.Left);
            var width = Math.Max(0, control.ActualWidth - left - (control is ComboBox or DatePicker ? 36 : 8));
            if (width < 1 || control.ActualHeight < 1) return;
            var brush = control.TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
            var text = new FormattedText(Hint, CultureInfo.CurrentUICulture, control.FlowDirection,
                new Typeface(control.FontFamily, control.FontStyle, control.FontWeight, control.FontStretch),
                control.FontSize, brush, VisualTreeHelper.GetDpi(control).PixelsPerDip)
                { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            context.PushClip(new RectangleGeometry(new Rect(control.RenderSize)));
            context.DrawText(text, new Point(left, control is TextBox { AcceptsReturn: true } ? Math.Max(6, control.Padding.Top)
                : Math.Max(0, (control.ActualHeight - text.Height) / 2)));
            context.Pop();
        }
    }
}
