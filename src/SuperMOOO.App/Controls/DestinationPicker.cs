using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SuperMOOO.App.Controls;

/// <summary>Một kênh/trang đích (đã đồng bộ ở S23) trong danh sách chọn.</summary>
public sealed partial class DestinationOption(DestinationDto destination) : ObservableObject
{
    public DestinationDto Destination { get; } = destination;
    public string Id => Destination.Id;

    /// <summary>Lý do kênh chưa đăng tự động được; null = dùng được.</summary>
    public string? Blocked => Destination.ConnectionApiStatus != nameof(ApiStatus.Connected)
        ? "Tài khoản chưa kết nối API hoặc cần kết nối lại ở màn Tài khoản & kênh (S23)." : Destination.AutoPublishBlockedReason;

    public string Label => $"{Labels.Term(Destination.Platform)} · {Destination.Title} ({Destination.ConnectionName})" + (Blocked is null ? "" : " – chưa đăng được");
    public string Hint => Blocked ?? Label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Chọn kênh đích dùng chung cho các màn tự xếp lịch đăng (S07, S08, S18…); danh sách lấy từ lõi đăng bài.</summary>
public sealed partial class DestinationPickerViewModel(OAuthConnectService oauth) : ObservableObject
{
    // Lựa chọn đặt trước khi danh sách được nạp (vd. nạp luồng đã lưu).
    private HashSet<string> _pending = [];

    public ObservableCollection<DestinationOption> Items { get; } = [];

    [ObservableProperty]
    public partial string? Error { get; set; }

    public List<string> SelectedIds => [.. Items.Where(i => i.IsSelected).Select(i => i.Id)];

    public string NameOf(string destinationId) =>
        Items.FirstOrDefault(i => i.Id == destinationId) is { } o ? $"{Labels.Term(o.Destination.Platform)} · {o.Destination.Title}" : destinationId;

    public async Task LoadAsync()
    {
        var keep = Items.Count > 0 ? Items.Where(i => i.IsSelected).Select(i => i.Id).ToHashSet() : _pending;
        try
        {
            var destinations = await oauth.ListDestinationsAsync(new ListDestinationsRequest(null), default);
            Items.Clear();
            foreach (var d in destinations.Where(d => d.Enabled && !d.Removed))
                Items.Add(new DestinationOption(d) { IsSelected = keep.Contains(d.Id) });
            Error = null;
        }
        catch (PublishingException ex)
        {
            Error = "Không tải được danh sách kênh: " + ex.Message;
        }
    }

    public void Select(IEnumerable<string> destinationIds)
    {
        _pending = [.. destinationIds];
        foreach (var i in Items) i.IsSelected = _pending.Contains(i.Id);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();
}

/// <summary>Danh sách chọn kênh đích; DataContext là <see cref="DestinationPickerViewModel"/>.</summary>
public sealed class DestinationPicker : UserControl
{
    public DestinationPicker()
    {
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(DestinationOption.IsSelected)));
        check.SetBinding(ContentProperty, new Binding(nameof(DestinationOption.Label)));
        check.SetBinding(ToolTipProperty, new Binding(nameof(DestinationOption.Hint)));
        check.SetValue(MarginProperty, new Thickness(0, 0, 0, 4));
        var list = new ItemsControl { ItemTemplate = new DataTemplate { VisualTree = check } };
        list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(DestinationPickerViewModel.Items)));

        var empty = new TextBlock
        {
            Text = "Chưa có kênh nào. Kết nối tài khoản và đồng bộ kênh ở màn Tài khoản & kênh (S23) rồi bấm tải lại.",
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false
        };
        empty.SetResourceReference(StyleProperty, "Hint");
        empty.SetBinding(VisibilityProperty, new Binding("Items.Count") { Converter = new EmptyToVisibilityConverter() });

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(StyleProperty, "Hint");
        error.SetBinding(TextBlock.TextProperty, new Binding(nameof(DestinationPickerViewModel.Error)));
        DockPanel.SetDock(error, Dock.Bottom);

        var refresh = new Button { Icon = new SymbolIcon(SymbolRegular.ArrowSync24), ToolTip = "Tải lại danh sách kênh", VerticalAlignment = VerticalAlignment.Top };
        refresh.SetResourceReference(StyleProperty, "IconButton");
        refresh.SetBinding(ButtonBase.CommandProperty, new Binding(nameof(DestinationPickerViewModel.RefreshCommand)));
        DockPanel.SetDock(refresh, Dock.Right);

        Content = new DockPanel { MinHeight = 52, Children = { error, refresh, new Grid { Children = { list, empty } } } };
    }
}
