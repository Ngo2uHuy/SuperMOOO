using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SuperMOOO.AI.Translation;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Workflows.Delivery;

namespace SuperMOOO.Workflows.AutoReup;

/// <summary>Cài đặt hiệu lực của một kênh đích: ghi đè trong luồng → hồ sơ thị trường → cài đặt chung của luồng.</summary>
public sealed record ReupTargetSettings(string DestinationId, MarketProfile? Profile, string Language, bool Translate, PostingSchedule? Schedule,
    IReadOnlyList<PostingWindow> Windows, IReadOnlyList<DayOfWeek> Days, int DailyLimit, string TitleTemplate, string CaptionTemplate, string Hashtags,
    string Privacy, string? Warning = null);

/// <summary>Kết quả xem trước một kênh: tiêu đề/mô tả hiệu lực và giờ đăng dự kiến.</summary>
public sealed record ReupPreview(string DestinationId, string Profile, string Title, string Caption, DateTimeOffset DueUtc, string DueLocal, string DueMachine,
    string? Warning);

/// <summary>Áp hồ sơ thị trường (S23) cho từng kênh đích của luồng S08: mẫu chữ, hashtag, dịch tiêu đề, khung giờ đăng theo giờ địa phương.</summary>
[Service]
public sealed class ReupMarketService(ISettingsStore settings, OAuthConnectService oauth, TranslatorRouter translator, DeliveryService delivery,
    ILogger<ReupMarketService> log)
{
    private const int CacheSize = 500;
    // Nhiều kênh cùng ngôn ngữ (và lần xem trước) chỉ dịch tiêu đề một lần.
    private readonly ConcurrentDictionary<(string Video, string Language), string> _titles = new();

    public MarketSettings Markets() => settings.Get<MarketSettings>(MarketSettings.Section);

    /// <summary>Kênh đích → id tài khoản (connection).</summary>
    public async Task<IReadOnlyDictionary<string, string>> ConnectionsAsync(CancellationToken ct)
    {
        try { return (await oauth.ListDestinationsAsync(new ListDestinationsRequest(null), ct)).ToDictionary(d => d.Id, d => d.ConnectionId); }
        catch (PublishingException e) { throw new InvalidOperationException(e.Message, e); }
    }

    public static ReupTargetSettings Resolve(ReupRuleConfig rule, string destinationId, string? connectionId, MarketSettings markets)
    {
        var options = rule.Destinations.FirstOrDefault(d => d.DestinationId == destinationId);
        var profile = options switch
        {
            // Luồng cũ chưa cấu hình theo kênh: giữ nguyên cài đặt chung.
            null => null,
            { MarketProfileId: null } => connectionId is null ? null : markets.ProfileOf(connectionId),
            { MarketProfileId: ReupDestinationOptions.NoProfile or "" } => null,
            { MarketProfileId: var id } => markets.Profiles.FirstOrDefault(p => p.Id == id)
        };
        // Hồ sơ đã chọn bị xoá: vẫn chạy bằng cài đặt chung nhưng báo để người dùng sửa.
        var missing = profile is null && options is { MarketProfileId: { Length: > 0 } pid } && pid != ReupDestinationOptions.NoProfile;
        var o = options?.Overrides ?? new ReupOverrides();
        IReadOnlyList<PostingWindow> windows = o.Windows ?? profile?.Windows ?? [];
        IReadOnlyList<DayOfWeek> days = o.Days ?? profile?.Days ?? [];
        var zone = o.TimeZoneId ?? profile?.TimeZoneId;
        var schedule = profile is not null || windows.Count > 0 || days.Count > 0 || !string.IsNullOrEmpty(zone)
            ? new PostingSchedule(MarketProfile.ResolveZone(zone), windows, days) : null;
        var language = o.Language ?? profile?.Language ?? "";
        return new ReupTargetSettings(destinationId, profile, language, (o.TranslateText ?? profile?.TranslateText ?? false) && language.Length > 0,
            schedule, windows, days, o.DailyLimit ?? profile?.DailyLimit ?? rule.DailyLimit, o.TitleTemplate ?? profile?.TitleTemplate ?? rule.TitleTemplate,
            o.CaptionTemplate ?? profile?.CaptionTemplate ?? rule.CaptionTemplate, o.Hashtags ?? profile?.Hashtags ?? "", o.Privacy ?? profile?.Privacy ?? rule.Privacy,
            missing ? MissingProfileWarning : null);
    }

    public const string MissingProfileWarning = "Hồ sơ đã bị xoá, đang dùng cài đặt chung";

    /// <summary>Tóm tắt giờ đăng cho bảng kênh ở S08.</summary>
    public static string Summary(ReupTargetSettings s, ReupRuleConfig rule)
    {
        if (s.Schedule is null) return $"Theo luồng · cách {rule.SpacingMinutes} phút · {s.DailyLimit}/ngày";
        var windows = s.Windows.Count == 0 ? "cả ngày" : MarketProfile.FormatWindows(s.Windows);
        var days = s.Days.Count is 0 or 7 ? "" : " · " + string.Join(",", s.Days.Select(DayName));
        var offset = s.Schedule.Zone.GetUtcOffset(DateTimeOffset.UtcNow);
        return $"{windows}{days} · UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm} · {s.DailyLimit}/ngày";
    }

    public static string DayName(DayOfWeek d) => d == DayOfWeek.Sunday ? "CN" : "T" + ((int)d + 1);

    /// <summary>
    /// Trả hàm dựng tiêu đề/mô tả/lịch cho từng kênh, chỉ chạy (và dịch) khi kênh đó thực sự tạo bài, để video bị bỏ qua vì trùng không tốn lượt dịch.
    /// Dịch lỗi thì dùng bản gốc; cảnh báo gom vào <c>Warnings</c> sau khi hàm được gọi.
    /// </summary>
    public async Task<(Func<string, CancellationToken, Task<DeliveryTarget?>> TargetFor, IReadOnlyList<string> Warnings)> PrepareAsync(ReupRuleConfig rule,
        ReupVideo video, CancellationToken ct)
    {
        var markets = Markets();
        var connections = await ConnectionsAsync(ct);
        var warnings = new List<string>();
        void Warn(string? w)
        {
            if (w is not null && !warnings.Contains(w)) warnings.Add(w);
        }
        async Task<DeliveryTarget?> TargetFor(string id, CancellationToken token)
        {
            var s = Resolve(rule, id, connections.GetValueOrDefault(id), markets);
            Warn(s.Warning);
            var (target, warning) = await TargetAsync(rule, s, video, token);
            Warn(warning);
            return target;
        }
        return (TargetFor, warnings);
    }

    public async Task<IReadOnlyList<ReupPreview>> PreviewAsync(ReupRuleConfig rule, ReupVideo video, CancellationToken ct)
    {
        var markets = Markets();
        var connections = await ConnectionsAsync(ct);
        var previews = new List<ReupPreview>();
        foreach (var id in rule.DestinationIds)
        {
            var s = Resolve(rule, id, connections.GetValueOrDefault(id), markets);
            var (target, warning) = await TargetAsync(rule, s, video, ct);
            var due = await delivery.NextDueAsync(id, target.Pacing, rule.PublishMode == ReupPublishModes.Automatic, ct);
            var zone = s.Schedule?.Zone ?? TimeZoneInfo.Local;
            previews.Add(new ReupPreview(id, s.Profile?.Name ?? "Cài đặt chung", target.Title, target.Caption, due,
                $"{TimeZoneInfo.ConvertTime(due, zone):dd/MM HH:mm} ({zone.StandardName})", due.ToLocalTime().ToString("dd/MM HH:mm"),
                string.Join("; ", new[] { s.Warning, warning }.OfType<string>()) is { Length: > 0 } w ? w : null));
        }
        return previews;
    }

    private async Task<(DeliveryTarget Target, string? Warning)> TargetAsync(ReupRuleConfig rule, ReupTargetSettings s, ReupVideo video, CancellationToken ct)
    {
        string? title = null, warning = null;
        if (s.Translate) (title, warning) = await TranslateTitleAsync(video, s.Language, ct);
        var caption = ReupRuleConfig.Fill(s.CaptionTemplate, video, rule.SourcePlatform, s.Hashtags, title);
        // Mẫu không có {hashtags} thì vẫn gắn hashtag của hồ sơ vào cuối mô tả.
        if (s.Hashtags.Length > 0 && !s.CaptionTemplate.Contains("{hashtags}", StringComparison.Ordinal)) caption = (caption + "\n" + s.Hashtags).Trim();
        return (new DeliveryTarget(ReupRuleConfig.Fill(s.TitleTemplate, video, rule.SourcePlatform, s.Hashtags, title), caption, s.Privacy,
            new SchedulePacing(rule.DelayMinutes, rule.SpacingMinutes, s.DailyLimit, s.Schedule)), warning);
    }

    private async Task<(string Title, string? Warning)> TranslateTitleAsync(ReupVideo video, string language, CancellationToken ct)
    {
        var key = (video.Url, language);
        if (_titles.TryGetValue(key, out var hit)) return (hit, null);
        try
        {
            var text = (await translator.TranslateAsync([video.Title], new TranslateOptions("auto", language), ct))[0].Trim();
            if (text.Length == 0) throw new InvalidOperationException("bản dịch rỗng");
            if (_titles.Count >= CacheSize) _titles.Clear();
            _titles[key] = text;
            return (text, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Dịch tiêu đề video {Key} sang {Language} lỗi, dùng bản gốc", video.Key, language);
            return (video.Title, $"Dịch tiêu đề sang {language} lỗi, dùng bản gốc: {e.Message}");
        }
    }
}
