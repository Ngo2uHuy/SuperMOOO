using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SuperMOOO.App.Controls;

/// <summary>Bảng kết quả dùng chung: lọc, xuất XLSX/CSV, lưu JSON, dùng dòng đã chọn, đưa vào tải; cột ảnh hiện thumbnail, dòng video xem trước được.</summary>
[Service(ServiceLifetime.Transient)]
public sealed class ResultGrid : UserControl
{
    private static readonly string[] ImageColumns = ["thumbnail", "thumbnail_url", "thumb", "image", "image_url", "avatar", "cover", "picture"];

    private readonly System.Windows.Controls.DataGrid _grid = new()
    {
        IsReadOnly = true,
        AutoGenerateColumns = false,
        SelectionMode = DataGridSelectionMode.Extended,
        CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column
    };

    private readonly ThumbnailLoader _thumbnails;
    private readonly IServiceProvider _services;

    public ResultGrid(ResultGridViewModel viewModel, ThumbnailLoader thumbnails, IServiceProvider services)
    {
        DataContext = ViewModel = viewModel;
        _thumbnails = thumbnails;
        _services = services;

        var filter = new TextBox
        {
            Width = 240, PlaceholderText = "Lọc theo nội dung mọi cột…", Icon = new SymbolIcon(SymbolRegular.Filter24),
            ClearButtonEnabled = true, Margin = new Thickness(0, 0, 8, 8)
        };
        filter.SetBinding(System.Windows.Controls.TextBox.TextProperty,
            new Binding(nameof(ResultGridViewModel.FilterText)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        var bar = new WrapPanel();
        bar.Children.Add(filter);
        bar.Children.Add(Tool("Xuất bảng", SymbolRegular.ArrowExportLtr24, nameof(ResultGridViewModel.ExportTableCommand), "Xuất các dòng đang hiện ra Excel (.xlsx) hoặc CSV"));
        bar.Children.Add(Tool("Lưu JSON", SymbolRegular.Save24, nameof(ResultGridViewModel.SaveResultCommand), "Lưu kết quả đang hiện ra file JSON"));
        bar.Children.Add(Tool("Dùng dòng đã chọn", SymbolRegular.ArrowEnterLeft24, nameof(ResultGridViewModel.UseSelectedCommand), "Nạp các dòng đã chọn vào form"));
        bar.Children.Add(Tool("Đưa vào tải", SymbolRegular.ArrowDownload24, nameof(ResultGridViewModel.SendToDownloadCommand), "Xếp mỗi dòng có liên kết thành một job tải (S09)"));

        var status = new TextBlock { Margin = new Thickness(0, 8, 0, 0) };
        status.SetResourceReference(StyleProperty, "Hint");
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(ResultGridViewModel.Status)));

        var empty = new EmptyState { Icon = SymbolRegular.Table24, Text = "Chưa có kết quả. Chạy tác vụ để xem dữ liệu ở đây." };
        empty.SetBinding(VisibilityProperty, new Binding("Visible.Count") { Converter = new EmptyToVisibilityConverter() });

        _grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ResultGridViewModel.Visible)));
        _grid.SelectionChanged += (_, _) => viewModel.Selected = _grid.SelectedItems.OfType<ResultRow>().ToList();
        _grid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_grid, source) is DataGridRow { Item: ResultRow row })
                Dispatcher.BeginInvoke(() => ShowPreview(row));
        };
        viewModel.TableChanged += (_, _) => RebuildColumns();

        var body = new Grid { Children = { _grid, empty } };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(status);
        root.Children.Add(body);
        Content = root;
    }

    public ResultGridViewModel ViewModel { get; }

    private void RebuildColumns()
    {
        _grid.Columns.Clear();
        if (ViewModel.Visible.Any(r => ViewModel.PreviewUrl(r) is not null)) _grid.Columns.Add(PreviewColumn());

        // Cột ảnh đứng đầu để nhìn là nhận ra video, dù trong dữ liệu (và file xuất) nó nằm cuối.
        var first = ViewModel.Visible.FirstOrDefault();
        var images = Enumerable.Range(0, ViewModel.Columns.Count).Where(i => IsImage(ViewModel.Columns[i], first?[i])).ToList();
        foreach (var i in images) _grid.Columns.Add(ImageColumn(ViewModel.Columns[i], i));
        for (var i = 0; i < ViewModel.Columns.Count; i++)
            if (!images.Contains(i))
                _grid.Columns.Add(new DataGridTextColumn { Header = ViewModel.Columns[i], Binding = new Binding($"[{i}]"), MaxWidth = 420 });
    }

    private void ShowPreview(ResultRow row)
    {
        if (ViewModel.PreviewUrl(row) is not { } url) return;
        var window = _services.GetRequiredService<VideoPreviewWindow>();
        window.SendToDownload = () => ViewModel.SendRowToDownloadAsync(row);
        window.Preview(Window.GetWindow(this), url, ViewModel.TitleOf(row));
    }

    private DataGridTemplateColumn PreviewColumn()
    {
        var icon = new FrameworkElementFactory(typeof(SymbolIcon));
        icon.SetValue(SymbolIcon.SymbolProperty, SymbolRegular.PlayCircle24);
        icon.SetValue(FontSizeProperty, 24d);
        icon.SetValue(MarginProperty, new Thickness(8, 0, 8, 0));
        icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(CursorProperty, System.Windows.Input.Cursors.Hand);
        icon.SetValue(ToolTipProperty, "Xem trước video (hoặc nhấp đúp vào dòng)");
        icon.SetResourceReference(ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        icon.SetBinding(VisibilityProperty, new Binding(".") { Converter = new PreviewVisibilityConverter(ViewModel) });
        // Không chặn sự kiện và mở sau: DataGrid cần nhận MouseUp để nhả chuột đã capture khi chọn dòng, nếu không cửa sổ modal không nhận được click.
        icon.AddHandler(MouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { DataContext: ResultRow row }) Dispatcher.BeginInvoke(() => ShowPreview(row));
        }));
        return new DataGridTemplateColumn { Header = "", CanUserSort = false, CellTemplate = new DataTemplate { VisualTree = icon } };
    }

    private static bool IsImage(string column, string? sample) =>
        ImageColumns.Contains(column, StringComparer.OrdinalIgnoreCase)
        || sample is not null && Uri.TryCreate(sample, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
           && (uri.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

    private DataGridTemplateColumn ImageColumn(string header, int index)
    {
        var image = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
        image.SetValue(HeightProperty, 56d);
        image.SetValue(MaxWidthProperty, 100d);
        image.SetValue(MarginProperty, new Thickness(4, 2, 4, 2));
        // Binding trên DataContext lấy nguồn từ DataContext của ô (dòng), nên ảnh nhận Thumbnail riêng rồi Source bám vào nó khi tải xong.
        image.SetBinding(DataContextProperty, new Binding($"[{index}]") { Converter = new ThumbnailConverter(_thumbnails) });
        image.SetBinding(System.Windows.Controls.Image.SourceProperty, new Binding(nameof(Thumbnail.Image)));
        return new DataGridTemplateColumn { Header = header, CellTemplate = new DataTemplate { VisualTree = image } };
    }

    private static Button Tool(string text, SymbolRegular icon, string command, string tip)
    {
        var button = new Button { Content = text, Icon = new SymbolIcon(icon), ToolTip = tip, Margin = new Thickness(0, 0, 8, 8) };
        button.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(command));
        return button;
    }

    /// <summary>Ảnh tải nền: ô hiện trống ngay, có ảnh thì báo binding cập nhật; lỗi thì giữ trống.</summary>
    private sealed class Thumbnail : ObservableObject
    {
        public Thumbnail(Task<ImageSource?> load) => _ = CompleteAsync(load);

        public ImageSource? Image { get; private set; }

        private async Task CompleteAsync(Task<ImageSource?> load)
        {
            Image = await load;
            OnPropertyChanged(nameof(Image));
        }
    }

    /// <summary>Ô rỗng/không phải URL ảnh thì để trống thay vì lỗi binding.</summary>
    private sealed class ThumbnailConverter(ThumbnailLoader loader) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string { Length: > 0 } text ? new Thumbnail(loader.LoadAsync(text)) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class PreviewVisibilityConverter(ResultGridViewModel viewModel) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is ResultRow row && viewModel.PreviewUrl(row) is not null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
