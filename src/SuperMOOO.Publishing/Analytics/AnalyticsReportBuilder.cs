using System.Globalization;
using SuperMOOO.Publishing.Contracts;

namespace SuperMOOO.Publishing.Analytics;

/// <summary>
/// Dựng báo cáo từ điểm dữ liệu thô. Quy tắc cốt lõi (PLAN 3.9): không cộng metric chéo nền tảng (mỗi dòng thuộc đúng một nền tảng),
/// dữ liệu chưa có hiện lý do (NoData/MissingPermission/TokenExpired...) và không bao giờ thành 0.
/// Giá trị một dòng = số mới nhất đến hết kỳ; kỳ trước = cùng độ dài ngay trước kỳ này; mỗi số kèm thời điểm lấy và nguồn.
/// </summary>
public static class AnalyticsReportBuilder
{
    public const string MixedSources = "nhiều nguồn";

    private sealed record Item(string Id, IReadOnlyList<MetricPoint> Points);

    private sealed record Group(string Kind, string Key, string Label, SocialPlatform Platform, List<Item> PostItems, List<Item> ChannelItems);

    public static AnalyticsReportDto Build(
        AnalyticsReportRequest request, IReadOnlyList<MetricDefinition> definitions, IReadOnlyList<PostInfo> posts,
        IReadOnlyList<DestinationInfoRow> destinations, IReadOnlyList<MetricPoint> points, DateTimeOffset now)
    {
        var from = request.FromUtc;
        var to = request.ToUtc;
        var length = to - from;
        var prevFrom = from - length;
        var window = points.Where(p => p.CapturedUtc >= prevFrom && p.CapturedUtc <= to).ToList();
        var byPost = window.Where(p => p.PublicationId is not null).ToLookup(p => p.PublicationId!);
        var byDestination = window.Where(p => p.PublicationId is null).ToLookup(p => p.DestinationId);

        Item PostItem(PostInfo p) => new(p.PublicationId, [.. byPost[p.PublicationId]]);
        Item ChannelItem(DestinationInfoRow d) => new(d.DestinationId, [.. byDestination[d.DestinationId]]);

        var groups = BuildGroups(request, posts, destinations, PostItem, ChannelItem);
        var rows = new List<AnalyticsRowDto>();
        foreach (var g in groups)
        {
            var platform = g.Platform.ToString();
            foreach (var d in definitions.Where(d => d.Platform == platform))
            {
                var items = d.Scope == MetricScope.Post ? g.PostItems : g.ChannelItems;
                if (items.Count == 0) continue;
                rows.Add(Row(g, d, items, from, prevFrom));
            }
        }

        var chartDefs = definitions.Where(d => rows.Any(r => r.Platform == d.Platform && r.MetricKey == d.Key && r.Status == MetricCellStatuses.Ok)).ToList();
        var chartDef = (request.ChartPlatform is not null && request.ChartMetricKey is not null
                           ? chartDefs.FirstOrDefault(d => d.Platform == request.ChartPlatform && d.Key == request.ChartMetricKey) : null)
                       ?? chartDefs.FirstOrDefault();
        return new AnalyticsReportDto(from, to, prevFrom, now, rows,
            chartDef is null ? null : Chart(chartDef, posts, destinations, byPost, byDestination, from, to),
            [.. chartDefs.Select(ToDto)]);
    }

    private static List<Group> BuildGroups(AnalyticsReportRequest request, IReadOnlyList<PostInfo> posts, IReadOnlyList<DestinationInfoRow> destinations,
        Func<PostInfo, Item> postItem, Func<DestinationInfoRow, Item> channelItem)
    {
        var groups = new List<Group>();
        var postFilterActive = request.CampaignId is not null || request.SourceId is not null || request.PublicationId is not null;
        switch (request.GroupBy)
        {
            case AnalyticsGroupBy.Post:
                foreach (var p in posts.OrderBy(p => p.PublishedUtc))
                    groups.Add(new Group(AnalyticsGroupBy.Post, p.PublicationId, $"{p.Title} · {p.DestinationTitle}", p.Platform, [postItem(p)], []));
                break;
            case AnalyticsGroupBy.Channel:
                foreach (var d in destinations.OrderBy(d => d.Title, StringComparer.CurrentCultureIgnoreCase))
                {
                    var own = posts.Where(p => p.DestinationId == d.DestinationId).Select(postItem).ToList();
                    // Lọc theo chiến dịch/nguồn/bài chỉ giữ kênh có bài khớp.
                    if (postFilterActive && own.Count == 0) continue;
                    groups.Add(new Group(AnalyticsGroupBy.Channel, d.DestinationId, d.Title, d.Platform, own, [channelItem(d)]));
                }
                break;
            case AnalyticsGroupBy.Platform:
                foreach (var platform in posts.Select(p => p.Platform).Concat(destinations.Select(d => d.Platform)).Distinct().Order())
                {
                    var channels = postFilterActive ? [] : destinations.Where(d => d.Platform == platform).Select(channelItem).ToList();
                    groups.Add(new Group(AnalyticsGroupBy.Platform, platform.ToString(), platform.ToString(), platform,
                        [.. posts.Where(p => p.Platform == platform).Select(postItem)], channels));
                }
                break;
            case AnalyticsGroupBy.Campaign:
                foreach (var g in posts.GroupBy(p => (p.CampaignId ?? "", p.Platform)).OrderBy(g => g.First().CampaignName ?? "~").ThenBy(g => g.Key.Platform))
                    groups.Add(new Group(AnalyticsGroupBy.Campaign, g.Key.Item1, $"{g.First().CampaignName ?? "(Không thuộc chiến dịch)"} · {g.Key.Platform}",
                        g.Key.Platform, [.. g.Select(postItem)], []));
                break;
            case AnalyticsGroupBy.Source:
                foreach (var g in posts.GroupBy(p => (p.SourceId ?? "", p.Platform)).OrderBy(g => g.First().SourceName ?? "~").ThenBy(g => g.Key.Platform))
                    groups.Add(new Group(AnalyticsGroupBy.Source, g.Key.Item1, $"{g.First().SourceName ?? "(Nhập tay / không rõ nguồn)"} · {g.Key.Platform}",
                        g.Key.Platform, [.. g.Select(postItem)], []));
                break;
            default:
                throw new ArgumentException("Nhóm báo cáo không hợp lệ.");
        }
        return groups;
    }

    private sealed record Latest(MetricPoint? Ok, MetricPoint? Any, MetricPoint? Previous);

    private static Latest LatestOf(Item item, string key, DateTimeOffset from)
    {
        var pts = item.Points.Where(p => p.MetricKey == key).OrderBy(p => p.CapturedUtc).ToList();
        var ok = pts.LastOrDefault(p => p.Status == MetricStatus.Ok);
        var any = pts.LastOrDefault();
        // Kỳ trước: số Ok mới nhất trước đầu kỳ này, và phải là điểm khác với số hiện tại (không tự so với chính nó).
        var prev = pts.LastOrDefault(p => p.Status == MetricStatus.Ok && p.CapturedUtc < from && !ReferenceEquals(p, ok));
        return new Latest(ok, any, prev);
    }

    private static AnalyticsRowDto Row(Group g, MetricDefinition d, List<Item> items, DateTimeOffset from, DateTimeOffset prevFrom)
    {
        var latest = items.Select(i => LatestOf(i, d.Key, from)).ToList();
        var covered = latest.Where(l => l.Ok is not null).ToList();

        if (covered.Count == 0)
        {
            var (status, detail) = Reason(latest);
            return new AnalyticsRowDto(g.Kind, g.Key, g.Label, d.Platform, d.Key, d.DisplayName, d.Unit, null, null, null, status, null, null, detail, 0, items.Count);
        }

        if (d.Aggregation == MetricAggregation.None && items.Count > 1)
            return new AnalyticsRowDto(g.Kind, g.Key, g.Label, d.Platform, d.Key, d.DisplayName, d.Unit, null, null, null, MetricCellStatuses.NotAggregable,
                covered.Min(l => l.Ok!.CapturedUtc), SourceOf(covered), "Không cộng được giữa nhiều bài/kênh (số người duy nhất bị trùng).", covered.Count, items.Count);

        double Agg(IEnumerable<double> values) => d.Aggregation == MetricAggregation.Mean ? values.Average() : values.Sum();
        var value = Agg(covered.Select(l => l.Ok!.Value!.Value));
        var withPrev = latest.Where(l => l.Previous is not null).ToList();
        double? previous = withPrev.Count > 0 ? Agg(withPrev.Select(l => l.Previous!.Value!.Value)) : null;
        var both = latest.Where(l => l.Ok is not null && l.Previous is not null).ToList();
        double? delta = both.Count > 0 ? Agg(both.Select(l => l.Ok!.Value!.Value)) - Agg(both.Select(l => l.Previous!.Value!.Value)) : null;

        var notes = new List<string>();
        if (covered.Count < items.Count) notes.Add($"{covered.Count}/{items.Count} có số liệu.");
        // Lần đọc mới hơn số Ok mà thất bại: vẫn hiện số cũ nhưng nói rõ nó có thể đã cũ.
        var stale = covered.Where(l => l.Any!.CapturedUtc > l.Ok!.CapturedUtc && l.Any.Status != MetricStatus.Ok).Select(l => l.Any!).FirstOrDefault();
        if (stale is not null) notes.Add($"Lần đọc {stale.CapturedUtc:yyyy-MM-dd HH:mm}Z không thành công ({StatusText(stale.Status)}{(stale.Detail is null ? "" : ": " + stale.Detail)}).");
        return new AnalyticsRowDto(g.Kind, g.Key, g.Label, d.Platform, d.Key, d.DisplayName, d.Unit, value, previous, delta, MetricCellStatuses.Ok,
            // Nhiều bài: lấy thời điểm cũ nhất để không phóng đại độ mới của tổng.
            covered.Min(l => l.Ok!.CapturedUtc), SourceOf(covered), notes.Count == 0 ? null : string.Join(' ', notes), covered.Count, items.Count);
    }

    private static string SourceOf(List<Latest> covered) =>
        covered.Select(l => l.Ok!.Source).Distinct().Count() == 1 ? covered[0].Ok!.Source : MixedSources;

    /// <summary>Không có số Ok nào: ưu tiên lý do cần người dùng hành động (token, quyền) trước lý do chờ/không có.</summary>
    private static (string Status, string Detail) Reason(List<Latest> latest)
    {
        var any = latest.Select(l => l.Any).Where(p => p is not null).Select(p => p!).ToList();
        if (any.Count == 0) return (MetricCellStatuses.NoData, "Chưa có dữ liệu: chưa đồng bộ lần nào.");
        foreach (var status in new[] { MetricStatus.TokenExpired, MetricStatus.MissingPermission, MetricStatus.NotYetAvailable, MetricStatus.NotAvailable })
            if (any.FirstOrDefault(p => p.Status == status) is { } p)
                return (status.ToString(), p.Detail ?? DefaultReason(status));
        return (MetricCellStatuses.NoData, "Chưa có dữ liệu.");
    }

    public static string DefaultReason(MetricStatus status) => status switch
    {
        MetricStatus.TokenExpired => "Token hết hạn: kết nối lại tài khoản.",
        MetricStatus.MissingPermission => "Thiếu quyền: cấp thêm quyền rồi kết nối lại tài khoản.",
        MetricStatus.NotYetAvailable => "Nền tảng chưa có số liệu (bài còn mới).",
        MetricStatus.NotAvailable => "Nền tảng không cung cấp số liệu này.",
        _ => ""
    };

    private static string StatusText(MetricStatus status) => status switch
    {
        MetricStatus.TokenExpired => "token hết hạn",
        MetricStatus.MissingPermission => "thiếu quyền",
        MetricStatus.NotYetAvailable => "chưa có số liệu",
        MetricStatus.NotAvailable => "không có số liệu",
        _ => "ok"
    };

    private static AnalyticsChartDto Chart(MetricDefinition d, IReadOnlyList<PostInfo> posts, IReadOnlyList<DestinationInfoRow> destinations,
        ILookup<string, MetricPoint> byPost, ILookup<string, MetricPoint> byDestination, DateTimeOffset from, DateTimeOffset to)
    {
        var platform = Enum.Parse<SocialPlatform>(d.Platform);
        var series = d.Scope == MetricScope.Post
            ? posts.Where(p => p.Platform == platform).Select(p => byPost[p.PublicationId].Where(x => x.MetricKey == d.Key && x.Status == MetricStatus.Ok)
                .OrderBy(x => x.CapturedUtc).ToList()).ToList()
            : destinations.Where(x => x.Platform == platform).Select(x => byDestination[x.DestinationId].Where(y => y.MetricKey == d.Key && y.Status == MetricStatus.Ok)
                .OrderBy(y => y.CapturedUtc).ToList()).ToList();
        series = [.. series.Where(s => s.Count > 0)];

        var result = new List<ChartPointDto>();
        for (var day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
        {
            var end = new DateTimeOffset(day.AddDays(1), TimeSpan.Zero);
            var values = series.Select(s => s.LastOrDefault(p => p.CapturedUtc < end)).Where(p => p is not null).Select(p => p!.Value!.Value).ToList();
            double? value = values.Count == 0 || (d.Aggregation == MetricAggregation.None && series.Count > 1) ? null
                : d.Aggregation == MetricAggregation.Mean ? values.Average() : values.Sum();
            result.Add(new ChartPointDto(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value));
        }
        return new AnalyticsChartDto(d.Platform, d.Key, d.DisplayName, d.Unit, result);
    }

    public static MetricDefinitionDto ToDto(MetricDefinition d) => new(d.Platform, d.Key, d.Scope.ToString(), d.DisplayName, d.Unit, d.Description,
        d.SourceEndpoint, d.RequiredScope, d.Verified);
}
