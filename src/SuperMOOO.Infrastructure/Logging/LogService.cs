using System.Text;
using Serilog.Events;

namespace SuperMOOO.Infrastructure.Logging;

/// <summary>Đọc/xoá/lưu bộ đệm nhật ký trong bộ nhớ (file log thật trên đĩa không bị xoá).</summary>
[Service]
public sealed class LogService(RingBufferSink ring)
{
    public IReadOnlyList<LogLine> Tail(int count = 200, LogEventLevel minimum = LogEventLevel.Information) =>
        ring.Snapshot().Where(l => l.Level >= minimum).TakeLast(count).ToList();

    public void ClearBuffer() => ring.Clear();

    public async Task ExportAsync(string destination, CancellationToken ct = default)
    {
        var text = string.Join(Environment.NewLine, ring.Snapshot().Select(l => $"{l.At:yyyy-MM-dd HH:mm:ss.fff} [{l.Level}] {l.Message}"));
        await File.WriteAllTextAsync(destination, text, new UTF8Encoding(true), ct);
    }
}
