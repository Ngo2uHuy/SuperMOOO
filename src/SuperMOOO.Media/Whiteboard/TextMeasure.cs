using System.Windows;
using System.Windows.Media;

namespace SuperMOOO.Media.Whiteboard;

/// <summary>Đo chữ thật bằng WPF (thay cho ước lượng bề rộng/cỡ chữ) để xuống dòng, co cỡ chữ và đặt hộp watermark.</summary>
public static class TextMeasure
{
    /// <summary>Bề rộng và chiều cao dòng (px) của text ở cỡ em px.</summary>
    public static (double Width, double Height) Measure(string text, string font, double px, bool bold)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), Math.Max(1, px), Brushes.Black, 1.0);
        return (formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    /// <summary>libass lấy Fontsize là chiều cao ô chữ (ascent + descent), canvas lấy là em: nhân hệ số này để chữ ASS ra đúng cỡ em.</summary>
    public static double CellRatio(string font)
    {
        try { return Math.Clamp(new FontFamily(font).LineSpacing, 1, 2); }
        catch (ArgumentException) { return 1.2; }
    }
}
