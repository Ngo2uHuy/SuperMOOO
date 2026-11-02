using System.Windows;
using System.Windows.Controls;

namespace SuperMOOO.App.Controls;

/// <summary>`c:FileDrop.IsEnabled="True"` trên TextBox: kéo-thả file/thư mục vào thì điền đường dẫn đầu tiên.</summary>
public static class FileDrop
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(FileDrop),
        new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    public static string[]? Paths(DragEventArgs e) => e.Data.GetData(DataFormats.FileDrop) as string[];

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not true) return;
        box.AllowDrop = true;
        // TextBox tự xử lý kéo-thả chữ nên phải chặn ở Preview để nhận file.
        box.PreviewDragOver += (_, args) =>
        {
            args.Effects = args.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        };
        box.PreviewDrop += (_, args) =>
        {
            if (Paths(args) is not [var first, ..]) return;
            box.SetCurrentValue(TextBox.TextProperty, first);
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            args.Handled = true;
        };
    }
}
