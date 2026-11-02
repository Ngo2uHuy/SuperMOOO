using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S17;

[Screen("S17", "Dựng video / Timeline", ScreenRegistry.Editing, 9)]
public partial class TimelineView : UserControl
{
    private readonly TimelineViewModel _vm;
    public TimelineView(TimelineViewModel vm, MediaPreviewPane preview)
    {
        InitializeComponent(); _vm = vm; DataContext = vm;
        preview.AllowExpand = true;
        preview.EmptyText = "Thêm video, chỉnh timeline rồi bấm Tạo preview.";
        PreviewHost.Content = preview;
        vm.PreviewReady += (_, path) => { preview.Open(path, true); preview.Seek(vm.Playhead); };
        vm.PreviewInvalidated += (_, _) => preview.Clear();
        vm.SeekRequested += (_, _) => preview.Seek(vm.Playhead);
        preview.TimeChanged += (_, seconds) => vm.Playhead = Math.Clamp(seconds, 0, vm.Duration);
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { e.Handled = true; await vm.AddPathsAsync(files); }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox || !vm.CanEdit) return;
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { vm.UndoCommand.Execute(null); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { vm.RedoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Delete) { vm.DeleteCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.None) { vm.SplitCommand.Execute(null); e.Handled = true; }
        };
        Unloaded += (_, _) => preview.Pause();
    }
    private void FitClick(object sender, RoutedEventArgs e) => _vm.Zoom = Math.Clamp((TimelineScroll.ViewportWidth - 190) / Math.Max(1, _vm.Project.Clips.Select(c => c.End).DefaultIfEmpty(0).Max()), 0.1, 150);
    private void TimelineScrolled(object sender, ScrollChangedEventArgs e) => TimelineCanvas.InvalidateVisual();
}
