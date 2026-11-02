using System.Text.Json;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Contracts;

namespace SuperMOOO.Publishing.Analytics;

/// <summary>
/// Lập job đồng bộ thống kê: chọn bài đến hạn theo <see cref="SyncCadence"/> (bài mới dày, bài cũ thưa) trong ngân sách ngày của từng nền tảng,
/// thêm snapshot kênh mỗi ngày, và gộp snapshot quá hạn thành daily_aggregates. Chỉ xếp job (lane Network, ưu tiên thấp); việc gọi mạng nằm ở
/// <see cref="AnalyticsSyncJobHandler"/>.
/// </summary>
[Service]
public sealed class AnalyticsPlanner(
    IAnalyticsRepository analytics,
    IConnectionRepository connections,
    IJobQueue jobs,
    IQuotaLedger ledger,
    IPublishSettingsStore settings,
    IClock clock,
    AnalyticsOptions options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCompact = DateTimeOffset.MinValue;

    public async Task<AnalyticsSyncResultDto> RunAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.UtcNow;
            await CompactIfDueAsync(now, ct);
            return await PlanAsync(now, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task CompactIfDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (now - _lastCompact < options.CompactEvery) return;
        _lastCompact = now;
        var days = await AnalyticsSettingsStore.RetentionDaysAsync(settings, options, ct);
        await analytics.CompactAsync(now - TimeSpan.FromDays(days), ct);
    }

    private async Task<AnalyticsSyncResultDto> PlanAsync(DateTimeOffset now, CancellationToken ct)
    {
        var conns = (await connections.ListAsync(ct)).ToDictionary(c => c.Id);
        var destinations = (await analytics.ListDestinationsAsync(ct)).Where(d => !d.Removed && conns.ContainsKey(d.ConnectionId)).ToDictionary(d => d.DestinationId);
        var busy = await analytics.ListDestinationsWithPendingSyncAsync(ct);
        var lastChannel = await analytics.LastChannelSyncAsync(ct);
        var candidates = (await analytics.ListSyncCandidatesAsync(now, ct))
            .Where(c => destinations.ContainsKey(c.DestinationId) && !busy.Contains(c.DestinationId)).ToList();

        var postsByDestination = new Dictionary<string, List<string>>();
        foreach (var platformGroup in candidates.GroupBy(c => c.Platform))
        {
            var budget = await AnalyticsSettingsStore.BudgetAsync(settings, options, platformGroup.Key, ct);
            var used = await ledger.GetUsedAsync(AnalyticsSettingsStore.LedgerKey(platformGroup.Key), AnalyticsSettingsStore.DayStart(now), AnalyticsSettingsStore.DayEnd(now), ct);
            var due = SyncCadence.SelectDue(
                platformGroup.Select(c => new SyncItem(c.PublicationId, c.PublishedUtc, c.LastSyncedUtc)), now, Math.Max(0, budget - used), options.Cadence);
            var byId = platformGroup.ToDictionary(c => c.PublicationId);
            foreach (var item in due)
            {
                var destinationId = byId[item.PublicationId].DestinationId;
                if (!postsByDestination.TryGetValue(destinationId, out var list)) postsByDestination[destinationId] = list = [];
                list.Add(item.PublicationId);
            }
        }

        var queued = 0;
        var posts = 0;
        foreach (var destination in destinations.Values.Where(d => !busy.Contains(d.DestinationId)))
        {
            if (queued >= options.MaxJobsPerRun) break;
            var ids = postsByDestination.GetValueOrDefault(destination.DestinationId) ?? [];
            var channelDue = !lastChannel.TryGetValue(destination.DestinationId, out var last) || now - last >= options.ChannelInterval;
            if (ids.Count == 0 && !channelDue) continue;

            // Kênh luôn đi kèm job đầu tiên của đích; đích chỉ có snapshot kênh vẫn có một job. Các job cùng tài khoản chạy lần lượt (khoá đồng thời).
            var chunks = ids.Chunk(Math.Max(1, options.MaxPostsPerJob)).ToList();
            if (chunks.Count == 0) chunks.Add([]);
            for (var i = 0; i < chunks.Count && queued < options.MaxJobsPerRun; i++)
            {
                var chunk = chunks[i];
                var payload = new AnalyticsJobPayload(destination.ConnectionId, destination.Platform.ToString(), destination.DestinationId,
                    [.. chunk], IncludeChannel: i == 0 && channelDue, ConcurrencyKey: "analytics-account:" + destination.ConnectionId);
                await jobs.EnqueueAsync(new JobRequest(PublishJobTypes.AnalyticsSync, $"Thống kê {destination.Platform}: {destination.Title}",
                    JsonSerializer.SerializeToNode(payload, PublishingJson.Options)!, JobLane.Network, Priority: options.JobPriority,
                    MaxAttempts: options.JobMaxAttempts, Owner: JobOwner.Screen("S14")), ct);
                queued++;
                posts += chunk.Length;
            }
        }
        return new AnalyticsSyncResultDto(queued, posts);
    }
}
