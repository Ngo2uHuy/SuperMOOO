using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SuperMOOO.App.Features.S11;

public readonly record struct ChartPoint(double X, double Y, DateTimeOffset At, double Value);

/// <summary>Quy đổi chuỗi chỉ số sang toạ độ vẽ; tách khỏi WPF để test được.</summary>
public static class MetricChartLayout
{
    public const double PadLeft = 64, PadRight = 16, PadTop = 14, PadBottom = 30;

    /// <summary>Mẫu cần xếp tăng dần theo thời gian. Chỉ có một điểm hoặc giá trị phẳng thì vẽ ở giữa trục.</summary>
    public static IReadOnlyList<ChartPoint> Compute(IReadOnlyList<MetricSample> samples, double width, double height)
    {
        if (samples.Count == 0 || width <= PadLeft + PadRight || height <= PadTop + PadBottom) return [];

        var plotW = width - PadLeft - PadRight;
        var plotH = height - PadTop - PadBottom;
        var t0 = samples[0].RecordedAt.ToUnixTimeMilliseconds();
        var t1 = samples[^1].RecordedAt.ToUnixTimeMilliseconds();
        var min = samples.Min(s => s.Value);
        var max = samples.Max(s => s.Value);

        return samples.Select(s =>
        {
            var x = t1 == t0 ? PadLeft + plotW / 2 : PadLeft + (s.RecordedAt.ToUnixTimeMilliseconds() - t0) / (double)(t1 - t0) * plotW;
            var y = max == min ? PadTop + plotH / 2 : PadTop + (1 - (s.Value - min) / (max - min)) * plotH;
            return new ChartPoint(x, y, s.RecordedAt, s.Value);
        }).ToList();
    }
}

/// <summary>Biểu đồ đường của một chỉ số theo thời gian (DrawingVisual, không dùng thư viện vẽ ngoài).</summary>
public sealed class MetricChart : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    private IReadOnlyList<MetricSample> _samples = [];

    public MetricChart()
    {
        AddVisualChild(_visual);
        SizeChanged += (_, _) => Redraw();
        MinHeight = 160;
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;

    public void SetSamples(IEnumerable<MetricSample> samples)
    {
        _samples = samples.OrderBy(s => s.RecordedAt).ToList();
        Redraw();
    }

    private Brush Themed(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void Redraw()
    {
        using var dc = _visual.RenderOpen();
        var text = Themed("TextFillColorPrimaryBrush", Brushes.Gray);
        var muted = Themed("TextFillColorSecondaryBrush", Brushes.Gray);
        var accent = Themed("AccentTextFillColorPrimaryBrush", Brushes.SteelBlue);
        var axis = new Pen(muted, 1);
        var w = ActualWidth;
        var h = ActualHeight;

        var points = MetricChartLayout.Compute(_samples, w, h);
        if (points.Count == 0)
        {
            dc.DrawText(Label("Chưa có dữ liệu chỉ số.", muted, 13), new Point(MetricChartLayout.PadLeft, MetricChartLayout.PadTop));
            return;
        }

        var left = MetricChartLayout.PadLeft;
        var bottom = h - MetricChartLayout.PadBottom;
        dc.DrawLine(axis, new Point(left, MetricChartLayout.PadTop), new Point(left, bottom));
        dc.DrawLine(axis, new Point(left, bottom), new Point(w - MetricChartLayout.PadRight, bottom));

        var min = _samples.Min(s => s.Value);
        var max = _samples.Max(s => s.Value);
        dc.DrawText(Label(Format(max), muted, 11), new Point(4, MetricChartLayout.PadTop - 6));
        dc.DrawText(Label(Format(min), muted, 11), new Point(4, bottom - 8));
        dc.DrawText(Label(points[0].At.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture), muted, 11), new Point(left, bottom + 6));
        if (points.Count > 1)
        {
            var last = Label(points[^1].At.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture), muted, 11);
            dc.DrawText(last, new Point(w - MetricChartLayout.PadRight - last.Width, bottom + 6));
        }

        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, points[0].Y), false, false);
                ctx.PolyLineTo(points.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), true, true);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(accent, 2), geometry);
        }

        foreach (var p in points) dc.DrawEllipse(accent, null, new Point(p.X, p.Y), 3.5, 3.5);
        dc.DrawText(Label(Format(points[^1].Value), text, 12), new Point(points[^1].X - 20, points[^1].Y - 20));
    }

    private FormattedText Label(string value, Brush brush, double size) =>
        new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Format(double value) => Math.Abs(value) >= 1000 ? value.ToString("N0", CultureInfo.CurrentCulture) : value.ToString("0.##", CultureInfo.CurrentCulture);
}
