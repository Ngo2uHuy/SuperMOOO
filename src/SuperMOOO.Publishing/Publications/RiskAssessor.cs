using System.Globalization;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Http;

namespace SuperMOOO.Publishing.Publications;

/// <param name="AiDisclosed">Bài có khai báo nội dung AI (ContainsSyntheticMedia).</param>
public sealed record RiskItem(
    string PublicationId, string DestinationId, string ConnectionId, DateTimeOffset At, FinishedContent Content, string? Description, bool AiDisclosed);

/// <summary>
/// Map Domain → record của PublishRiskAdvisor. Mỗi kênh đích là một "tài khoản" trên nền tảng của nó (Meta: mỗi Page/Instagram riêng,
/// giới hạn theo PlatformLimits của đúng nền tảng); các đích chung một kết nối là một nhóm khi xét dùng chung proxy.
/// Giới hạn bài/ngày + khoảng cách theo tài khoản (chỉ chặt hơn PlatformLimits), lịch sử bài đang chiếm chỗ, ngắt mạch,
/// lỗi quyền chưa xử lý, chữ ký video (nếu đã tính), cờ AI/reup của nội dung. Dùng chung cho tạo tay, planner, duyệt và đổi lịch.
/// </summary>
[Service]
public sealed class RiskAssessor(
    IPublicationRepository publications,
    IConnectionRepository connections,
    IDestinationRepository destinations,
    ICircuitBreaker breaker,
    IActionRequiredRepository actions,
    IPublishSettingsStore settings,
    IContentSignatureRepository signatures,
    SuperMOOO.Publishing.Library.ContentSignatureService signatureService)
{
    public static readonly RiskOptions Options = new();

    public async Task<IReadOnlyList<RiskWarning>> AssessAsync(IReadOnlyList<RiskItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var from = items.Min(p => p.At) - Options.DuplicateWindow - TimeSpan.FromDays(1);
        var to = items.Max(p => p.At) + Options.DuplicateWindow + TimeSpan.FromDays(1);
        var ids = items.Select(p => p.PublicationId).ToHashSet();
        // Chữ ký chỉ tính cho video sắp đăng (lúc đánh giá), không tính hàng loạt lúc nhập.
        await signatureService.EnsureAsync(items.Select(p => p.Content.Id), ct);
        var sigs = await signatures.ListAsync(ct);
        VideoSignature? Sig(string contentId) => sigs.TryGetValue(contentId, out var h) ? new VideoSignature(h) : null;

        var history = (await publications.ListForRiskAsync(from, to, ct)).Where(h => !ids.Contains(h.PublicationId))
            .Select(h => new PostedPublication(h.DestinationId.Length > 0 ? h.DestinationId : h.ConnectionId, h.AtUtc,
                new RiskContent(h.ContentItemId, h.VideoSha256, Sig(h.ContentItemId), h.Description, false, true, false)))
            .ToList();

        var defaultMax = await ReadIntAsync(SettingKeys.PublishMaxPerDay, ct);
        var defaultGap = await ReadIntAsync(SettingKeys.PublishMinGapMinutes, ct);
        var openAuth = (await actions.ListOpenByKindAsync(ActionKinds.ConnectionNeedsReauth, ct)).Select(a => a.RefId).ToHashSet();
        var conns = (await connections.ListAsync(ct)).ToDictionary(c => c.Id);
        var accounts = (await destinations.ListAsync(null, true, ct)).Where(d => conns.ContainsKey(d.ConnectionId)).Select(d =>
        {
            var c = conns[d.ConnectionId];
            var decision = breaker.Peek(c.Platform.ToString(), c.Id);
            var state = decision.Allowed ? decision.State == CircuitState.HalfOpen ? BreakerState.HalfOpen : BreakerState.Closed : BreakerState.Open;
            var gap = c.MinGapMinutes ?? defaultGap;
            return new RiskAccount(d.Id, ToKind(d.Platform), TimeZoneInfo.Local, c.RouteProfileId, null, false, state,
                openAuth.Contains(c.Id), c.MaxPostsPerDay ?? defaultMax, gap is null ? null : TimeSpan.FromMinutes(gap.Value), c.Id);
        }).ToList();

        var plans = items.Select(p => new PlannedPublication(p.PublicationId, p.DestinationId, p.At,
            new RiskContent(p.Content.Id, p.Content.VideoSha256, Sig(p.Content.Id), p.Description,
                p.Content.IsAiGenerated, p.AiDisclosed, p.Content.IsReupload))).ToList();
        return PublishRiskAdvisor.Assess(plans, history, accounts, Options).Where(w => w.Level != RiskLevel.Info).ToList();
    }

    private async Task<int?> ReadIntAsync(string key, CancellationToken ct) =>
        int.TryParse(await settings.GetAsync(key, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    public static PlatformKind ToKind(SocialPlatform p) => p switch
    {
        SocialPlatform.YouTube => PlatformKind.YouTube,
        SocialPlatform.Facebook => PlatformKind.Facebook,
        SocialPlatform.Instagram => PlatformKind.Instagram,
        SocialPlatform.TikTok => PlatformKind.TikTok,
        _ => PlatformKind.Other
    };

    /// <summary>Giới hạn hiệu lực của một kênh: chặt nhất giữa PlatformLimits của nền tảng kênh, cấu hình chung và cấu hình riêng của tài khoản.</summary>
    public async Task<(int MaxPerDay, TimeSpan MinGap)> LimitsAsync(SocialConnection c, SocialPlatform platform, CancellationToken ct)
    {
        var limit = PlatformLimits.Defaults.GetValueOrDefault(ToKind(platform)) ?? PlatformLimits.Defaults[PlatformKind.Other];
        var max = c.MaxPostsPerDay ?? await ReadIntAsync(SettingKeys.PublishMaxPerDay, ct);
        var gap = c.MinGapMinutes ?? await ReadIntAsync(SettingKeys.PublishMinGapMinutes, ct);
        return (Math.Min(limit.MaxPostsPerDay, max ?? int.MaxValue),
            TimeSpan.FromMinutes(Math.Max(limit.MinGap.TotalMinutes, gap ?? 0)));
    }
}
