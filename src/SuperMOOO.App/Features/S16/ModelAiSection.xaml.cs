using System.Windows.Controls;
using SuperMOOO.AI.Catalog;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("modelai", "Model AI", 3)]
public partial class ModelAiSection : UserControl
{
    public ModelAiSection(ModelAiViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private ModelAiViewModel ViewModel => (ModelAiViewModel)DataContext;

    private void OnTextPreset(object sender, SelectionChangedEventArgs e) => ViewModel.ApplyTextPresetCommand.Execute(((ComboBox)sender).SelectedItem as ModelPreset);

    private void OnAsrPreset(object sender, SelectionChangedEventArgs e) => ViewModel.ApplyAsrPresetCommand.Execute(((ComboBox)sender).SelectedItem as ModelPreset);

    private void OnTtsPreset(object sender, SelectionChangedEventArgs e) => ViewModel.ApplyTtsPresetCommand.Execute(((ComboBox)sender).SelectedItem as ModelPreset);
}
