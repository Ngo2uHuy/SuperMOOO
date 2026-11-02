using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SuperMOOO.Social.Crawlers;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SuperMOOO.App.Controls;

/// <summary>Dropdown chuyên mục của nền tảng (có ô tìm qua ComboSearch); giá trị lưu là khoá chuyên mục.</summary>
public sealed class CategoryPicker : StackPanel
{
    private readonly FormFieldViewModel _field;
    private readonly ComboBox _combo = new()
    {
        DisplayMemberPath = nameof(CategoryOption.Label), SelectedValuePath = nameof(CategoryOption.Key), HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _note = new() { Text = "Nền tảng này không có chuyên mục để chọn.", Visibility = Visibility.Collapsed };

    public CategoryPicker(FormFieldViewModel field)
    {
        _field = field;
        _note.SetResourceReference(StyleProperty, "Hint");
        _combo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(FormFieldViewModel.Text)) { Source = field, Mode = BindingMode.TwoWay });
        Children.Add(_combo);
        Children.Add(_note);
    }

    /// <summary>Nạp chuyên mục theo nền tảng; giá trị đang chọn không còn thì về "all".</summary>
    public void SetPlatform(string? platform)
    {
        var options = PlatformCategories.For(platform);
        _combo.Visibility = options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _note.Visibility = options.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (options.All(o => o.Key != _field.Text)) _field.Text = PlatformCategories.All.Key;
        _combo.ItemsSource = options;
        _combo.SelectedValue = _field.Text;
    }
}
