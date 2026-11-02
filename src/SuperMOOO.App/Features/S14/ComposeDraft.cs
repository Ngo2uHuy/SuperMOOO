using System.IO;
using SuperMOOO.Publishing.Library;

namespace SuperMOOO.App.Features.S14;

public sealed class ComposeEditorDraft
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "";
    public List<ContentOption> Media { get; set; } = [];
    public List<string> Targets { get; set; } = [];
    public List<CaptionDraft> Captions { get; set; } = [];
    public List<TargetDraft> TargetOptions { get; set; } = [];
    public string RepeatMode { get; set; } = "None";
    public string RepeatCount { get; set; } = "2";
    public bool StripMetadata { get; set; }
    public bool AutoCrop { get; set; }
    public bool AddWatermark { get; set; }
    public string LogoPath { get; set; } = "";
    public string Mode { get; set; } = "Fixed";
    public DateTime? Date { get; set; }
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "21:00";
    public string MinGap { get; set; } = "15";
    public string MaxGap { get; set; } = "40";
    public bool Shuffle { get; set; }
    public bool IsAiContent { get; set; } = true;
    public bool UseNativeSchedule { get; set; }
}

public sealed record CaptionDraft(string Platform, string Title, string Description, string Tags);
public sealed record TargetDraft(string Id, string Title, string Description, string Tags, string Privacy, bool Kids, bool Ai);

public sealed partial class ComposeViewModel
{
    private bool _draftRestored;
    private string? _lastDraft;
    [ObservableProperty] public partial string DraftStatus { get; set; } = "";

    private void RestoreEditorDraft()
    {
        if (_draftRestored) return;
        _draftRestored = true;
        foreach (var caption in PlatformCaptions) caption.PropertyChanged += (_, _) => InvalidatePreview();
        if (TookHandoff) return;
        var draft = settings.Get<ComposeEditorDraft>("publishing.composeDraft");
        Title = draft.Title;
        Description = draft.Description;
        TagsText = draft.Tags;
        foreach (var media in draft.Media.DistinctBy(m => m.Id)) SelectedMedia.Add(media);
        foreach (var target in Targets) target.IsSelected = draft.Targets.Contains(target.Id);
        foreach (var target in Targets)
            if (draft.TargetOptions.FirstOrDefault(t => t.Id == target.Id) is { } savedTarget)
            {
                (target.Title, target.Description, target.TagsText, target.Privacy) = (savedTarget.Title, savedTarget.Description, savedTarget.Tags, savedTarget.Privacy);
                target.MadeForKids = savedTarget.Kids;
                target.ContainsSyntheticMedia = savedTarget.Ai;
            }
        foreach (var caption in PlatformCaptions)
            if (draft.Captions.FirstOrDefault(c => c.Platform == caption.Platform) is { } saved)
                (caption.Title, caption.Description, caption.TagsText) = (saved.Title, saved.Description, saved.Tags);
        ScheduleMode = draft.Mode;
        ScheduleDate = draft.Date ?? DateTime.Today;
        ScheduleTime = draft.Start;
        ScheduleEnd = draft.End;
        MinGap = draft.MinGap;
        MaxGap = draft.MaxGap;
        ShuffleTags = draft.Shuffle;
        IsAiContent = draft.IsAiContent;
        UseNativeSchedule = draft.UseNativeSchedule;
        RepeatMode = draft.RepeatMode;
        RepeatCount = draft.RepeatCount;
        StripMetadata = draft.StripMetadata;
        AutoCrop = draft.AutoCrop;
        AddWatermark = draft.AddWatermark;
        LogoPath = draft.LogoPath;
        InvalidatePreview();
    }

    public async Task AutoSaveAsync()
    {
        if (!_draftRestored || IsBusy) return;
        try
        {
            var draft = new ComposeEditorDraft
            {
                Title = Title, Description = Description, Tags = TagsText, Media = [.. SelectedMedia],
                Targets = [.. Targets.Where(t => t.IsSelected).Select(t => t.Id)],
                Captions = [.. PlatformCaptions.Select(c => new CaptionDraft(c.Platform, c.Title, c.Description, c.TagsText))],
                TargetOptions = [.. Targets.Select(t => new TargetDraft(t.Id, t.Title, t.Description, t.TagsText, t.Privacy, t.MadeForKids, t.ContainsSyntheticMedia))],
                RepeatMode = RepeatMode, RepeatCount = RepeatCount, StripMetadata = StripMetadata, AutoCrop = AutoCrop,
                AddWatermark = AddWatermark, LogoPath = LogoPath,
                Mode = ScheduleMode, Date = ScheduleDate, Start = ScheduleTime, End = ScheduleEnd,
                MinGap = MinGap, MaxGap = MaxGap, Shuffle = ShuffleTags,
                IsAiContent = IsAiContent, UseNativeSchedule = UseNativeSchedule
            };
            var serialized = System.Text.Json.JsonSerializer.Serialize(draft);
            if (_lastDraft == serialized) return;
            await settings.SetAsync("publishing.composeDraft", draft);
            _lastDraft = serialized;
            DraftStatus = $"Bản soạn tự lưu lúc {DateTime.Now:HH:mm}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DraftStatus = "Chưa tự lưu được bản soạn.";
            dialogs.Error("Không lưu được bản soạn: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task BrowseImagesAsync()
    {
        var paths = dialogs.OpenFiles("Chọn ảnh (nhiều ảnh tạo thành một bài carousel)", "Ảnh|*.jpg;*.jpeg;*.png;*.webp");
        if (paths.Length > 0) await ImportImagesAsync(paths);
    }

    public async Task ImportImagesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await ingest.IngestImagesAsync(paths, null, default);
            if (await contents.GetAsync(result.ContentId, default) is { } detail)
            {
                var option = new ContentOption(result.ContentId, detail.Item.Title, $"{paths.Count} ảnh", paths[0], true);
                if (SelectedMedia.All(m => m.Id != option.Id)) SelectedMedia.Add(option);
                SelectedContent = option;
            }
            InvalidatePreview();
        }
        catch (Exception ex) when (ex is IngestException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            dialogs.Error("Không nhập được ảnh: " + ex.Message);
        }
        finally { IsBusy = false; }
    }
}
