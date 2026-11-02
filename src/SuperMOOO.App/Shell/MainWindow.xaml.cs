using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using SuperMOOO.App.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace SuperMOOO.App.Shell;

public partial class MainWindow : FluentWindow
{
    private readonly Dictionary<string, NavigationViewItem> _items = [];
    private readonly ScreenRegistry _screens;
    private readonly List<NavigationViewItemHeader> _headers = [];
    private bool? _wide;

    public MainWindow(MainViewModel viewModel, CommandPalette palette, ScreenRegistry screens, ThemeService theme,
        ISnackbarService snackbar, IContentDialogService contentDialogs)
    {
        InitializeComponent();
        DataContext = viewModel;
        _screens = screens;
        WindowBackdropType = ThemeService.Backdrop;
        snackbar.SetSnackbarPresenter(Snackbar);
        contentDialogs.SetDialogHost(DialogHost);
        theme.Attach(this);
        BuildNavigation(screens);

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Help, (_, _) => viewModel.ShowHelpCommand.Execute(null)));
        // Khung tác vụ của từng màn dùng GoToPage + mã màn (vd. "S15") để mở màn khác.
        CommandBindings.Add(new CommandBinding(NavigationCommands.GoToPage, (_, e) => { if (e.Parameter is string id) Open(id); }));
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusSearch), Key.K, ModifierKeys.Control));

        Search.TextChanged += (box, e) =>
        {
            e.Handled = true;   // tự lọc theo màn + tác vụ thay cho bộ lọc chuỗi mặc định
            if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) box.ItemsSource = palette.Search(e.Text);
        };
        // Danh sách còn mở nghĩa là đang di chuyển bằng phím; chỉ mở màn khi đã chốt (click/Enter).
        Search.SuggestionChosen += (box, e) =>
        {
            if (!box.IsSuggestionListOpen && e.SelectedItem is PaletteItem item) Open(item.ScreenId);
        };
        Search.QuerySubmitted += (_, e) =>
        {
            if (palette.Search(e.QueryText).FirstOrDefault() is { } item) Open(item.ScreenId);
        };

        viewModel.PropertyChanged += OnViewModelChanged;
        SizeChanged += (_, e) => AdaptPane(e.NewSize.Width);
        // Thanh thu gọn chỉ còn icon: ẩn tiêu đề nhóm để không bị cắt chữ.
        Nav.PaneOpened += (_, _) => ShowHeaders(true);
        Nav.PaneClosed += (_, _) => ShowHeaders(false);
        Loaded += (_, _) => ShowCurrent();
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    private void BuildNavigation(ScreenRegistry screens)
    {
        Nav.MenuItems.Clear();
        Nav.FooterMenuItems.Clear();
        _items.Clear();
        _headers.Clear();
        string[] footer = ["S15", "S16"];
        var frequent = ViewModel.FrequentScreens;
        var groups = new List<(string Title, IEnumerable<ScreenDescriptor> Screens)>();
        if (frequent.Count > 0) groups.Add(("HAY DÙNG", frequent));
        groups.AddRange(screens.All.Where(s => !footer.Contains(s.Id) && !frequent.Any(f => f.Id == s.Id))
            .GroupBy(s => s.Category).Select(g => (g.Key, (IEnumerable<ScreenDescriptor>)g)));
        foreach (var group in groups)
        {
            // Nhãn nhóm chữ hoa nhỏ, màu nhạt để tách hẳn khỏi mục bấm được.
            var header = new NavigationViewItemHeader
            {
                Text = group.Title.ToUpper(CultureInfo.CurrentCulture), Margin = new Thickness(8, 16, 0, 4), FontSize = 11, FontWeight = FontWeights.SemiBold
            };
            header.SetResourceReference(ForegroundProperty, "TextFillColorTertiaryBrush");
            System.Windows.Automation.AutomationProperties.SetName(header, SentenceCase(group.Title));
            _headers.Add(header);
            Nav.MenuItems.Add(header);
            foreach (var screen in group.Screens) Nav.MenuItems.Add(Item(screen));
        }

        foreach (var screen in screens.All.Where(s => footer.Contains(s.Id))) Nav.FooterMenuItems.Add(Item(screen));
        ShowHeaders(Nav.IsPaneOpen);
    }

    private static string SentenceCase(string text) => text[..1] + text[1..].ToLower(CultureInfo.CurrentCulture);

    private NavigationViewItem Item(ScreenDescriptor screen)
    {
        var item = new NavigationViewItem
        {
            Content = screen.Title,
            Icon = new SymbolIcon(screen.Icon),
            ToolTip = $"{screen.Id} · {screen.Description}"
        };
        item.Click += (_, _) => ViewModel.Select(screen);
        _items[screen.Id] = item;
        return item;
    }

    private void Open(string screenId)
    {
        if (_screens.Find(screenId) is { } screen) ViewModel.Select(screen);
        Search.Text = "";
    }

    private void FocusSearch()
    {
        Nav.IsPaneOpen = true;
        // Ô tìm chỉ hiện sau khi thanh điều hướng mở xong layout.
        Dispatcher.BeginInvoke(() => Search.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Cửa sổ hẹp thì thu gọn thanh điều hướng để vùng làm việc giữ được bố cục 2 cột; chỉ đổi khi vượt ngưỡng nên không đè thao tác bật/tắt tay.</summary>
    private void AdaptPane(double width)
    {
        var wide = width >= 1200;
        if (wide == _wide) return;
        _wide = wide;
        Nav.IsPaneOpen = wide;
        ShowHeaders(wide);
    }

    private void ShowHeaders(bool visible)
    {
        foreach (var header in _headers) header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FrequentScreens))
            Dispatcher.BeginInvoke(() =>
            {
                BuildNavigation(_screens);
                foreach (var (id, item) in _items)
                    if (id == ViewModel.Selected?.Id) item.Activate(Nav);
            }, System.Windows.Threading.DispatcherPriority.Background);
        if (e.PropertyName == nameof(MainViewModel.CurrentView) && IsLoaded) ShowCurrent();
    }

    private void ShowCurrent()
    {
        if (ViewModel.CurrentView is not FrameworkElement view) return;
        // Frame của NavigationView chặn kế thừa thuộc tính, nên đặt màu chữ theo theme cho từng màn.
        view.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        Nav.ReplaceContent(view);
        foreach (var (id, item) in _items)
        {
            if (id == ViewModel.Selected?.Id) item.Activate(Nav);
            else item.Deactivate(Nav);
        }
    }
}
