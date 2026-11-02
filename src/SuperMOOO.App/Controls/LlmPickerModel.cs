using System.Collections.ObjectModel;
using SuperMOOO.AI.Llm;

namespace SuperMOOO.App.Controls;

public sealed partial class LlmPickerModel : ObservableObject
{
    private readonly LlmProviderRegistry _llm;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;

    public LlmPickerModel(LlmProviderRegistry llm, ISettingsStore settings, IDialogService dialogs)
    {
        (_llm, _settings, _dialogs) = (llm, settings, dialogs);
        ProviderId = settings.Get<AiModelsSettings>("ai.models").TextProvider;
    }

    public IReadOnlyList<LlmProviderSpec> Providers => LlmProviderSpec.All;
    public ObservableCollection<string> Models { get; } = [];
    [ObservableProperty] public partial string ProviderId { get; set; } = "";
    [ObservableProperty] public partial string Model { get; set; } = "";

    partial void OnProviderIdChanged(string value)
    {
        var configured = _settings.Get<AiModelsSettings>("ai.models");
        var model = value == configured.TextProvider && !string.IsNullOrWhiteSpace(configured.TextModel)
            ? configured.TextModel : Providers.FirstOrDefault(p => p.Id == value)?.DefaultModel ?? "";
        Models.Clear();
        if (model.Length > 0) Models.Add(model);
        Model = model;
    }

    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        var provider = ProviderId;
        try
        {
            var models = await _llm.Get(provider).ListModelsAsync(CancellationToken.None);
            if (ProviderId != provider) return;
            var selected = Model;
            Models.Clear();
            foreach (var model in models.Append(selected).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().Order()) Models.Add(model);
            Model = selected;
            if (models.Count == 0) _dialogs.Info("Model AI", "Nhà cung cấp chưa trả về model nào. Bạn có thể nhập tên model.");
        }
        catch (Exception ex)
        {
            _dialogs.Error("Không tải được danh sách model: " + ex.Message);
        }
    }
}
