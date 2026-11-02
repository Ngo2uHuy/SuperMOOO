using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SuperMOOO.Core.Operations;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SuperMOOO.App.Controls;

/// <summary>Form sinh từ [Field] của một request type; dùng cho mọi operation thay vì viết XAML riêng từng màn.</summary>
public sealed class DynamicOperationForm : UserControl
{
    private readonly StackPanel _panel = new();
    private readonly TextBlock _general = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };

    public DynamicOperationForm()
    {
        _general.SetResourceReference(StyleProperty, "ErrorText");
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Margin = new Thickness(0, 0, 12, 0), Children = { _panel, _general } }
        };
    }

    public FormModel? Model { get; private set; }

    /// <param name="platform">Nền tảng cố định cho field chuyên mục khi request không có field Platform (suy từ operation).</param>
    public void Bind(Type requestType, object? initial = null, string? platform = null)
    {
        Model = new FormModel(requestType, initial);
        _panel.Children.Clear();
        _general.Visibility = Visibility.Collapsed;
        var platformField = Model.Fields.FirstOrDefault(f => f.Meta.Name == "Platform");
        foreach (var field in Model.Fields)
        {
            var row = BuildRow(field);
            _panel.Children.Add(row);
            if (field.Meta.Kind != FieldKind.Category || FindPicker(row) is not { } chips) continue;
            if (platformField is null) chips.SetPlatform(platform);
            else
            {
                chips.SetPlatform(platformField.Text);
                platformField.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FormFieldViewModel.Text)) chips.SetPlatform(platformField.Text);
                };
            }
        }

        if (Model.Fields.Count == 0)
            _panel.Children.Add(new TextBlock { Text = "Tác vụ này không cần thiết lập thêm.", Style = (Style)FindResource("Hint") });
    }

    public bool TryGetRequest(out object? request)
    {
        request = null;
        if (Model is null) return false;

        var ok = Model.TryBuild(out request);
        _general.Text = Model.GeneralError;
        _general.Visibility = Model.GeneralError is null ? Visibility.Collapsed : Visibility.Visible;
        return ok;
    }

    private static FrameworkElement BuildRow(FormFieldViewModel field)
    {
        var row = new Field
        {
            Header = field.Meta.Kind == FieldKind.Bool ? null : field.Meta.Required ? field.Label + " *" : field.Label,
            Hint = string.IsNullOrWhiteSpace(field.Help) ? null : field.Help,
            Content = BuildInput(field),
            Margin = new Thickness(0)
        };

        var error = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        error.SetResourceReference(StyleProperty, "ErrorText");
        error.SetBinding(TextBlock.TextProperty, new Binding(nameof(FormFieldViewModel.Error)) { Source = field });
        error.SetBinding(VisibilityProperty, new Binding(nameof(FormFieldViewModel.Error)) { Source = field, Converter = new EmptyToVisibilityConverter { Invert = true } });
        return new StackPanel { Margin = new Thickness(0, 0, 0, 16), Children = { row, error } };
    }

    private static CategoryPicker? FindPicker(FrameworkElement row) =>
        row is StackPanel { Children: [Field { Content: CategoryPicker chips }, ..] } ? chips : null;

    private static FrameworkElement BuildInput(FormFieldViewModel field)
    {
        if (field.Meta.Name is "Country" or "CountryCode" || field.Meta.Name == "Platform" && field.Meta.Kind != FieldKind.Choice)
        {
            var options = field.Meta.Name == "Platform" ? InputChoices.Platforms : InputChoices.Countries;
            var picker = new ValuePicker { Options = field.Meta.Required ? options.Where(x => x.Value.Length > 0) : options };
            picker.SetBinding(ValuePicker.ValueProperty, TwoWay(field, nameof(FormFieldViewModel.Text)));
            return picker;
        }
        switch (field.Meta.Kind)
        {
            case FieldKind.Bool:
                var check = new CheckBox { Content = field.Label };
                check.SetBinding(ToggleButton.IsCheckedProperty, TwoWay(field, nameof(FormFieldViewModel.Flag)));
                return check;
            case FieldKind.Choice:
                var combo = new ComboBox
                {
                    ItemsSource = field.Meta.Choices.Select((value, i) => new ChoiceItem(value,
                        field.Meta.ChoiceLabels is { } labels && i < labels.Count ? labels[i] : value)).ToArray(),
                    DisplayMemberPath = nameof(ChoiceItem.Label), SelectedValuePath = nameof(ChoiceItem.Value),
                    HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = false
                };
                combo.SetBinding(Selector.SelectedValueProperty, TwoWay(field, nameof(FormFieldViewModel.Text)));
                return combo;
            case FieldKind.Category:
                return new CategoryPicker(field);
            case FieldKind.Secret:
                var secret = new System.Windows.Controls.PasswordBox();
                secret.PasswordChanged += (_, _) => field.Text = secret.Password;
                return secret;
            case FieldKind.File or FieldKind.Folder:
                var path = BuildTextBox(field, multiline: false);
                FileDrop.SetIsEnabled(path, true);
                path.PlaceholderText = field.Meta.Kind == FieldKind.Folder ? "Chọn hoặc kéo-thả thư mục vào đây" : "Chọn hoặc kéo-thả file vào đây";
                var browse = new Button
                {
                    Icon = new SymbolIcon(field.Meta.Kind == FieldKind.Folder ? SymbolRegular.FolderOpen24 : SymbolRegular.DocumentSearch24),
                    ToolTip = field.Meta.Kind == FieldKind.Folder ? "Chọn thư mục…" : "Chọn file…"
                };
                browse.SetResourceReference(StyleProperty, "IconButton");
                browse.Click += (_, _) => Browse(field);
                var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
                grid.Children.Add(path);
                Grid.SetColumn(browse, 1);
                grid.Children.Add(browse);
                return grid;
            default:
                return BuildTextBox(field, field.Meta.Kind is FieldKind.MultiLine or FieldKind.UrlList);
        }
    }

    private sealed record ChoiceItem(string Value, string Label);

    private static TextBox BuildTextBox(FormFieldViewModel field, bool multiline)
    {
        var box = new TextBox { PlaceholderText = string.IsNullOrWhiteSpace(field.Meta.Example) ? $"Nhập {field.Label}…" : field.Meta.Example };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 88;
            box.VerticalContentAlignment = VerticalAlignment.Top;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        box.SetBinding(System.Windows.Controls.TextBox.TextProperty, TwoWay(field, nameof(FormFieldViewModel.Text)));
        return box;
    }

    private static void Browse(FormFieldViewModel field)
    {
        if (field.Meta.Kind == FieldKind.Folder)
        {
            var folder = new OpenFolderDialog { Title = field.Label };
            if (folder.ShowDialog() == true) field.Text = folder.FolderName;
            return;
        }

        var file = new OpenFileDialog { Title = field.Label };
        if (file.ShowDialog() == true) field.Text = file.FileName;
    }

    private static Binding TwoWay(FormFieldViewModel field, string path) =>
        new(path) { Source = field, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
}
