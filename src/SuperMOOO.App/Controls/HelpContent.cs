using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.RegularExpressions;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SuperMOOO.App.Controls;

/// <summary>Dựng nội dung hướng dẫn từ văn bản thuần: dòng đầu là tiêu đề, dòng kết thúc bằng ":" là mục, "1. …" / "- …" thụt lề treo.</summary>
public static partial class HelpContent
{
    public static UIElement Build(string text)
    {
        var panel = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        var first = true;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            if (first)
            {
                panel.Children.Add(Block(line, 22, FontWeights.SemiBold, "TextFillColorPrimaryBrush", new Thickness(0, 0, 0, 4)));
                first = false;
            }
            else if (line.EndsWith(':') && !ListItem().IsMatch(line))
                panel.Children.Add(Block(line, 15, FontWeights.SemiBold, "TextFillColorPrimaryBrush", new Thickness(0, 14, 0, 4)));
            else if (ListItem().Match(line) is { Success: true } item)
                panel.Children.Add(Item(item.Groups[1].Value, item.Groups[2].Value));
            else
                panel.Children.Add(Block(line, 13, FontWeights.Normal, "TextFillColorSecondaryBrush", new Thickness(0, 2, 0, 2)));
        }

        return new ScrollViewer { MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    private static UIElement Item(string marker, string body)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = Block(marker == "-" ? "•" : marker, 13, FontWeights.SemiBold, "TextFillColorSecondaryBrush", new Thickness(0));
        mark.HorizontalAlignment = HorizontalAlignment.Right;
        mark.Margin = new Thickness(0, 0, 8, 0);
        var content = Block(body, 13, FontWeights.Normal, "TextFillColorSecondaryBrush", new Thickness(0));
        Grid.SetColumn(content, 1);
        grid.Children.Add(mark);
        grid.Children.Add(content);
        return grid;
    }

    private static TextBlock Block(string text, double size, FontWeight weight, string brushKey, Thickness margin)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    [GeneratedRegex(@"^\s*(\d+\.|-)\s+(.*)$")]
    private static partial Regex ListItem();
}
