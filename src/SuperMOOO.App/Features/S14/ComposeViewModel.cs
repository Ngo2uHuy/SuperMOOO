using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using SuperMOOO.AI.Llm;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.App.Features.S14;

public sealed record ContentOption(string Id, string Title, string Info, string? Thumbnail = null, bool IsImage = false)
{
    public string Kind => IsImage ? "ẢNH" : "VIDEO";
}

public sealed record TikTokOption(string Key, string Label);

/// <summary>Một đích kênh/trang với nội dung riêng; TikTok có thêm form Direct Post.</summary>
public sealed partial class TargetViewModel(DestinationDto destination) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display), nameof(Initial), nameof(Blocked), nameof(CanSelect))]
    public partial DestinationDto Destination { get; set; } = destination;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Blocked), nameof(CanSelect))]
    public partial bool IsPaused { get; set; }
    public bool CanSelect => string.IsNullOrEmpty(Blocked);
    public string Id => Destination.Id;
    public string Initial => string.IsNullOrWhiteSpace(Destination.ConnectionName) ? "?" : Destination.ConnectionName[..1].ToUpperInvariant();
    public string Display => $"{Labels.Term(Destination.Platform)} · {Destination.Title} ({Destination.ConnectionName})";
    public string? Blocked => IsPaused ? "Tài khoản đang tạm dừng tại S23."
        : Destination.ConnectionApiStatus != nameof(ApiStatus.Connected)
        ? "Tài khoản chưa kết nối API hoặc cần kết nối lại." : Destination.AutoPublishBlockedReason;
    public bool IsTikTok => Destination.Platform == nameof(SocialPlatform.TikTok);
    public bool IsYouTube => Destination.Platform == nameof(SocialPlatform.YouTube);
    public IReadOnlyList<string> PrivacyOptions { get; } = Enum.GetNames<PublishPrivacy>();
    public IReadOnlyList<TikTokOption> TikTokModes { get; } =
        [new(TikTokPostModes.Inbox, "Gửi vào hộp thư TikTok"), new(TikTokPostModes.DirectPost, "Đăng trực tiếp (Direct Post)")];
    public ObservableCollection<TikTokOption> TikTokPrivacyChoices { get; } = [];

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect) IsSelected = false;
    }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    public partial string TagsText { get; set; } = "";

    [ObservableProperty]
    public partial string Privacy { get; set; } = nameof(PublishPrivacy.Private);

    [ObservableProperty]
    public partial bool MadeForKids { get; set; }

    [ObservableProperty]
    public partial bool ContainsSyntheticMedia { get; set; }

    [ObservableProperty]
    public partial string TikTokMode { get; set; } = TikTokPostModes.Inbox;

    [ObservableProperty]
    public partial string? TikTokPrivacy { get; set; }

    [ObservableProperty]
    public partial bool AllowComment { get; set; }

    [ObservableProperty]
    public partial bool AllowDuet { get; set; }

    [ObservableProperty]
    public partial bool AllowStitch { get; set; }

    [ObservableProperty]
    public partial bool DiscloseContent { get; set; }

    [ObservableProperty]
    public partial bool YourBrand { get; set; }

    [ObservableProperty]
    public partial bool BrandedContent { get; set; }

    [ObservableProperty]
    public partial bool UserConsented { get; set; }

    [ObservableProperty]
    public partial string CreatorInfo { get; set; } = "";

    [ObservableProperty]
    public partial bool CommentDisabled { get; set; }

    [ObservableProperty]
    public partial bool DuetDisabled { get; set; }

    [ObservableProperty]
    public partial bool StitchDisabled { get; set; }

    public bool IsDirectPost => IsTikTok && TikTokMode == TikTokPostModes.DirectPost;

    partial void OnTikTokModeChanged(string value) => OnPropertyChanged(nameof(IsDirectPost));

    public TikTokPostSettingsDto? ToTikTok() => !IsTikTok ? null : TikTokMode == TikTokPostModes.DirectPost
        ? new TikTokPostSettingsDto(TikTokPostModes.DirectPost, TikTokPrivacy, AllowComment && !CommentDisabled, AllowDuet && !DuetDisabled,
            AllowStitch && !StitchDisabled, DiscloseContent, YourBrand, BrandedContent, UserConsented)
        : new TikTokPostSettingsDto(TikTokPostModes.Inbox);

    public PublicationTargetRequest ToRequest(string contentId, DateTimeOffset? at) => new(
        contentId, Id, Title, Description, [.. TagsText.Split([',', '\n', '#'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        Privacy, MadeForKids, ContainsSyntheticMedia, at, ToTikTok());
}

public sealed partial class ComposeViewModel(PublicationService publications, OAuthConnectService oauth, PlatformSettingsService platform,
    IContentRepository contents, LlmProviderRegistry llm, PostActions postActions, IDialogService dialogs,
    SuperMOOO.Publishing.Library.ContentIngestService ingest, S21.ContentHandoff handoff, ISettingsStore settings, PublishMediaPreparer mediaPreparer,
    IPublishSettingsStore publishSettings) : ObservableObject
{
    public LlmPickerModel ScriptAi { get; } = new(llm, settings, dialogs);

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public event Action? Created;

    public ObservableCollection<ContentOption> Contents { get; } = [];
    public ObservableCollection<TargetViewModel> Targets { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial ContentOption? SelectedContent { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    public partial string TagsText { get; set; } = "";

    [ObservableProperty]
    public partial string AiHint { get; set; } = "";

    [ObservableProperty]
    public partial bool IsAiContent { get; set; } = true;

    [ObservableProperty]
    public partial DateTime? ScheduleDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial string ScheduleTime { get; set; } = "09:00";

    [ObservableProperty]
    public partial bool UseNativeSchedule { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    partial void OnSelectedContentChanged(ContentOption? value)
    {
        if (value is not null && Title.Length == 0) Title = value.Title;
    }

    /// <summary>Lần nạp gần nhất vừa nhận video từ S21: view chuyển sang tab Soạn bài rồi tự xoá cờ.</summary>
    public bool TookHandoff { get; set; }

    public async Task LoadAsync()
    {
        try
        {
            var (items, _) = await contents.QueryAsync(string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(), null, 1, 100, default);
            var destinations = await oauth.ListDestinationsAsync(new ListDestinationsRequest(null), default);
            var paused = new HashSet<string>();
            foreach (var id in destinations.Select(d => d.ConnectionId).Distinct())
                if (await publishSettings.GetAsync("account.paused:" + id, default) == "true") paused.Add(id);
            // Video được chọn từ S21 ("Soạn bài"): đưa lên đầu danh sách dù không nằm trong trang tìm kiếm hiện tại.
            var handed = handoff.Take() is { } handedId && await contents.GetAsync(handedId, default) is { } handedDetail
                ? Option(handedDetail) : null;
            if (handed is not null) TookHandoff = true;
            UiPost.Run(_ui, () =>
            {
                var keep = handed?.Id ?? SelectedContent?.Id;
                Contents.Sync([.. items.Select(i => new ContentOption(i.Item.Id, i.Item.Title, Info(i.Item), i.ThumbnailPath,
                    SuperMOOO.Publishing.Library.MediaFiles.IsImage(i.Item.OriginalPath ?? "")))], c => c.Id, c => c);
                if (handed is not null && Contents.All(c => c.Id != handed.Id)) Contents.Insert(0, handed);
                if (handed is not null && SelectedMedia.All(c => c.Id != handed.Id)) SelectedMedia.Add(handed);
                SelectedContent = Contents.FirstOrDefault(c => c.Id == keep);
                var known = Targets.ToDictionary(t => t.Id);
                var fresh = destinations.Where(d => d.Enabled && !d.Removed).ToList();
                foreach (var gone in Targets.Where(t => fresh.All(d => d.Id != t.Id)).ToList()) Targets.Remove(gone);
                foreach (var d in fresh)
                {
                    if (known.TryGetValue(d.Id, out var existing))
                    {
                        existing.Destination = d;
                        existing.IsPaused = paused.Contains(d.ConnectionId);
                        if (!existing.CanSelect) existing.IsSelected = false;
                        continue;
                    }
                    var target = new TargetViewModel(d) { IsPaused = paused.Contains(d.ConnectionId) };
                    target.PropertyChanged += (_, _) => InvalidatePreview();
                    Targets.Add(target);
                }
                InvalidatePreview();
                RestoreEditorDraft();
            });
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private Task SearchContentAsync() => LoadAsync();

    [RelayCommand]
    private async Task BrowseVideoAsync()
    {
        var paths = dialogs.OpenFiles("Chọn video", "Video|*.mp4;*.mov;*.mkv;*.webm");
        if (paths.Length == 0) return;
        await ImportVideosAsync(paths);
    }

    public async Task ImportVideosAsync(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            foreach (var path in paths)
            {
            // Một cổng nhập chung: sha256 cache theo size+mtime, chống trùng, không chép file.
            var request = SuperMOOO.Publishing.Library.LooseFileReader.Build(path, SuperMOOO.Publishing.Library.LibrarySources.Manual, null, requireStable: false)
                with { IsAiGenerated = IsAiContent };
            var existing = (await ingest.IngestAsync(request, default)).ContentId;
                if (await contents.GetAsync(existing, default) is { } detail)
                {
                    var option = Option(detail);
                    if (SelectedMedia.All(m => m.Id != existing)) SelectedMedia.Add(option);
                    SelectedContent = option;
                }
            }
            InvalidatePreview();
        }
        catch (SuperMOOO.Publishing.Library.IngestException ex)
        {
            dialogs.Error(ex.Message);
        }
        catch (IOException ex)
        {
            dialogs.Error("Không đọc được file video: " + ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ApplyCommon()
    {
        foreach (var t in Targets.Where(t => t.IsSelected))
        {
            t.Title = Title;
            t.Description = Description;
            t.TagsText = TagsText;
        }
    }

    [RelayCommand]
    private Task SuggestCommonAsync() => SuggestAsync(null);

    [RelayCommand]
    private Task SuggestTargetAsync(TargetViewModel? target) => SuggestAsync(target);

    private async Task SuggestAsync(TargetViewModel? target)
    {
        SelectedContent ??= SelectedMedia.FirstOrDefault();
        if (SelectedContent is null)
        {
            dialogs.Error("Chọn video trước khi nhờ AI gợi ý.");
            return;
        }
        IsBusy = true;
        try
        {
            var platformName = target is null ? "mạng xã hội" : Labels.Term(target.Destination.Platform);
            var prompt = $"Viết nội dung đăng video lên {platformName}. Tên video: \"{SelectedContent.Title}\". "
                + (AiHint.Length > 0 ? $"Gợi ý thêm: {AiHint}. " : "")
                + "Trả về JSON {\"title\": string (tối đa 90 ký tự), \"description\": string, \"tags\": string[] (tối đa 8, không có dấu #)} bằng tiếng Việt.";
            var reply = await llm.Get(ScriptAi.ProviderId).CompleteAsync(new LlmRequest([new ChatMessage(ChatRole.User, prompt)], Model: string.IsNullOrWhiteSpace(ScriptAi.Model) ? null : ScriptAi.Model.Trim(), MaxTokens: 1024, Temperature: 0.7, JsonOutput: true), default);
            var json = JsonNode.Parse(reply.Text.Trim().Trim('`').Replace("json\n", "", StringComparison.Ordinal));
            var title = json?["title"]?.GetValue<string>() ?? "";
            var description = json?["description"]?.GetValue<string>() ?? "";
            var tags = string.Join(", ", json?["tags"]?.AsArray().Select(t => t?.GetValue<string>()).OfType<string>() ?? []);
            if (target is null) (Title, Description, TagsText) = (title, description, tags);
            else (target.Title, target.Description, target.TagsText) = (title, description, tags);
        }
        catch (Exception ex) when (ex is ProviderException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            dialogs.Error("AI chưa gợi ý được: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadCreatorInfoAsync(TargetViewModel? target)
    {
        if (target is null) return;
        try
        {
            var info = await platform.GetTikTokCreatorInfoAsync(new GetTikTokCreatorInfoRequest(target.Id), default);
            target.TikTokPrivacyChoices.Clear();
            foreach (var key in info.PrivacyLevelOptions) target.TikTokPrivacyChoices.Add(new TikTokOption(key, PrivacyLabel(key)));
            target.CommentDisabled = info.CommentDisabled;
            target.DuetDisabled = info.DuetDisabled;
            target.StitchDisabled = info.StitchDisabled;
            target.CreatorInfo = $"{info.Nickname ?? info.Username}"
                + (info.MaxVideoPostDurationSec is { } sec ? $" · tối đa {sec} giây" : "")
                + (info.DirectPostAudited ? "" : " · app chưa qua audit: chỉ chọn được \"Chỉ mình tôi\"");
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private Task PublishNowAsync() => SubmitAsync(PublishModes.Now);

    [RelayCommand]
    private Task ScheduleAsync() => SubmitAsync(PublishModes.Schedule);

    [RelayCommand]
    private Task SaveDraftAsync() => SubmitAsync(PublishModes.Draft);

    private async Task SubmitAsync(string mode)
    {
        if (IsBusy) return;
        List<PublicationTargetRequest> items;
        try
        {
            items = _previewItems is not null && _previewMode == mode ? _previewItems : BuildItems(mode);
            if (mode == PublishModes.Schedule && items.Any(i => i.ScheduledUtc <= DateTimeOffset.UtcNow))
                throw new InvalidOperationException("Lịch xem trước đã quá giờ. Hãy chọn giờ mới rồi xem trước lại.");
        }
        catch (InvalidOperationException ex)
        {
            dialogs.Error(ex.Message);
            return;
        }
        Task<CreatePublicationsResultDto> Send(bool ack) =>
            publications.CreateAsync(new CreatePublicationsRequest(items, mode, null, UseNativeSchedule && mode == PublishModes.Schedule, ack, ack,
                AllowRepeatedSchedule: IsRepeating), default);
        IsBusy = true;
        try
        {
            if (mode != PublishModes.Draft)
                foreach (var target in Targets.Where(t => t.IsSelected))
                    if (await publishSettings.GetAsync("account.paused:" + target.Destination.ConnectionId, default) == "true")
                    {
                        target.IsPaused = true;
                        target.IsSelected = false;
                        throw new InvalidOperationException($"{target.Display}: tài khoản đang tạm dừng. Tiếp tục tài khoản ở S23 rồi cập nhật danh sách.");
                    }
            if (AddWatermark && string.IsNullOrWhiteSpace(LogoPath)) throw new InvalidOperationException("Chọn logo trước khi chèn watermark.");
            items = await mediaPreparer.PrepareAsync(items, Targets.ToDictionary(t => t.Id, t => t.Destination.Platform),
                StripMetadata, AutoCrop, AddWatermark ? LogoPath : null, new Progress<string>(message => PlanStatus = message), default);
            var result = await postActions.ConfirmWarningsAsync(await Send(false), Send);
            if (result is null) return;
            dialogs.Info("Đăng bài", mode switch
            {
                PublishModes.Draft => $"Đã lưu {result.PublicationIds.Count} bản nháp.",
                PublishModes.Schedule => $"Đã hẹn giờ {result.PublicationIds.Count} bài.",
                _ => $"Đã đưa {result.PublicationIds.Count} bài vào hàng đợi đăng."
            });
            InvalidatePreview();
            Created?.Invoke();
        }
        catch (Exception ex) when (ex is PublishingException or IOException or InvalidOperationException or SuperMOOO.Media.Ffmpeg.FfmpegException
            or SuperMOOO.Publishing.Library.IngestException)
        {
            dialogs.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static ContentOption Option(ContentDetail detail) => new(detail.Item.Id, detail.Item.Title,
        detail.Assets.Any(a => a.Kind == MediaKind.Image) ? $"{detail.Assets.Count(a => a.Kind == MediaKind.Image)} ảnh" : Info(detail.Item),
        detail.Assets.FirstOrDefault(a => a.Kind is MediaKind.Thumbnail or MediaKind.Image)?.StoredPath,
        detail.Assets.Any(a => a.Kind == MediaKind.Image));

    private static string Info(FinishedContent item) =>
        (item.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss") + " · " : "") + (item.IsAiGenerated ? "AI" : "");

    private static string PrivacyLabel(string key) => key switch
    {
        TikTokPrivacyLevels.PublicToEveryone => "Mọi người",
        TikTokPrivacyLevels.MutualFollowFriends => "Bạn bè (theo dõi lẫn nhau)",
        TikTokPrivacyLevels.FollowerOfCreator => "Người theo dõi",
        TikTokPrivacyLevels.SelfOnly => "Chỉ mình tôi",
        _ => key
    };
}
