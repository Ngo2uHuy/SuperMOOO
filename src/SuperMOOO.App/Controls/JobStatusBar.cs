using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SuperMOOO.App.Controls;

/// <summary>
/// Thanh tiến độ ở vị trí cố định: vòng xoay khi chạy, bước hiện tại, %, nút Huỷ.
/// Bind theo tên Status/Progress/IsRunning/CancelCommand của DataContext (JobViewModelBase, OperationRunnerViewModel, MediaToolsViewModel).
/// </summary>
public sealed class JobStatusBar : UserControl
{
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(JobStatusBar),
        new PropertyMetadata(100d));

    public JobStatusBar()
    {
        var visible = (BooleanToVisibilityConverter)Application.Current.FindResource("BoolToVis");

        var ring = new ProgressRing { IsIndeterminate = true, Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        ring.SetBinding(VisibilityProperty, new Binding("IsRunning") { Converter = visible, FallbackValue = Visibility.Collapsed });

        var status = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        status.SetBinding(TextBlock.TextProperty, new Binding("Status") { TargetNullValue = "Sẵn sàng", FallbackValue = "Sẵn sàng" });
        status.SetBinding(ToolTipProperty, new Binding("Status"));

        var bar = new ProgressBar { Margin = new Thickness(0, 8, 0, 0) };
        bar.SetBinding(RangeBase.ValueProperty, new Binding("Progress") { Mode = BindingMode.OneWay, FallbackValue = 0d });
        bar.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(Maximum)) { Source = this });

        var percent = new TextBlock { MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        percent.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        percent.SetBinding(TextBlock.TextProperty, new Binding("Progress") { Converter = new PercentConverter(this), FallbackValue = "" });
        percent.SetBinding(VisibilityProperty, new Binding("IsRunning") { Converter = visible, FallbackValue = Visibility.Collapsed });

        var cancel = new Button { Content = "Huỷ", Icon = new SymbolIcon(SymbolRegular.Dismiss24), Margin = new Thickness(12, 0, 0, 0), ToolTip = "Dừng tác vụ đang chạy" };
        cancel.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding("CancelCommand"));
        cancel.SetBinding(VisibilityProperty, new Binding("IsRunning") { Converter = visible, FallbackValue = Visibility.Collapsed });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { status, bar } };
        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } }
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(percent, 2);
        Grid.SetColumn(cancel, 3);
        grid.Children.Add(ring);
        grid.Children.Add(text);
        grid.Children.Add(percent);
        grid.Children.Add(cancel);

        var card = new Border { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(1), Child = grid, MinHeight = 64 };
        card.SetResourceReference(Border.CornerRadiusProperty, "Radius");
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Content = card;

        // Chỉ JobViewModelBase có ShowStatusBar; bind theo tên với VM khác sẽ sinh lỗi binding trong --trace-binding.
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is JobViewModelBase) SetBinding(VisibilityProperty, new Binding(nameof(JobViewModelBase.ShowStatusBar)) { Converter = visible });
            else ClearValue(VisibilityProperty);
        };
    }

    /// <summary>Thang của Progress: 100 (đa số VM) hoặc 1 (OperationRunnerViewModel).</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    private sealed class PercentConverter(JobStatusBar owner) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is double v && owner.Maximum > 0 ? $"{Math.Clamp(v / owner.Maximum, 0, 1) * 100:0}%" : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
