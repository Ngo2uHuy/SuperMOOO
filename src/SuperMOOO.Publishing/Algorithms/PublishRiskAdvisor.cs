using System.Globalization;
using System.Text;
using SuperMOOO.Publishing.Algorithms;

namespace SuperMOOO.Publishing.Algorithms;

public enum RiskLevel { Info, Warning, Block }

public enum BreakerState { Closed, HalfOpen, Open }

public sealed record RiskWarning(string PublicationId, RiskLevel Level, string Code, string Message);

/// <param name="NetworkProfileId">Network profile/proxy sẽ dùng cho lần đăng này.</param>
/// <param name="LastNetworkProfileId">Network profile của lần dùng trước (null nếu chưa có).</param>
/// <param name="MaxPostsPerDay">Giới hạn riêng của tài khoản (null = chỉ theo trần nền tảng).</param>
/// <param name="GroupId">Các đích chung một lần đăng nhập (Meta: nhiều Page) tính là một tài khoản khi xét dùng chung proxy.</param>
public sealed record RiskAccount(
    string AccountId,
    PlatformKind Platform,
    TimeZoneInfo TimeZone,
    string? NetworkProfileId,
    string? LastNetworkProfileId,
    bool HasBrowserSession,
    BreakerState Breaker,
    bool HasUnresolvedPolicyError,
    int? MaxPostsPerDay = null,
    TimeSpan? MinGap = null,
    string? GroupId = null);

public sealed record RiskContent(
    string ContentId,
    string? Sha256,
    VideoSignature? Signature,
    string? Caption,
    bool IsAiGenerated,
    bool AiDisclosed,
    bool IsReupload);

public sealed record PlannedPublication(string PublicationId, string AccountId, DateTimeOffset ScheduledUtc, RiskContent Content);

public sealed record PostedPublication(string AccountId, DateTimeOffset PostedUtc, RiskContent Content);

public sealed record RiskOptions
{
    public IReadOnlyDictionary<PlatformKind, PlatformLimit> Limits { get; init; } = PlatformLimits.Defaults;
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromDays(3);
    public double VideoSimilarityThreshold { get; init; } = 0.9;
    public double CaptionJaccardThreshold { get; init; } = 0.8;
    public int CaptionShingleSize { get; init; } = 3;
    public int MaxAccountsPerProxy { get; init; } = 3;
}

public static class PublishRiskAdvisor
{
    public static IReadOnlyList<RiskWarning> Assess(
        IReadOnlyList<PlannedPublication> planned,
        IReadOnlyList<PostedPublication> history,
        IReadOnlyList<RiskAccount> accounts,
        RiskOptions options)
    {
        var byId = accounts.ToDictionary(a => a.AccountId, StringComparer.Ordinal);
        var warnings = new List<RiskWarning>();

        foreach (var p in planned)
        {
            if (!byId.TryGetValue(p.AccountId, out var account))
            {
                warnings.Add(new(p.PublicationId, RiskLevel.Warning, "ACCOUNT_INFO_MISSING", "Thiếu thông tin tài khoản nên không đánh giá được rủi ro."));
                continue;
            }
            AccountState(p, account, warnings);
            Network(p, account, accounts, options, warnings);
            Rate(p, account, planned, history, options, warnings);
            Duplicates(p, account, planned, history, byId, options, warnings);
            ContentFlags(p, warnings);
        }
        return warnings;
    }

    public static bool IsBlocked(IEnumerable<RiskWarning> warnings, string publicationId) =>
        warnings.Any(w => w.PublicationId == publicationId && w.Level == RiskLevel.Block);

    private static void AccountState(PlannedPublication p, RiskAccount a, List<RiskWarning> w)
    {
        if (a.Breaker == BreakerState.Open)
            w.Add(new(p.PublicationId, RiskLevel.Block, "BREAKER_OPEN", "Tài khoản đang bị ngắt mạch do lỗi liên tiếp; không đăng cho tới khi hồi phục."));
        if (a.HasUnresolvedPolicyError)
            w.Add(new(p.PublicationId, RiskLevel.Block, "POLICY_ERROR_UNRESOLVED", "Tài khoản có lỗi quyền/chính sách chưa xử lý; cần xử lý trước khi đăng."));
    }

    private static void Network(PlannedPublication p, RiskAccount a, IReadOnlyList<RiskAccount> accounts, RiskOptions o, List<RiskWarning> w)
    {
        if (a.NetworkProfileId is null) return;

        var shared = accounts.Where(x => x.Platform == a.Platform && x.NetworkProfileId == a.NetworkProfileId)
            .Select(x => x.GroupId ?? x.AccountId).Distinct(StringComparer.Ordinal).Count();
        if (shared > o.MaxAccountsPerProxy)
            w.Add(new(p.PublicationId, RiskLevel.Warning, "PROXY_SHARED",
                $"Network profile này đang dùng chung cho {shared} tài khoản cùng nền tảng (ngưỡng {o.MaxAccountsPerProxy})."));

        if (a.HasBrowserSession && a.LastNetworkProfileId is not null && a.LastNetworkProfileId != a.NetworkProfileId)
            w.Add(new(p.PublicationId, RiskLevel.Warning, "PROXY_CHANGED_WITH_SESSION",
                "Đổi network profile/proxy cho tài khoản đang có phiên đăng nhập trình duyệt; nền tảng có thể yêu cầu xác minh lại."));
    }

    private static void Rate(
        PlannedPublication p, RiskAccount a, IReadOnlyList<PlannedPublication> planned, IReadOnlyList<PostedPublication> history,
        RiskOptions o, List<RiskWarning> w)
    {
        var platformLimit = o.Limits.GetValueOrDefault(a.Platform);
        var maxPerDay = Min(a.MaxPostsPerDay, platformLimit?.MaxPostsPerDay);
        var minGap = Max(a.MinGap, platformLimit?.MinGap);

        var others = history.Where(h => h.AccountId == a.AccountId).Select(h => h.PostedUtc)
            // Bài dự kiến chỉ tính khi đứng trước bài này, để chỉ bài vượt mức bị chặn chứ không chặn cả loạt.
            .Concat(planned.Where(x => x.AccountId == a.AccountId && x.PublicationId != p.PublicationId && Precedes(x, p)).Select(x => x.ScheduledUtc))
            .ToList();

        if (maxPerDay is { } max)
        {
            var day = LocalDate(a.TimeZone, p.ScheduledUtc);
            var count = others.Count(t => LocalDate(a.TimeZone, t) == day) + 1;
            if (count > max)
                w.Add(new(p.PublicationId, RiskLevel.Block, "RATE_DAILY_LIMIT", $"Vượt giới hạn {max} bài/ngày (theo ngày local của tài khoản): sẽ có {count} bài."));
        }

        if (minGap is { } gap && gap > TimeSpan.Zero && others.Any(t => (t - p.ScheduledUtc).Duration() < gap))
            w.Add(new(p.PublicationId, RiskLevel.Block, "RATE_MIN_GAP", $"Khoảng cách tới bài khác của tài khoản nhỏ hơn mức tối thiểu {gap.TotalMinutes:0} phút."));
    }

    private static void Duplicates(
        PlannedPublication p, RiskAccount a, IReadOnlyList<PlannedPublication> planned, IReadOnlyList<PostedPublication> history,
        Dictionary<string, RiskAccount> byId, RiskOptions o, List<RiskWarning> w)
    {
        var peers = history.Select(h => (h.AccountId, Time: h.PostedUtc, h.Content))
            .Concat(planned.Where(x => x.PublicationId != p.PublicationId).Select(x => (x.AccountId, Time: x.ScheduledUtc, x.Content)))
            .Where(x => x.AccountId != a.AccountId
                        && byId.TryGetValue(x.AccountId, out var other) && other.Platform == a.Platform
                        && (x.Time - p.ScheduledUtc).Duration() <= o.DuplicateWindow)
            .ToList();

        var videoAccounts = peers.Where(x => SameVideo(p.Content, x.Content, o)).Select(x => x.AccountId).Distinct().Count();
        if (videoAccounts > 0)
            w.Add(new(p.PublicationId, RiskLevel.Warning, "DUP_VIDEO_MULTI_ACCOUNT",
                $"Cùng video (hoặc gần trùng) cũng đăng lên {videoAccounts} tài khoản khác cùng nền tảng trong {o.DuplicateWindow.TotalHours:0} giờ."));

        var captionShingles = Shingles(p.Content.Caption, o.CaptionShingleSize);
        if (captionShingles.Count == 0) return;
        var captionAccounts = peers
            .Where(x => Jaccard(captionShingles, Shingles(x.Content.Caption, o.CaptionShingleSize)) >= o.CaptionJaccardThreshold)
            .Select(x => x.AccountId).Distinct().Count();
        if (captionAccounts > 0)
            w.Add(new(p.PublicationId, RiskLevel.Warning, "DUP_CAPTION_MULTI_ACCOUNT",
                $"Caption gần trùng với bài của {captionAccounts} tài khoản khác cùng nền tảng trong {o.DuplicateWindow.TotalHours:0} giờ."));
    }

    private static void ContentFlags(PlannedPublication p, List<RiskWarning> w)
    {
        if (p.Content.IsAiGenerated && !p.Content.AiDisclosed)
            w.Add(new(p.PublicationId, RiskLevel.Warning, "AI_UNDECLARED", "Nội dung tạo bằng AI nhưng chưa khai báo; nhiều nền tảng yêu cầu khai báo."));
        if (p.Content.IsReupload)
            w.Add(new(p.PublicationId, RiskLevel.Warning, "REUPLOAD_COPYRIGHT", "Nội dung từ nguồn đăng lại (reup), có rủi ro bản quyền; cần xác nhận quyền sử dụng."));
    }

    private static bool SameVideo(RiskContent a, RiskContent b, RiskOptions o)
    {
        if (a.ContentId == b.ContentId) return true;
        if (a.Sha256 is not null && string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
        return a.Signature is { } sa && b.Signature is { } sb && VideoSignature.Similarity(sa, sb) >= o.VideoSimilarityThreshold;
    }

    /// <summary>Bỏ dấu (kể cả đ), hạ chữ thường, chỉ giữ chữ/số; để caption khác biệt về dấu câu/hoa thường vẫn tính là trùng.</summary>
    internal static string NormalizeCaption(string caption)
    {
        var sb = new StringBuilder(caption.Length);
        foreach (var ch in caption.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            sb.Append(c == 'đ' ? 'd' : char.IsLetterOrDigit(c) ? c : ' ');
        }
        return sb.ToString();
    }

    internal static HashSet<string> Shingles(string? caption, int size)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(caption)) return set;
        var words = NormalizeCaption(caption).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return set;
        if (words.Length <= size)
        {
            set.Add(string.Join(' ', words));
            return set;
        }
        for (var i = 0; i + size <= words.Length; i++) set.Add(string.Join(' ', words, i, size));
        return set;
    }

    internal static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return inter / (double)(a.Count + b.Count - inter);
    }

    private static bool Precedes(PlannedPublication x, PlannedPublication y) =>
        x.ScheduledUtc != y.ScheduledUtc ? x.ScheduledUtc < y.ScheduledUtc : string.CompareOrdinal(x.PublicationId, y.PublicationId) < 0;

    private static DateOnly LocalDate(TimeZoneInfo tz, DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, tz).DateTime);

    private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static TimeSpan? Max(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : a.Value > b.Value ? a : b;
}
