namespace SuperMOOO.Core.Models;

// Section "jobs": số job chạy song song mỗi lane.
public sealed class JobsSettings
{
    public int Media { get; set; } = 1;
    public int Network { get; set; } = 3;
    public int AiWorker { get; set; } = 1;
    public int Cloud { get; set; } = 2;
    public int Publish { get; set; } = 2;
}

// Section "network": hồ sơ proxy đã lưu (mật khẩu ở ISecretStore, scope network:<id>).
public sealed class NetworkSettings
{
    public List<ProxyProfile> Profiles { get; set; } = [];
    public List<NetworkRoute> Routes { get; set; } = [];
    public List<string> Favorites { get; set; } = [];
}

// Section "cache": TTL và trần dung lượng của CacheJanitor; JobHistoryDays: số ngày giữ lịch sử tác vụ đã kết thúc (JobHistoryJanitor, ≤ 0 = không xoá).
public sealed class CacheSettings
{
    public int TtlDays { get; set; } = 7;
    public int MaxMb { get; set; } = 2048;
    public int JobHistoryDays { get; set; } = 60;
}

// Section "pipeline.defaults": key = Kind của step, value = options mặc định.
public sealed class PipelineDefaultsSettings : Dictionary<string, JsonObject>;

public sealed record SystemStats(int CpuCores, long TotalMemoryMb, long ProcessMemoryMb, long DataDriveFreeMb, string Os);
public sealed record HealthReport(IReadOnlyList<CapabilityItem> Items)
{
    public bool Ok => Items.All(i => i.Available);
}
