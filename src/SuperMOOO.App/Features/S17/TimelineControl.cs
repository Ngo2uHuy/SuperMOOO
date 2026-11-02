using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SuperMOOO.App.Features.S17;

/// <summary>Vẽ các track theo giây; chỉ commit một lần khi kết thúc kéo để undo nguyên thao tác.</summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double Header = 110;
    private const double Ruler = 32;
    private const double Row = 48;
    private static readonly string[] Labels = ["Video", "Tiếng gốc", "Voiceover", "Nhạc nền", "Phụ đề"];
    private static readonly Brush[] Colors = [Brushes.SteelBlue, Brushes.Teal, Brushes.DarkCyan, Brushes.DarkGoldenrod, Brushes.SlateBlue];
    private TimelineViewModel? _vm;
    private readonly List<(TimelineClip Clip, Rect Rect)> _hits = [];
    private TimelineClip? _drag;
    private Point _origin;
    private Point _pointer;
    private int _edge;
    // Đầu phát là visual riêng, chỉ dịch ngang khi Playhead đổi nên không phải vẽ lại cả timeline mỗi khung hình.
    private readonly DrawingVisual _playhead = new();
    private readonly TranslateTransform _playheadMove = new();

    public TimelineControl()
    {
        using (var dc = _playhead.RenderOpen())
            dc.DrawLine(new Pen(Brushes.OrangeRed, 2), new Point(0, 0), new Point(0, Ruler + Row * Labels.Length));
        _playhead.Transform = _playheadMove;
        AddVisualChild(_playhead);
        Focusable = true;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => { Detach(); if (IsLoaded) Attach(); };
        ToolTip = "Kéo thân clip để di chuyển; kéo hai mép để trim. Video tự nối liên tiếp. Bấm thước để tua. Ctrl + con lăn để zoom.";
    }
    private void Attach()
    {
        if (_vm is not null || DataContext is not TimelineViewModel vm) return;
        _vm = vm; vm.PropertyChanged += Changed; Resize();
    }
    private void Detach()
    {
        if (_vm is not null) _vm.PropertyChanged -= Changed;
        _vm = null;
        MovePlayhead();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimelineViewModel.Project) or nameof(TimelineViewModel.Zoom) or nameof(TimelineViewModel.Duration)) Resize();
        if (e.PropertyName == nameof(TimelineViewModel.Playhead)) MovePlayhead();
        if (e.PropertyName is nameof(TimelineViewModel.SelectedClip) or nameof(TimelineViewModel.CanEdit)) InvalidateVisual();
    }
    private void MovePlayhead() => _playheadMove.X = _vm is { } vm ? Header + vm.Playhead * vm.Zoom : -10;
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _playhead : throw new ArgumentOutOfRangeException(nameof(index));
    private void Resize()
    {
        Width = Header + Math.Max(20, _vm?.Project.Clips.Select(c => c.End).DefaultIfEmpty(0).Max() ?? 0) * (_vm?.Zoom ?? 35) + 80;
        Height = Ruler + Row * Labels.Length;
        MovePlayhead();
        InvalidateVisual();
    }
    private Brush ResourceBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    private void Text(DrawingContext dc, string value, double x, double y, Brush brush, double maxWidth = 0)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
            12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0) { text.MaxTextWidth = maxWidth; text.MaxTextHeight = 26; text.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(text, new Point(x, y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var foreground = ResourceBrush("TextFillColorPrimaryBrush", Brushes.White);
        var line = new Pen(ResourceBrush("CardStrokeColorDefaultBrush", Brushes.Gray), 1);
        dc.DrawRectangle(ResourceBrush("CardBackgroundFillColorDefaultBrush", Brushes.Black), null, new Rect(0, 0, ActualWidth, ActualHeight));
        _hits.Clear();
        if (_vm is not { } vm) return;
        for (var row = 0; row < Labels.Length; row++)
        {
            var y = Ruler + row * Row;
            dc.DrawLine(line, new Point(0, y), new Point(ActualWidth, y));
        }
        var step = vm.Zoom < 10 ? 30 : vm.Zoom < 25 ? 10 : vm.Zoom < 70 ? 5 : 1;
        var visible = GetVisibleRange();
        for (var seconds = Math.Max(0, Math.Floor((visible.Left - Header) / vm.Zoom / step) * step); Header + seconds * vm.Zoom < visible.Right; seconds += step)
        {
            var x = Header + seconds * vm.Zoom;
            dc.DrawLine(line, new Point(x, Ruler - 8), new Point(x, ActualHeight));
            Text(dc, seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss"), x + 3, 5, foreground);
        }
        foreach (var clip in vm.Clips)
        {
            var row = clip.Track switch { TimelineTrack.Video => 0, TimelineTrack.Voiceover => 2, TimelineTrack.Music => 3, _ => 4 };
            DrawClip(clip, row, false);
            if (clip.Track == TimelineTrack.Video) DrawClip(clip, 1, true);
        }
        // Cột tên track vẽ sau clip, bám mép trái viewport để cuộn ngang vẫn biết đang ở track nào.
        dc.DrawRectangle(ResourceBrush("CardBackgroundFillColorSecondaryBrush", Brushes.DimGray), line, new Rect(visible.Left, Ruler, Header, Row * Labels.Length));
        for (var row = 0; row < Labels.Length; row++)
            Text(dc, Labels[row], visible.Left + 8, Ruler + row * Row + 15, foreground, Header - 12);
        if (_drag is not null)
        {
            dc.DrawLine(new Pen(Brushes.Gold, 2), new Point(_pointer.X, 0), new Point(_pointer.X, ActualHeight));
            Text(dc, $"{(_pointer.X - _origin.X) / vm.Zoom:+0.00;-0.00;0}s", _pointer.X + 5, 15, foreground);
        }

        void DrawClip(TimelineClip clip, int row, bool sourceAudio)
        {
            var x = Header + clip.Start * vm.Zoom;
            var width = Math.Max(3, clip.Duration * vm.Zoom - 2);
            if (x + width < visible.Left || x > visible.Right) return;
            var rect = new Rect(x, Ruler + row * Row + 5, width, Row - 10);
            dc.PushOpacity(clip.Muted ? 0.4 : 1);
            dc.DrawRoundedRectangle(Colors[row], new Pen(vm.SelectedClip?.Id == clip.Id ? Brushes.Gold : Colors[row], vm.SelectedClip?.Id == clip.Id ? 2 : 1), rect, 4, 4);
            if (width > 24)
            {
                var label = sourceAudio ? (clip.Muted ? "Đã tắt tiếng" : $"Tiếng gốc · {clip.Volume:0.##}×") :
                    clip.Track == TimelineTrack.Subtitle ? clip.Text : System.IO.Path.GetFileName(clip.Path);
                // Chỉ layout phần chữ trong viewport, kể cả clip rất dài.
                var textX = Math.Max(rect.X + 7, visible.Left + 7);
                var textWidth = Math.Min(rect.Right, visible.Right) - textX - 7;
                if (textWidth > 10) Text(dc, label, textX, rect.Y + 9, Brushes.White, textWidth);
            }
            dc.Pop();
            _hits.Add((clip, rect));
        }
    }
    private Rect GetVisibleRange()
    {
        // ScrollViewer chỉ yêu cầu một vùng nhỏ của timeline; tránh hàng trăm nghìn nhãn khi zoom lớn.
        var parent = VisualTreeHelper.GetParent(this);
        while (parent is not null && parent is not System.Windows.Controls.ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        return parent is System.Windows.Controls.ScrollViewer scroll
            ? new Rect(scroll.HorizontalOffset, 0, Math.Max(1, scroll.ViewportWidth), Height)
            : new Rect(0, 0, Math.Min(ActualWidth, 2000), Height);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_vm is not { CanEdit: true } vm) return;
        Focus(); _origin = _pointer = e.GetPosition(this);
        if (_origin.Y > Ruler && _origin.X < GetVisibleRange().Left + Header) return;      // bấm vào cột tên track, không phải clip bị che bên dưới
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(_origin));
        if (hit.Clip is null) { vm.Seek((_origin.X - Header) / vm.Zoom); return; }
        vm.SelectedClip = hit.Clip;
        _drag = hit.Clip;
        _edge = _origin.X - hit.Rect.Left < 7 ? -1 : hit.Rect.Right - _origin.X < 7 ? 1 : 0;
        CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer = e.GetPosition(this);
        if (_drag is not null) InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is not { } clip || _vm is not { } vm) return;
        var delta = (e.GetPosition(this).X - _origin.X) / vm.Zoom;
        _drag = null; ReleaseMouseCapture();
        if (Math.Abs(delta * vm.Zoom) > 3) vm.DragClip(clip.Id, delta, _edge);
        InvalidateVisual(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _drag = null; InvalidateVisual(); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_vm is { } vm && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { vm.Zoom = Math.Clamp(vm.Zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.1, 150); e.Handled = true; }
        base.OnMouseWheel(e);
    }
}
