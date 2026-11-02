using System.Collections.ObjectModel;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;
using SuperMOOO.AI.Tts;

namespace SuperMOOO.App.Features.S16;

[Service(ServiceLifetime.Transient)]
public sealed partial class ModelAiViewModel : ObservableObject
{
    private readonly ModelProfileService _profiles;
    private readonly LlmProviderRegistry _llm;
    private readonly ISettingsStore _settings;

    public ModelAiViewModel(ModelProfileService profiles, ModelCatalog catalog, LlmProviderRegistry llm, ISettingsStore settings)
    {
        _profiles = profiles;
        _llm = llm;
        _settings = settings;

        TextPresets = [.. catalog.For("text")];
        AsrPresets = [.. catalog.For("asr")];
        TtsPresets = [.. catalog.For("tts")];

        var s = profiles.Get();
        TextProvider = s.TextProvider;
        TextModel = s.TextModel;
        LocalLlmUrl = s.LocalLlmUrl;
        OpenAiCompatUrl = s.OpenAiCompatUrl;
        OpenAiTtsUrl = s.OpenAiTtsUrl;
        AsrBackend = s.AsrBackend;
        AsrModel = s.AsrModel;
        Translator = s.Translator;
        TtsProvider = s.TtsProvider;
        TtsVoice = s.TtsVoice;
        TtsModel = s.TtsModel;

        var edge = settings.Get<TtsEdgeSettings>("tts.edge");
        EdgeEnabled = edge.Enabled;
    }

    public IReadOnlyList<string> TextProviders { get; } = LlmProviderSpec.All.Select(p => p.Id).ToList();
    public IReadOnlyList<string> AsrBackends { get; } = ["local.whisper", "groq", "openai", "gemini"];
    public IReadOnlyList<string> Translators { get; } = ["auto", "llm", "google-free", "local-mt"];
    public IReadOnlyList<string> TtsProviders => VoiceIds.AllProviders;
    public IReadOnlyList<ModelPreset> TextPresets { get; }
    public IReadOnlyList<ModelPreset> AsrPresets { get; }
    public IReadOnlyList<ModelPreset> TtsPresets { get; }
    public ObservableCollection<string> Models { get; } = [];
    public IReadOnlyList<string> AsrModelOptions => AsrPresets.Where(p => p.Provider == AsrBackend).Select(p => p.Model).Distinct().ToList();
    public IReadOnlyList<string> TtsModelOptions => TtsPresets.Where(p => p.Provider == TtsProvider && !p.Provider.StartsWith("native.", StringComparison.Ordinal))
        .Select(p => p.Model).Distinct().ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalProvider), nameof(IsCompatProvider))]
    public partial string TextProvider { get; set; } = "";

    public bool IsLocalProvider => TextProvider == LlmProviderSpec.Local.Id;

    public bool IsCompatProvider => TextProvider == LlmProviderSpec.OpenAiCompat.Id;

    [ObservableProperty]
    public partial string OpenAiCompatUrl { get; set; } = "";

    [ObservableProperty]
    public partial string OpenAiTtsUrl { get; set; } = "";

    [ObservableProperty]
    public partial string LocalLlmUrl { get; set; } = "";

    [ObservableProperty]
    public partial string TextModel { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AsrModelOptions))]
    public partial string AsrBackend { get; set; } = "";

    [ObservableProperty]
    public partial string AsrModel { get; set; } = "";

    [ObservableProperty]
    public partial string Translator { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TtsModelOptions))]
    public partial string TtsProvider { get; set; } = "";

    [ObservableProperty]
    public partial string TtsVoice { get; set; } = "";

    [ObservableProperty]
    public partial string TtsModel { get; set; } = "";

    [ObservableProperty]
    public partial bool EdgeEnabled { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckKeyCommand), nameof(LoadModelsCommand))]
    public partial bool IsBusy { get; set; }

    // Model của nhà cung cấp cũ (vd. gemini-…) gửi sang nhà cung cấp mới sẽ bị từ chối; constructor/preset gán model ngay sau nên không bị đè.
    partial void OnTextProviderChanged(string value)
    {
        if (LlmProviderSpec.All.FirstOrDefault(p => p.Id == value) is not { } spec) return;
        TextModel = spec.DefaultModel;
        Models.Clear();
    }

    [RelayCommand]
    private void ApplyTextPreset(ModelPreset? preset)
    {
        if (preset is null) return;
        TextProvider = preset.Provider;
        TextModel = preset.Model;
    }

    [RelayCommand]
    private void ApplyAsrPreset(ModelPreset? preset)
    {
        if (preset is null) return;
        AsrBackend = preset.Provider;
        AsrModel = preset.Model;
    }

    [RelayCommand]
    private void ApplyTtsPreset(ModelPreset? preset)
    {
        if (preset is null) return;
        TtsProvider = preset.Provider;
        TtsModel = preset.Provider.StartsWith("native.", StringComparison.Ordinal) ? "" : preset.Model;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (!Uri.TryCreate(LocalLlmUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                throw new ArgumentException("URL máy chủ cục bộ phải bắt đầu bằng http:// hoặc https://.");
            foreach (var custom in new[] { OpenAiCompatUrl, OpenAiTtsUrl })
                if (custom.Trim().Length > 0 && (!Uri.TryCreate(custom.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
                    throw new ArgumentException("Base URL OpenAI phải bắt đầu bằng http:// hoặc https://.");
            if (IsCompatProvider && OpenAiCompatUrl.Trim().Length == 0) throw new ArgumentException("Nhập base URL cho OpenAI-compatible.");
            await _profiles.SaveAsync(new AiModelsSettings
            {
                TextProvider = TextProvider, TextModel = TextModel.Trim(), LocalLlmUrl = LocalLlmUrl.Trim(), OpenAiCompatUrl = OpenAiCompatUrl.Trim(), OpenAiTtsUrl = OpenAiTtsUrl.Trim(), AsrBackend = AsrBackend, AsrModel = AsrModel.Trim(),
                Translator = Translator, TtsProvider = TtsProvider, TtsVoice = TtsVoice.Trim(), TtsModel = TtsModel.Trim()
            });
            var edge = _settings.Get<TtsEdgeSettings>("tts.edge");
            edge.Enabled = EdgeEnabled;
            await _settings.SetAsync("tts.edge", edge);
            Status = "Đã lưu.";
        }
        catch (ArgumentException e)
        {
            Status = e.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckKeyAsync()
    {
        IsBusy = true;
        try { Status = (await _llm.Get(TextProvider).CheckAsync(CancellationToken.None)).Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task LoadModelsAsync()
    {
        IsBusy = true;
        try
        {
            var models = await _llm.Get(TextProvider).ListModelsAsync(CancellationToken.None);
            Models.Clear();
            foreach (var m in models.Order()) Models.Add(m);
            Status = $"Đã tải {models.Count} model.";
        }
        catch (ProviderException e)
        {
            Status = e.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanRun() => !IsBusy;
}
