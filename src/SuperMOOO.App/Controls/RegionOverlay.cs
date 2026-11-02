using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SuperMOOO.App.Controls;

/// <summary>
/// Lớp phủ đặt cùng ô lưới, cùng Margin với một Image Stretch=Uniform: xếp các <see cref="FrameRegion"/> theo % vùng ảnh thực hiển thị
/// (đã trừ dải letterbox). Không có nền nên bấm ra ngoài các vùng vẫn xuyên xuống phần tử bên dưới.
/// </summary>
public sealed class RegionOverlay : Panel
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(RegionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Vùng ảnh thực hiển thị trong lớp phủ (DIP); rỗng khi chưa có ảnh.</summary>
    internal Rect Frame { get; private set; } = Rect.Empty;

    /// <summary>Kích thước điểm ảnh của khung nguồn, để quy cỡ chữ phụ đề (px video) ra % chiều cao.</summary>
    internal Size SourcePixels => Source switch
    {
        BitmapSource b => new Size(b.PixelWidth, b.PixelHeight),
        { } s => new Size(s.Width, s.Height),
        _ => new Size()
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren) child.Measure(availableSize);
        return new Size();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Frame = Fit(finalSize);
        foreach (UIElement child in InternalChildren)
        {
            var box = child is FrameRegion { IsActive: true } region && !Frame.IsEmpty ? region.GetBox(SourcePixels) : null;
            child.Arrange(box is { } b
                ? new Rect(Frame.X + b.X * Frame.Width / 100, Frame.Y + b.Y * Frame.Height / 100, b.Width * Frame.Width / 100, b.Height * Frame.Height / 100)
                : new Rect());
        }
        return finalSize;
    }

    // Cùng phép co của Stretch=Uniform: giữ tỉ lệ, căn giữa ô.
    private Rect Fit(Size size)
    {
        if (Source is not { Width: > 0, Height: > 0 } source || size.Width <= 0 || size.Height <= 0) return Rect.Empty;
        var scale = Math.Min(size.Width / source.Width, size.Height / source.Height);
        var (w, h) = (source.Width * scale, source.Height * scale);
        return new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
    }
}

/// <summary>Một vùng theo % khung hình trên <see cref="RegionOverlay"/>: viền màu + nhãn, kéo để di chuyển, kéo cạnh/góc để đổi kích thước.</summary>
public abstract class FrameRegion : FrameworkElement
{
    private const double Grip = 7;
    protected const double MinPercent = 3;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(FrameRegion),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(FrameRegion),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(FrameRegion),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    private Edge _drag;
    private Point _start;
    private Rect _origin;

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Tắt thì không vẽ (khác Visibility: dùng cho điều kiện thứ hai, vd. luồng đang chọn).</summary>
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    /// <summary>Vùng hiện tại theo % (x, y, w, h đã nằm trong 0–100); null khi dữ liệu chưa hợp lệ.</summary>
    internal abstract Rect? GetBox(Size sourcePixels);

    protected abstract void SetBox(Rect box);

    protected virtual bool CanResize => true;

    protected virtual bool VerticalOnly => false;

    protected virtual void RenderContent(DrawingContext dc, Rect bounds) { }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Parent is not RegionOverlay overlay || GetBox(overlay.SourcePixels) is not { } box) return;
        _drag = HitEdge(e.GetPosition(this));
        _origin = box;
        _start = e.GetPosition(overlay);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured)
        {
            Cursor = CursorFor(HitEdge(e.GetPosition(this)));
            return;
        }
        if (Parent is not RegionOverlay overlay || overlay.Frame.IsEmpty) return;
        var p = e.GetPosition(overlay);
        var dx = VerticalOnly ? 0 : (p.X - _start.X) * 100 / overlay.Frame.Width;
        var dy = (p.Y - _start.Y) * 100 / overlay.Frame.Height;
        SetBox(Drag(_origin, _drag, dx, dy));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = Edge.None;
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }

    private static Rect Drag(Rect r, Edge edge, double dx, double dy)
    {
        if (edge == Edge.Move)
            return new Rect(Math.Clamp(r.X + dx, 0, 100 - r.Width), Math.Clamp(r.Y + dy, 0, 100 - r.Height), r.Width, r.Height);

        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        // Max/Min giữ min ≤ max cho Clamp khi vùng gõ tay nhỏ hơn MinPercent.
        if (edge.HasFlag(Edge.Left)) left = Math.Clamp(left + dx, 0, Math.Max(0, right - MinPercent));
        if (edge.HasFlag(Edge.Right)) right = Math.Clamp(right + dx, Math.Min(100, left + MinPercent), 100);
        if (edge.HasFlag(Edge.Top)) top = Math.Clamp(top + dy, 0, Math.Max(0, bottom - MinPercent));
        if (edge.HasFlag(Edge.Bottom)) bottom = Math.Clamp(bottom + dy, Math.Min(100, top + MinPercent), 100);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private Edge HitEdge(Point p)
    {
        if (!CanResize) return Edge.Move;
        // Vùng nhỏ thì thu vùng bắt cạnh lại để giữa vẫn còn chỗ kéo di chuyển.
        var g = Math.Min(Grip, Math.Min(ActualWidth, ActualHeight) / 3);
        var edge = Edge.None;
        if (p.X <= g) edge |= Edge.Left; else if (p.X >= ActualWidth - g) edge |= Edge.Right;
        if (p.Y <= g) edge |= Edge.Top; else if (p.Y >= ActualHeight - g) edge |= Edge.Bottom;
        return edge == Edge.None ? Edge.Move : edge;
    }

    private Cursor CursorFor(Edge edge) => edge switch
    {
        Edge.Left or Edge.Right => Cursors.SizeWE,
        Edge.Top or Edge.Bottom => Cursors.SizeNS,
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        _ => VerticalOnly ? Cursors.SizeNS : Cursors.SizeAll
    };

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        if (bounds.Width < 2 || bounds.Height < 2) return;
        var stroke = Stroke ?? Brushes.DeepSkyBlue;
        var color = (stroke as SolidColorBrush)?.Color ?? Colors.DeepSkyBlue;
        var hot = IsMouseOver || IsMouseCaptured;
        var fill = new SolidColorBrush(Color.FromArgb(hot ? (byte)0x44 : (byte)0x28, color.R, color.G, color.B));
        fill.Freeze();
        var pen = new Pen(stroke, hot ? 2 : 1.5);
        // Vùng chỉ để xem (không kéo được) vẽ nét đứt cho dễ phân biệt.
        if (!IsHitTestVisible) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();

        // Nội dung vẽ ngoài clip: phụ đề nhiều dòng tràn lên trên dải một dòng giống chữ thật.
        RenderContent(dc, bounds);
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawRectangle(fill, pen, new Rect(1, 1, bounds.Width - 2, bounds.Height - 2));

        if (Label is { Length: > 0 } label)
        {
            var light = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 150;
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                11, light ? Brushes.Black : Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawRectangle(stroke, null, new Rect(0, 0, text.Width + 8, text.Height + 2));
            dc.DrawText(text, new Point(4, 1));
        }

        var (w, h) = (bounds.Width, bounds.Height);
        if (hot && CanResize && IsHitTestVisible && w >= 12 && h >= 12)
            foreach (var (x, y) in new[] { (0d, 0d), (w / 2, 0), (w, 0), (0, h / 2), (w, h / 2), (0, h), (w / 2, h), (w, h) })
                dc.DrawRectangle(Brushes.White, pen, new Rect(Math.Clamp(x - 3, 0, w - 6), Math.Clamp(y - 3, 0, h - 6), 6, 6));
        dc.Pop();
    }
}

/// <summary>Vùng tự do bind với chuỗi "x,y,w,h" theo % (cùng định dạng LocalizeViewModel.ParseBox).</summary>
public sealed class BoxRegion : FrameRegion
{
    public static readonly DependencyProperty BoxProperty = DependencyProperty.Register(nameof(Box), typeof(string), typeof(BoxRegion),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Box { get => (string?)GetValue(BoxProperty); set => SetValue(BoxProperty, value); }

    internal override Rect? GetBox(Size sourcePixels) => Parse(Box);

    // SetCurrentValue giữ binding TwoWay và đẩy giá trị về ô nhập chữ.
    protected override void SetBox(Rect box) => SetCurrentValue(BoxProperty, Format(box));

    /// <summary>Đọc khoan dung để vẽ khi đang gõ dở: kẹp vào khung, sai định dạng hoặc rộng/cao ≤ 0 thì null.</summary>
    internal static Rect? Parse(string? text)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return null;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !double.IsFinite(v[i])) return null;
        var (x, y) = (Math.Clamp(v[0], 0, 100), Math.Clamp(v[1], 0, 100));
        var (w, h) = (Math.Min(v[2], 100 - x), Math.Min(v[3], 100 - y));
        return w > 0 && h > 0 ? new Rect(x, y, w, h) : null;
    }

    // Làm tròn x,y trước rồi kẹp w,h để x+w không vượt 100 vì sai số làm tròn (ParseBox sẽ báo lỗi).
    internal static string Format(Rect r)
    {
        var (x, y) = (Math.Round(r.X, 1), Math.Round(r.Y, 1));
        var (w, h) = (Math.Min(Math.Round(r.Width, 1), 100 - x), Math.Min(Math.Round(r.Height, 1), 100 - y));
        return string.Create(CultureInfo.InvariantCulture, $"{x:0.#},{y:0.#},{w:0.#},{h:0.#}");
    }
}

/// <summary>
/// Dải phụ đề: <see cref="Y"/> là % mép dưới chữ (SubtitleStyle.PositionY), <see cref="Left"/>/<see cref="Width"/> là dải chữ theo % (BoxLeft/BoxWidth),
/// cao một dòng theo <see cref="TextSize"/> (px video). Kéo cạnh trên/dưới đổi cỡ chữ, cạnh trái/phải đổi dải xuống dòng, kéo giữa để di chuyển.
/// </summary>
public sealed class SubtitleRegion : FrameRegion
{
    private const double LineFactor = 1.3;
    private Size _source;

    public static readonly DependencyProperty YProperty = DependencyProperty.Register(nameof(Y), typeof(double), typeof(SubtitleRegion),
        new FrameworkPropertyMetadata(82d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty LeftProperty = DependencyProperty.Register(nameof(Left), typeof(double), typeof(SubtitleRegion),
        new FrameworkPropertyMetadata(5d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public new static readonly DependencyProperty WidthProperty = DependencyProperty.Register(nameof(Width), typeof(double), typeof(SubtitleRegion),
        new FrameworkPropertyMetadata(90d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty TextSizeProperty = DependencyProperty.Register(nameof(TextSize), typeof(double), typeof(SubtitleRegion),
        new FrameworkPropertyMetadata(33d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontNameProperty = DependencyProperty.Register(nameof(FontName), typeof(string), typeof(SubtitleRegion),
        new FrameworkPropertyMetadata("Arial", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SubtitleRegion),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Y { get => (double)GetValue(YProperty); set => SetValue(YProperty, value); }
    public double Left { get => (double)GetValue(LeftProperty); set => SetValue(LeftProperty, value); }
    public new double Width { get => (double)GetValue(WidthProperty); set => SetValue(WidthProperty, value); }
    public double TextSize { get => (double)GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }
    public string? FontName { get => (string?)GetValue(FontNameProperty); set => SetValue(FontNameProperty, value); }
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    internal override Rect? GetBox(Size sourcePixels)
    {
        _source = sourcePixels;
        var h = sourcePixels.Height > 0 && TextSize > 0 ? Math.Clamp(TextSize * LineFactor / sourcePixels.Height * 100, MinPercent, 50) : 10;
        // AssWriter giữ tối thiểu 4% lề dưới nên chữ không xuống quá 96%.
        var bottom = Math.Clamp(double.IsFinite(Y) ? Y : 82, h, 96);
        var x = Math.Clamp(double.IsFinite(Left) ? Left : 5, 0, 95);
        var w = Math.Clamp(double.IsFinite(Width) ? Width : 90, 5, 100 - x);
        return new Rect(x, bottom - h, w, h);
    }

    protected override void SetBox(Rect box)
    {
        SetCurrentValue(YProperty, Math.Round(Math.Min(box.Bottom, 96), 1));
        SetCurrentValue(LeftProperty, Math.Round(box.X, 1));
        SetCurrentValue(WidthProperty, Math.Round(box.Width, 1));
        // Chiều cao dải = một dòng chữ nên kéo cạnh trên/dưới chính là đổi cỡ chữ (px video).
        if (_source.Height > 0) SetCurrentValue(TextSizeProperty, Math.Round(Math.Clamp(box.Height * _source.Height / 100 / LineFactor, 10, 200)));
    }

    protected override void RenderContent(DrawingContext dc, Rect bounds)
    {
        var size = bounds.Height / LineFactor;
        if (size < 4) return;
        var text = new FormattedText(string.IsNullOrWhiteSpace(Text) ? "Phụ đề mẫu" : Text.Trim(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(string.IsNullOrWhiteSpace(FontName) ? "Arial" : FontName), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = bounds.Width, TextAlignment = TextAlignment.Center
        };
        // Xuống dòng trong dải như AssWriter; mép dưới chữ bám mép dưới dải, dòng thừa tràn lên trên.
        dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, Math.Max(1, size / 16)), text.BuildGeometry(new Point(0, bounds.Height - text.Height)));
    }
}
