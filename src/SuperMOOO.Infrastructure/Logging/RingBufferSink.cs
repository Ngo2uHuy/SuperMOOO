using Serilog.Core;
using Serilog.Events;

namespace SuperMOOO.Infrastructure.Logging;

public sealed record LogLine(DateTimeOffset At, LogEventLevel Level, string Message);

/// <summary>Giữ N dòng log gần nhất trong bộ nhớ cho màn Nhật ký, không phải đọc lại file.</summary>
public sealed class RingBufferSink(int capacity = 2000) : ILogEventSink
{
    private readonly Queue<LogLine> _lines = new(capacity);
    private readonly object _gate = new();

    public event EventHandler<LogLine>? Emitted;

    public void Emit(LogEvent logEvent)
    {
        var line = new LogLine(logEvent.Timestamp, logEvent.Level, logEvent.RenderMessage());
        lock (_gate)
        {
            if (_lines.Count == capacity) _lines.Dequeue();
            _lines.Enqueue(line);
        }

        Emitted?.Invoke(this, line);
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return _lines.ToList();
    }
}
