using System.Reflection;
using System.Windows.Controls;
using SuperMOOO.App.Shell;
using Wpf.Ui.Controls;

namespace SuperMOOO.App.Features.S16;

public sealed record SettingsSectionItem(string Title, SymbolRegular Icon, object Content)
{
    public override string ToString() => Title;
}

[Screen("S16", "Cài đặt & hệ thống", ScreenRegistry.Release, 20)]
public partial class SettingsView : UserControl, IAsyncScreen
{
    private static readonly Dictionary<string, SymbolRegular> Icons = new()
    {
        ["appearance"] = SymbolRegular.PaintBrush24, ["paths"] = SymbolRegular.Folder24, ["apikeys"] = SymbolRegular.Key24,
        ["modelai"] = SymbolRegular.BrainCircuit24, ["worker"] = SymbolRegular.Bot24, ["ttslocal"] = SymbolRegular.PersonVoice24,
        ["platforms"] = SymbolRegular.Globe24, ["network"] = SymbolRegular.Server24, ["logs"] = SymbolRegular.DocumentText24,
        ["images"] = SymbolRegular.Image24, ["health"] = SymbolRegular.HeartPulse24, ["legacy"] = SymbolRegular.Archive24
    };

    public SettingsView(IServiceProvider services, AppAssemblies assemblies)
    {
        InitializeComponent();

        var sections = assemblies.Items
            .SelectMany(a => a.GetTypes())
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<SettingsSectionAttribute>()))
            .Where(x => x.Attr is not null)
            .OrderBy(x => x.Attr!.Order)
            .Select(x => new SettingsSectionItem(x.Attr!.Title, Icons.GetValueOrDefault(x.Attr.Id, SymbolRegular.Settings24), services.GetRequiredService(x.Type)))
            .ToList();
        Sections.ItemsSource = sections;
        Sections.SelectedIndex = 0;
    }

    public Task StartSetupAsync()
    {
        var section = Sections.Items.OfType<SettingsSectionItem>().First(s => s.Content is AiWorkerSection);
        Sections.SelectedItem = section;
        return ((AiWorkerViewModel)((AiWorkerSection)section.Content).DataContext).InstallWorkerCommand.ExecuteAsync(null);
    }

    public Task WhenLoadedAsync() =>
        Task.WhenAll(Sections.Items.OfType<SettingsSectionItem>().Select(s => s.Content).OfType<IAsyncScreen>().Select(s => s.WhenLoadedAsync()));
}
