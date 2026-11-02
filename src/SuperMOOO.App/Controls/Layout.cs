using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace SuperMOOO.App.Controls;

/// <summary>Đầu màn: tiêu đề, một dòng mô tả, nút hành động chính (Content) và nút Hướng dẫn (F1). Template ở Themes/Tokens.xaml.</summary>
public sealed class ScreenHeader : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ScreenHeader));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(ScreenHeader));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

/// <summary>Ô nhập có nhãn ở trên và gợi ý mờ ở dưới.</summary>
public sealed class Field : HeaderedContentControl
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(Field));

    public string? Hint { get => (string?)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    // Nhãn của Field cũng là tên truy cập của ô nhập bên trong.
    protected override void OnContentChanged(object oldContent, object newContent) { base.OnContentChanged(oldContent, newContent); Label(); }

    protected override void OnHeaderChanged(object oldHeader, object newHeader) { base.OnHeaderChanged(oldHeader, newHeader); Label(); }

    private void Label()
    {
        if (Content is FrameworkElement element && Header is string text) AccessibleNames.Apply(element, text.TrimEnd(' ', '*'));
    }
}

/// <summary>Trạng thái rỗng: icon lớn, một câu hướng dẫn, nút hành động (Content).</summary>
public sealed class EmptyState : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(EmptyState),
        new PropertyMetadata(SymbolRegular.DocumentDismiss24));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(EmptyState));

    public SymbolRegular Icon { get => (SymbolRegular)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}

/// <summary>Null, chuỗi rỗng, số 0 hoặc danh sách rỗng → Visible (dùng cho trạng thái rỗng); Invert để hiện khi có dữ liệu.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string s => s.Length == 0,
            int n => n == 0,
            ICollection c => c.Count == 0,
            _ => false
        };
        return empty != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Bố cục 2 vùng (thiết lập | xem trước/kết quả) có GridSplitter; hẹp hơn <see cref="Threshold"/> thì xếp dọc.</summary>
public sealed class SplitView : Grid
{
    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(nameof(Primary), typeof(UIElement), typeof(SplitView),
        new PropertyMetadata(null, (d, e) => ((SplitView)d).Swap((UIElement?)e.OldValue, (UIElement?)e.NewValue)));
    public static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(nameof(Secondary), typeof(UIElement), typeof(SplitView),
        new PropertyMetadata(null, (d, e) => ((SplitView)d).Swap((UIElement?)e.OldValue, (UIElement?)e.NewValue)));

    private readonly GridSplitter _splitter = new() { ResizeBehavior = GridResizeBehavior.PreviousAndNext };
    private bool? _vertical;

    public SplitView()
    {
        _splitter.SetResourceReference(StyleProperty, "SplitterGrip");
        Children.Add(_splitter);
        SizeChanged += (_, e) => Layout(e.NewSize.Width < Threshold);
    }

    public UIElement? Primary { get => (UIElement?)GetValue(PrimaryProperty); set => SetValue(PrimaryProperty, value); }
    public UIElement? Secondary { get => (UIElement?)GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }

    /// <summary>Tỉ lệ vùng thiết lập so với vùng xem trước khi xếp ngang.</summary>
    public double PrimaryRatio { get; set; } = 1;

    public double Threshold { get; set; } = 900;

    private void Swap(UIElement? old, UIElement? next)
    {
        if (old is not null) Children.Remove(old);
        if (next is not null) Children.Add(next);
        _vertical = null;
        Layout(ActualWidth > 0 && ActualWidth < Threshold);
    }

    private void Layout(bool vertical)
    {
        if (_vertical == vertical) return;
        _vertical = vertical;
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        // Xếp dọc thì vùng thiết lập cần cao hơn vùng xem trước để còn thao tác được.
        var primary = vertical ? 1.6 : PrimaryRatio;
        var defs = new[] { new GridLength(primary, GridUnitType.Star), new GridLength(12), new GridLength(1, GridUnitType.Star) };
        if (vertical)
            foreach (var d in defs) RowDefinitions.Add(new RowDefinition { Height = d, MinHeight = d.IsStar ? 160 : 0 });
        else
            foreach (var d in defs) ColumnDefinitions.Add(new ColumnDefinition { Width = d, MinWidth = d.IsStar ? 320 : 0 });

        _splitter.ResizeDirection = vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        _splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
        _splitter.VerticalAlignment = VerticalAlignment.Stretch;
        Place(Primary, 0, vertical);
        Place(_splitter, 1, vertical);
        Place(Secondary, 2, vertical);
    }

    private static void Place(UIElement? element, int index, bool vertical)
    {
        if (element is null) return;
        SetRow(element, vertical ? index : 0);
        SetColumn(element, vertical ? 0 : index);
    }
}
