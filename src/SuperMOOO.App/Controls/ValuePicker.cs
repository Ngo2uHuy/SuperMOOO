using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace SuperMOOO.App.Controls;

/// <summary>Chọn tên dễ đọc nhưng binding/lưu mã; giữ được giá trị cũ ngoài danh mục.</summary>
public sealed class ValuePicker : ComboBox
{
    private readonly ObservableCollection<InputOption> _options = [];
    private bool _updating;
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(nameof(Options), typeof(IEnumerable<InputOption>), typeof(ValuePicker),
        new PropertyMetadata(null, (d, _) => ((ValuePicker)d).SyncOptions()));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(ValuePicker),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ValuePicker)d).SyncSelection()));
    public IEnumerable<InputOption>? Options { get => (IEnumerable<InputOption>?)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    public string? Value { get => (string?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public ValuePicker()
    {
        SetResourceReference(StyleProperty, typeof(ComboBox));
        ItemsSource = _options;
        DisplayMemberPath = nameof(InputOption.Display);
        SelectedValuePath = nameof(InputOption.Value);
        IsTextSearchEnabled = true;
        ComboSearch.SetIsEnabled(this, true);
    }

    private void SyncOptions()
    {
        _updating = true;
        try
        {
            _options.Clear();
            foreach (var option in Options ?? []) _options.Add(option);
        }
        finally { _updating = false; }
        SyncSelection();
    }

    private void SyncSelection()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var value = Value ?? "";
            var selected = _options.FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));
            if (selected is null && value.Length > 0)
            {
                selected = new InputOption(value, $"{value} (đã lưu)");
                _options.Add(selected);
            }
            SetCurrentValue(SelectedItemProperty, selected);
        }
        finally { _updating = false; }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (!_updating && SelectedItem is InputOption option) SetCurrentValue(ValueProperty, option.Value);
    }
}
