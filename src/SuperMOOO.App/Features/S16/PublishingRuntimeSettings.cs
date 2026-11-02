using Microsoft.Extensions.Hosting;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Connectors;

namespace SuperMOOO.App.Features.S16;

/// <summary>Áp băng thông upload và số upload đồng thời (lane Publish) đã lưu ở S16: một lần khi host khởi động và mỗi lần S16 lưu.</summary>
[Service]
public sealed class PublishingRuntimeSettings(IPublishSettingsStore settings, IJobLaneLimits lanes) : IHostedService
{
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        UploadThrottle.SetLimitKbps(Math.Max(0, await IntAsync(SettingKeys.UploadBandwidthKbps, ct)));
        lanes.SetParallel(JobLane.Publish, Math.Max(0, await IntAsync(SettingKeys.ConcurrencyPublish, ct)));
    }

    public Task StartAsync(CancellationToken cancellationToken) => ApplyAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<int> IntAsync(string key, CancellationToken ct) =>
        int.TryParse(await settings.GetAsync(key, ct), out var value) ? value : 0;
}
