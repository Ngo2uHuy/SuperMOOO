using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Publications;
using SuperMOOO.Publishing.Scheduling;

namespace SuperMOOO.App.Features.S14;

public sealed partial class PlatformCaption(string platform) : ObservableObject
{
    public string Platform { get; } = platform;
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string TagsText { get; set; } = "";
}

public sealed record ComposePreview(string Content, string Target, string At, string Caption);

public sealed partial class ComposeViewModel
{
    private List<PublicationTargetRequest>? _previewItems;
    private string? _previewMode;
    public ObservableCollection<ContentOption> SelectedMedia { get; } = [];
    public ObservableCollection<ComposePreview> Preview { get; } = [];
    public IReadOnlyList<PlatformCaption> PlatformCaptions { get; } =
        [new("Facebook"), new("TikTok"), new("Instagram"), new("YouTube")];
    public IReadOnlyList<TikTokOption> ScheduleModes { get; } =
        [new("Now", "Đăng ngay"), new("Fixed", "Hẹn giờ cố định"), new("Spread", "Rải trong khung giờ")];

    [ObservableProperty] public partial string ScheduleMode { get; set; } = "Fixed";
    [ObservableProperty] public partial string ScheduleEnd { get; set; } = "21:00";
    [ObservableProperty] public partial string MinGap { get; set; } = "15";
    [ObservableProperty] public partial string MaxGap { get; set; } = "40";
    [ObservableProperty] public partial bool ShuffleTags { get; set; }
    [ObservableProperty] public partial string PlanStatus { get; set; } = "";
    [ObservableProperty] public partial bool ShowLibrary { get; set; }
    [ObservableProperty] public partial string RepeatMode { get; set; } = "None";
    [ObservableProperty] public partial string RepeatCount { get; set; } = "2";
    public bool IsRepeating => RepeatMode != "None";
    public bool CanEdit => !IsBusy;
    public IReadOnlyList<TikTokOption> RepeatModes { get; } = [new("None", "Không"), new("Daily", "Hằng ngày"), new("Weekly", "Hằng tuần")];
    public string SelectedTargetSummary => $"{Targets.Count(t => t.IsSelected)} / {Targets.Count} đã chọn";
    public string QueueButtonText
    {
        get
        {
            var repeats = IsRepeating && ScheduleMode != "Now" && int.TryParse(RepeatCount, out var count) && count >= 2 ? count : 1;
            return $"Thêm vào hàng đợi ({(long)SelectedMedia.Count * Targets.Count(t => t.IsSelected) * repeats})";
        }
    }
    [RelayCommand] private void ToggleLibrary() => ShowLibrary = !ShowLibrary;

    public string SelectionSummary => $"{SelectedMedia.Count} tệp · {Targets.Count(t => t.IsSelected)} kênh đã chọn";

    private void InvalidatePreview()
    {
        _previewItems = null;
        Preview.Clear();
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectedTargetSummary));
        OnPropertyChanged(nameof(QueueButtonText));
        OnPropertyChanged(nameof(CharacterSummary));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(CanEdit));
        if (e.PropertyName == nameof(Description)) OnPropertyChanged(nameof(CharacterSummary));
        if (e.PropertyName == nameof(RepeatMode)) OnPropertyChanged(nameof(IsRepeating));
        if (e.PropertyName is nameof(ScheduleMode) or nameof(ScheduleDate) or nameof(ScheduleTime) or nameof(ScheduleEnd)
            or nameof(MinGap) or nameof(MaxGap) or nameof(ShuffleTags) or nameof(Title) or nameof(Description) or nameof(TagsText)
            or nameof(RepeatMode) or nameof(RepeatCount) or nameof(IsAiContent))
            InvalidatePreview();
    }

    [RelayCommand]
    private void AddSelectedMedia()
    {
        if (SelectedContent is { } item && SelectedMedia.All(m => m.Id != item.Id)) SelectedMedia.Add(item);
        InvalidatePreview();
    }

    [RelayCommand]
    private void RemoveMedia(ContentOption? item)
    {
        if (item is not null) SelectedMedia.Remove(item);
        InvalidatePreview();
    }

    [RelayCommand]
    private void MoveMediaUp(ContentOption? item)
    {
        if (item is not null && SelectedMedia.IndexOf(item) is > 0 and var index)
            SelectedMedia.Move(index, index - 1);
        InvalidatePreview();
    }

    [RelayCommand]
    private void SelectAvailableTargets()
    {
        foreach (var target in Targets) target.IsSelected = target.Blocked is null;
        InvalidatePreview();
    }

    [RelayCommand]
    private void ClearTargets()
    {
        foreach (var target in Targets) target.IsSelected = false;
        InvalidatePreview();
    }

    [RelayCommand]
    private void PreviewSchedule()
    {
        try
        {
            _previewMode = ScheduleMode == "Now" ? PublishModes.Now : PublishModes.Schedule;
            _previewItems = BuildItems(_previewMode);
            Preview.Clear();
            foreach (var item in _previewItems)
                Preview.Add(new ComposePreview(SelectedMedia.FirstOrDefault(m => m.Id == item.ContentItemId)?.Title ?? SelectedContent?.Title ?? "",
                    Targets.First(t => t.Id == item.DestinationId).Display,
                    item.ScheduledUtc is { } at ? PostRow.VietnamTimeText(at) : "Ngay khi đến lượt", item.Description ?? ""));
            PlanStatus = $"{Preview.Count} lượt đăng; nội dung và giờ trong bản xem trước sẽ được dùng khi thêm hàng đợi.";
        }
        catch (InvalidOperationException ex) { dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private Task QueueAsync() => SubmitAsync(ScheduleMode == "Now" ? PublishModes.Now : PublishModes.Schedule);

    private List<PublicationTargetRequest> BuildItems(string mode)
    {
        var media = SelectedMedia.ToList();
        var targets = Targets.Where(t => t.IsSelected).ToList();
        if (media.Count == 0) throw new InvalidOperationException("Chọn ít nhất một tệp từ kho hoặc từ máy.");
        if (targets.Count == 0) throw new InvalidOperationException("Chọn ít nhất một kênh/trang đích.");
        if (targets.FirstOrDefault(t => !t.CanSelect) is { } blocked)
            throw new InvalidOperationException($"{blocked.Display}: {blocked.Blocked}");
        if (media.Count * targets.Count > PublicationService.MaxItemsPerRequest)
            throw new InvalidOperationException($"Mỗi lần tối đa {PublicationService.MaxItemsPerRequest} lượt đăng (số tệp × số kênh).");
        DateTimeOffset? start = null;
        var min = 0;
        var max = 0;
        var spread = mode == PublishModes.Schedule && ScheduleMode == "Spread";
        DateTimeOffset? end = null;
        if (mode == PublishModes.Schedule)
        {
            if (ScheduleDate is not { } date || !TimeOnly.TryParseExact(ScheduleTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new InvalidOperationException("Nhập ngày và giờ HH:mm (giờ Việt Nam).");
            start = VietnamTime.FromLocal(date.Date + time.ToTimeSpan());
            if (start <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Giờ bắt đầu phải ở tương lai.");
            if (spread)
            {
                if (!TimeOnly.TryParseExact(ScheduleEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
                    throw new InvalidOperationException("Giờ kết thúc phải có dạng HH:mm.");
                end = VietnamTime.FromLocal(date.Date + until.ToTimeSpan());
                if (end <= start) throw new InvalidOperationException("Giờ kết thúc phải sau giờ bắt đầu trong cùng ngày.");
                if (!int.TryParse(MinGap, out min) || !int.TryParse(MaxGap, out max) || min < 1 || max < min || max > 1440)
                    throw new InvalidOperationException("Giãn cách phải từ 1 đến 1440 phút; tối đa không nhỏ hơn tối thiểu.");
                if ((end - start)!.Value.TotalMinutes < (media.Count * targets.Count - 1) * min)
                    throw new InvalidOperationException("Khung giờ không đủ chỗ cho tất cả lượt đăng với giãn cách tối thiểu đã chọn.");
            }
        }
        var result = new List<PublicationTargetRequest>();
        var at = start;
        foreach (var content in media)
        foreach (var target in targets)
        {
            if (spread && result.Count > 0)
            {
                var remaining = media.Count * targets.Count - result.Count - 1;
                var available = (int)(end!.Value - at!.Value).TotalMinutes - remaining * min;
                at = at.Value.AddMinutes(Random.Shared.Next(min, Math.Min(max, available) + 1));
            }
            var platform = PlatformCaptions.FirstOrDefault(p => p.Platform == target.Destination.Platform);
            var request = target.ToRequest(content.Id, at);
            var title = FirstText(target.Title, platform?.Title, Title, content.Title);
            var description = FirstText(target.Description, platform?.Description, Description);
            var tags = FirstText(target.TagsText, platform?.TagsText, TagsText);
            var tagList = Expand(tags).Split([',', '\n', '#'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
            if (ShuffleTags) Random.Shared.Shuffle(tagList);
            var item = request with { Title = Expand(title), Description = Expand(description), Tags = [.. tagList],
                ContainsSyntheticMedia = target.ContainsSyntheticMedia || IsAiContent };
            if (mode != PublishModes.Draft) ValidateText(target, item);
            result.Add(item);
        }
        if (IsRepeating && mode != PublishModes.Draft)
        {
            if (mode != PublishModes.Schedule) throw new InvalidOperationException("Lặp lại chỉ dùng với hẹn giờ hoặc rải trong khung giờ.");
            if (!int.TryParse(RepeatCount, out var count) || count < 2 || (long)count * result.Count > PublicationService.MaxItemsPerRequest)
                throw new InvalidOperationException($"Số đợt phải từ 2 và tổng số lượt không quá {PublicationService.MaxItemsPerRequest}.");
            var first = result.ToArray();
            for (var day = 1; day < count; day++)
                result.AddRange(first.Select(item => item with { ScheduledUtc = item.ScheduledUtc!.Value.AddDays(day * (RepeatMode == "Weekly" ? 7 : 1)) }));
        }
        return result;
    }

    private static string FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string Expand(string text)
    {
        // Xử lý từ nhóm trong cùng để hỗ trợ cả biến thể lồng nhau.
        for (var depth = 0; depth < 16; depth++)
        {
            var changed = false;
            text = Regex.Replace(text, @"\{([^{}]*\|[^{}]*)\}", match =>
            {
                changed = true;
                var choices = match.Groups[1].Value.Split('|');
                return choices[Random.Shared.Next(choices.Length)];
            }, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!changed) return text;
        }
        throw new InvalidOperationException("Caption có quá nhiều lớp biến thể lồng nhau (tối đa 16).");
    }
}
