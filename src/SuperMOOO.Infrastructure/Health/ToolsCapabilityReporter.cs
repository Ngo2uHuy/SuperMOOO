namespace SuperMOOO.Infrastructure.Health;

public sealed class ToolsCapabilityReporter(IToolLocator tools) : ICapabilityReporter
{
    public string Area => "Công cụ";

    public Task<CapabilityReport> ReportAsync(CancellationToken ct) =>
        Task.FromResult(new CapabilityReport(Area, Enum.GetValues<ToolKind>()
            .Select(t => tools.TryResolve(t, out var path) ? new CapabilityItem(t.ToString(), true, path) : new CapabilityItem(t.ToString(), false, "Chưa tìm thấy"))
            .ToList()));
}
