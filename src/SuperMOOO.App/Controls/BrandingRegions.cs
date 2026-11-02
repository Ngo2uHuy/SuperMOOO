using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SuperMOOO.Media.Whiteboard;

namespace SuperMOOO.App.Controls;

/// <summary>
/// Vùng đặt watermark/logo: toạ độ tự do X/Y (% góc trên trái của hộp, NaN = chưa kéo, dùng mã góc Position như video). Chỉ di chuyển được;
/// kích thước do cỡ chữ/cỡ logo ở form quyết định nên hộp trên khung xem trước đúng bằng hộp trong video.
/// </summary>
public abstract class PlacedRegion : FrameRegion
{
    public static readonly DependencyProperty XProperty = DependencyProperty.Register(nameof(X), typeof(double), typeof(PlacedRegion),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty YProperty = DependencyProperty.Register(nameof(Y), typeof(double), typeof(PlacedRegion),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(string), typeof(PlacedRegion),
        new FrameworkPropertyMetadata("TL", FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty ContentOpacityProperty = DependencyProperty.Register(nameof(ContentOpacity), typeof(double), typeof(PlacedRegion),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double X { get => (double)GetValue(XProperty); set => SetValue(XProperty, value); }
    public double Y { get => (double)GetValue(YProperty); set => SetValue(YProperty, value); }
    public string? Position { get => (string?)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double ContentOpacity { get => (double)GetValue(ContentOpacityProperty); set => SetValue(ContentOpacityProperty, value); }

    protected override bool CanResize => false;

    protected override void SetBox(Rect box)
    {
        SetCurrentValue(XProperty, Math.Round(box.X, 1));
        SetCurrentValue(YProperty, Math.Round(box.Y, 1));
    }

    /// <summary>Hộp (%) cho kích thước pixel video (w, h): toạ độ tự do nếu có, ngược lại góc theo Position (cùng lề với LogoOverlay/BrandingFilters).</summary>
    protected Rect Place(Size source, double w, double h)
    {
        var (wp, hp) = (w / source.Width * 100, h / source.Height * 100);
        if (double.IsFinite(X) && double.IsFinite(Y)) return new Rect(Math.Clamp(X, 0, Math.Max(0, 100 - wp)), Math.Clamp(Y, 0, Math.Max(0, 100 - hp)), wp, hp);

        var m = Math.Max(12, Math.Round(Math.Min(source.Width, source.Height) * 0.025));
        var (px, py) = (Position ?? "TL").ToUpperInvariant() switch
        {
            "TL" => (m, m),
            "BL" => (m, source.Height - h - m),
            "BR" => (source.Width - w - m, source.Height - h - m),
            "C" or "CENTER" => ((source.Width - w) / 2, (source.Height - h) / 2),
            _ => (source.Width - w - m, m)
        };
        return new Rect(Math.Max(0, px / source.Width * 100), Math.Max(0, py / source.Height * 100), wp, hp);
    }

    protected static System.Windows.Media.Color ParseColor(string? text, System.Windows.Media.Color fallback)
    {
        try { return text is { Length: > 0 } ? (System.Windows.Media.Color)ColorConverter.ConvertFromString(text) : fallback; }
        catch (FormatException) { return fallback; }
    }
}

/// <summary>Watermark chữ: cỡ em = TextSize x cạnh ngắn / 1080, đo bằng cùng TextMeasure với renderer.</summary>
public sealed class WatermarkRegion : PlacedRegion
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(WatermarkRegion),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontNameProperty = DependencyProperty.Register(nameof(FontName), typeof(string), typeof(WatermarkRegion),
        new FrameworkPropertyMetadata("Segoe UI", FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextSizeProperty = DependencyProperty.Register(nameof(TextSize), typeof(double), typeof(WatermarkRegion),
        new FrameworkPropertyMetadata(40d, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextColorProperty = DependencyProperty.Register(nameof(TextColor), typeof(string), typeof(WatermarkRegion),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutlineColorProperty = DependencyProperty.Register(nameof(OutlineColor), typeof(string), typeof(WatermarkRegion),
        new FrameworkPropertyMetadata("#000000", FrameworkPropertyMetadataOptions.AffectsRender));

    private Size _source;

    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? FontName { get => (string?)GetValue(FontNameProperty); set => SetValue(FontNameProperty, value); }
    public double TextSize { get => (double)GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }
    public string? TextColor { get => (string?)GetValue(TextColorProperty); set => SetValue(TextColorProperty, value); }
    public string? OutlineColor { get => (string?)GetValue(OutlineColorProperty); set => SetValue(OutlineColorProperty, value); }

    private double Em(Size source) => Math.Max(8, (double.IsFinite(TextSize) ? TextSize : 40) * Math.Min(source.Width, source.Height) / 1080);

    internal override Rect? GetBox(Size sourcePixels)
    {
        _source = sourcePixels;
        if (sourcePixels.Width <= 0 || sourcePixels.Height <= 0 || string.IsNullOrWhiteSpace(Text)) return null;
        var (w, h) = TextMeasure.Measure(Text.Trim(), FontName ?? "Segoe UI", Em(sourcePixels), true);
        return Place(sourcePixels, w, h);
    }

    protected override void RenderContent(DrawingContext dc, Rect bounds)
    {
        if (_source.Height <= 0 || string.IsNullOrWhiteSpace(Text)) return;
        var em = Em(_source);
        var (_, h) = TextMeasure.Measure(Text.Trim(), FontName ?? "Segoe UI", em, true);
        var size = em * bounds.Height / h;
        if (size < 3) return;
        var text = new FormattedText(Text.Trim(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(FontName ?? "Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size,
            new SolidColorBrush(ParseColor(TextColor, Colors.White)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var pen = new Pen(new SolidColorBrush(ParseColor(OutlineColor, Colors.Black)), Math.Max(1, size / 7)) { LineJoin = PenLineJoin.Round };
        dc.PushOpacity(Math.Clamp(ContentOpacity, 0, 1));
        dc.DrawGeometry(new SolidColorBrush(ParseColor(TextColor, Colors.White)), pen, text.BuildGeometry(new Point(0, 0)));
        dc.Pop();
    }
}

/// <summary>Logo ảnh: rộng SizePercent % khung (như LogoOverlay), cao theo tỉ lệ ảnh.</summary>
public sealed class LogoRegion : PlacedRegion
{
    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(nameof(ImagePath), typeof(string), typeof(LogoRegion),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((LogoRegion)d).Reload()));
    public static readonly DependencyProperty SizePercentProperty = DependencyProperty.Register(nameof(SizePercent), typeof(double), typeof(LogoRegion),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    private BitmapImage? _bitmap;

    public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
    public double SizePercent { get => (double)GetValue(SizePercentProperty); set => SetValue(SizePercentProperty, value); }

    private void Reload()
    {
        _bitmap = null;
        if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath)) return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(ImagePath);
            image.EndInit();
            image.Freeze();
            _bitmap = image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or UriFormatException)
        {
            // logo hỏng thì không vẽ hộp xem trước; video vẫn bỏ qua logo như cũ
        }
    }

    internal override Rect? GetBox(Size sourcePixels)
    {
        if (_bitmap is not { PixelHeight: > 0 } bitmap || sourcePixels.Width <= 0 || sourcePixels.Height <= 0) return null;
        var width = sourcePixels.Width * Math.Clamp(double.IsFinite(SizePercent) ? SizePercent : 12, 5, 50) / 100;
        return Place(sourcePixels, width, width * bitmap.PixelHeight / bitmap.PixelWidth);
    }

    protected override void RenderContent(DrawingContext dc, Rect bounds)
    {
        if (_bitmap is null) return;
        dc.PushOpacity(Math.Clamp(ContentOpacity, 0, 1));
        dc.DrawImage(_bitmap, bounds);
        dc.Pop();
    }
}
