using System.IO;
using System.Collections.ObjectModel;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Tts;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S01;

namespace SuperMOOO.App.Features.S05;

/// <summary>Clone giọng OmniVoice: mẫu 3–30 s kèm đúng lời nói trong mẫu; danh sách giọng đã clone và xoá.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class VoiceCloneViewModel(IVoiceCloner cloner, IVoiceProfileStore profiles, IDialogService dialogs) : ObservableObject
{
    public IReadOnlyList<LanguageChoice> Languages { get; } =
        [new("auto", "Tự nhận diện"), .. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];

    public ObservableCollection<VoiceProfile> Clones { get; } = [];

    [ObservableProperty] public partial string VoiceName { get; set; } = "";
    [ObservableProperty] public partial string ReferenceAudio { get; set; } = "";
    [ObservableProperty] public partial string ReferenceText { get; set; } = "";
    [ObservableProperty] public partial LanguageChoice? Language { get; set; }
    [ObservableProperty] public partial VoiceProfile? Selected { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnrollCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; set; }

    public async Task LoadAsync()
    {
        Language ??= Languages[0];
        Clones.Clear();
        foreach (var p in await profiles.ListAsync(cloner.ProviderId)) Clones.Add(p);
    }

    [RelayCommand]
    private void BrowseAudio()
    {
        if (dialogs.OpenFile("Chọn audio mẫu (3–30 giây)", "Audio|*.wav;*.mp3;*.m4a;*.flac;*.ogg|Tất cả|*.*") is { } file) ReferenceAudio = file;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task EnrollAsync() => RunAsync(async () =>
    {
        var voice = await cloner.EnrollAsync(new VoiceCloneRequest(VoiceName, ReferenceAudio, ReferenceText, Language?.Code == "auto" ? null : Language?.Code), CancellationToken.None);
        Status = $"Đã lưu giọng \"{voice.DisplayName}\".";
        VoiceName = ReferenceText = ReferenceAudio = "";
        await LoadAsync();
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (Selected is not { } clone || !await dialogs.ConfirmAsync("Xoá giọng", $"Xoá giọng \"{clone.Name}\"?")) return;
        await cloner.DeleteAsync(clone.Id, CancellationToken.None);
        Status = "Đã xoá giọng.";
        await LoadAsync();
    });

    private bool CanRun() => !IsBusy;

    private async Task RunAsync(Func<Task> work)
    {
        IsBusy = true;
        try { await work(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or ProviderException)
        {
            Status = ex.Message;
            dialogs.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
