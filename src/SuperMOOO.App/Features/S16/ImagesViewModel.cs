using SuperMOOO.AI.Image;

namespace SuperMOOO.App.Features.S16;

[Service(ServiceLifetime.Transient)]
public sealed partial class ImagesViewModel : ObservableObject
{
    private readonly ImageProviderRegistry _registry;
    private readonly ISettingsStore _settings;

    public ImagesViewModel(ImageProviderRegistry registry, ISettingsStore settings)
    {
        _registry = registry;
        _settings = settings;
        Providers = registry.All.Select(p => p.Id).ToList();
        var s = settings.Get<AiImagesSettings>("ai.images");
        Provider = s.Provider;
        Model = s.Model;
        SdWebUiUrl = s.SdWebUiUrl;
        IntervalSeconds = s.PollinationsIntervalSeconds;
        OpenAiUrl = s.OpenAiUrl;
        GeminiUrl = s.GeminiUrl;
    }

    public IReadOnlyList<string> Providers { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSdWebUi), nameof(IsPollinations), nameof(ModelHint))]
    public partial string Provider { get; set; } = "";

    [ObservableProperty]
    public partial string Model { get; set; } = "";

    [ObservableProperty]
    public partial string SdWebUiUrl { get; set; } = "";

    [ObservableProperty]
    public partial string OpenAiUrl { get; set; } = "";

    [ObservableProperty]
    public partial string GeminiUrl { get; set; } = "";

    [ObservableProperty]
    public partial int IntervalSeconds { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsSdWebUi => Provider == "sdwebui";
    public bool IsPollinations => Provider == "pollinations";

    public string ModelHint => _registry.All.FirstOrDefault(p => p.Id == Provider) is { DefaultModel.Length: > 0 } p
        ? $"Để trống = {p.DefaultModel}" : "Để trống = checkpoint đang nạp trong WebUI";

    // Model của provider cũ gửi sang provider mới sẽ bị từ chối.
    partial void OnProviderChanged(string value) => Model = "";

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!await TrySaveAsync()) return;
        Status = "Đã lưu.";
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (!await TrySaveAsync()) return;
        IsBusy = true;
        try { Status = (await _registry.Get(Provider).CheckAsync(CancellationToken.None)).Message; }
        finally { IsBusy = false; }
    }

    private bool CanCheck() => !IsBusy;

    private async Task<bool> TrySaveAsync()
    {
        if (IsSdWebUi && (!Uri.TryCreate(SdWebUiUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")))
        {
            Status = "URL SD WebUI phải bắt đầu bằng http:// hoặc https://.";
            return false;
        }

        foreach (var custom in new[] { OpenAiUrl, GeminiUrl })
            if (custom.Trim().Length > 0 && (!Uri.TryCreate(custom.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
            {
                Status = "Base URL OpenAI/Gemini phải bắt đầu bằng http:// hoặc https://.";
                return false;
            }

        await _settings.SetAsync("ai.images", new AiImagesSettings
        {
            Provider = Provider, Model = Model.Trim(), SdWebUiUrl = SdWebUiUrl.Trim(), OpenAiUrl = OpenAiUrl.Trim(), GeminiUrl = GeminiUrl.Trim(),
            PollinationsIntervalSeconds = Math.Clamp(IntervalSeconds, 1, 300)
        });
        return true;
    }
}
