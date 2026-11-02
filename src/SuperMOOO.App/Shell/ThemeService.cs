using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SuperMOOO.App.Shell;

/// <summary>Giao diện Sáng / Tối / Theo hệ thống (lưu ở settings "ui"); màu nhấn cố định theo nhận diện app, nền trang/thẻ đặc để thẻ tách khỏi nền.</summary>
[Service]
public sealed class ThemeService(ISettingsStore settings)
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    /// <summary>Màu nhấn cố định: màu nhấn Windows khác nhau mỗi máy (có máy là vàng/hồng) làm giao diện thiếu đồng nhất.</summary>
    private static readonly Color Brand = Color.FromRgb(0x1E, 0x6F, 0xD9);

    private Window? _window;
    private bool _watching;

    // ThemeService là singleton; đăng ký một lần để mọi lần đổi theme (kể cả theo hệ thống) đều áp lại bảng màu.
    static ThemeService() => ApplicationThemeManager.Changed += (theme, _) => ApplyPalette(theme);

    public string Mode => settings.Get<UiSettings>("ui").Theme is var mode && mode is Light or Dark ? mode : System;

    /// <summary>Mica nếu Windows hỗ trợ, không thì Acrylic, cuối cùng là nền đặc.</summary>
    public static WindowBackdropType Backdrop { get; } =
        WindowBackdrop.IsSupported(WindowBackdropType.Mica) ? WindowBackdropType.Mica
        : WindowBackdrop.IsSupported(WindowBackdropType.Acrylic) ? WindowBackdropType.Acrylic
        : WindowBackdropType.None;

    /// <summary>Gắn cửa sổ chính (để đổi nền và theo dõi theme hệ thống) rồi áp chế độ đã lưu.</summary>
    public void Attach(Window window)
    {
        _window = window;
        Apply(Mode);
    }

    public async Task SetModeAsync(string mode)
    {
        await settings.SetAsync("ui", new UiSettings { Theme = mode });
        Apply(mode);
    }

    /// <summary>Ép Sáng/Tối tạm thời, không lưu (chụp ảnh màn hình).</summary>
    public void Preview(ApplicationTheme theme)
    {
        Unwatch();
        ApplicationThemeManager.Apply(theme, Backdrop);
    }

    private void Apply(string mode)
    {
        if (mode == System)
        {
            ApplicationThemeManager.ApplySystemTheme();
            if (_window is not null && !_watching)
            {
                SystemThemeWatcher.Watch(_window, Backdrop);
                _watching = true;
            }

            return;
        }

        Unwatch();
        ApplicationThemeManager.Apply(mode == Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, Backdrop);
    }

    private void Unwatch()
    {
        if (!_watching || _window is not { IsLoaded: true }) return;
        SystemThemeWatcher.UnWatch(_window);
        _watching = false;
    }

    /// <summary>Ghi đè vài brush của WPF-UI ở cấp Application (thắng bộ theme đã merge): nền trang xám nhạt, thẻ trắng viền mảnh như các app quản lý.</summary>
    private static void ApplyPalette(ApplicationTheme theme)
    {
        var resources = Application.Current.Resources;
        // Thanh trạng thái giữ màu nhận diện ở mọi theme (màu nhấn bản Tối quá sáng cho một dải nền).
        resources["StatusBarBackground"] = Solid(0x1E6FD9);
        string[] keys =
        [
            "NavigationViewContentBackground", "NavigationViewContentGridBorderBrush", "CardBackground", "CardBackgroundFillColorDefaultBrush",
            "CardBorderBrush", "CardStrokeColorDefaultBrush", "ExpanderHeaderBackground", "ExpanderContentBackground", "ExpanderHeaderBorderBrush",
            "NavigationViewItemBackgroundSelected"
        ];
        if (theme == ApplicationTheme.HighContrast)
        {
            foreach (var key in keys) resources.Remove(key);
            return;
        }

        ApplicationAccentColorManager.Apply(Brand, theme);
        var dark = theme == ApplicationTheme.Dark;
        var page = Solid(dark ? 0x1B1C1E : 0xF3F5F8);
        var card = Solid(dark ? 0x26282B : 0xFFFFFF);
        var stroke = Solid(dark ? 0x35383C : 0xE2E6EC);
        var selected = Solid(dark ? 0x26364D : 0xE4EDFB);
        Brush[] values = [page, stroke, card, card, stroke, stroke, card, card, stroke, selected];
        for (var i = 0; i < keys.Length; i++) resources[keys[i]] = values[i];
    }

    private static SolidColorBrush Solid(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
