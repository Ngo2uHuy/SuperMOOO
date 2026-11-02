using System.Windows;
using System.Windows.Input;

namespace SuperMOOO.App.Controls;

public static class DialogKeys
{
    /// <summary>Esc đóng cửa sổ phụ; dùng KeyDown (không phải Preview) để Esc đóng danh sách xổ của ComboBox trước.</summary>
    public static void CloseOnEscape(Window window) => window.KeyDown += (_, e) =>
    {
        if (e.Handled || e.Key != Key.Escape) return;
        e.Handled = true;
        window.Close();
    };
}
