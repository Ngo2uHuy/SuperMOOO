using System.Windows;
using System.Windows.Controls;

namespace SuperMOOO.App.Features.S02;

/// <summary>Danh sách + trình sửa câu phụ đề; DataContext là <see cref="SubtitleEditorViewModel"/> (dùng ở S02 và nhúng vào S06).</summary>
public partial class SubtitleListPanel : UserControl
{
    public SubtitleListPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SubtitleEditorViewModel vm) TranslationColumn.Visibility = vm.ShowTranslation ? Visibility.Visible : Visibility.Collapsed;
        };
    }
}
