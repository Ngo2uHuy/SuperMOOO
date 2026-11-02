using Serilog;
using Serilog.Events;

namespace SuperMOOO.Infrastructure.Logging;

public static class SerilogSetup
{
    public static (Serilog.Core.Logger Logger, RingBufferSink Ring) Create(AppPaths paths)
    {
        var ring = new RingBufferSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.Logs, "app-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, shared: true)
            // File riêng chỉ cảnh báo/lỗi, giữ lâu hơn: giám sát lỗi không phải lọc qua log thông tin, và kèm nguồn để biết module nào lỗi.
            .WriteTo.File(Path.Combine(paths.Logs, "errors-.log"), restrictedToMinimumLevel: LogEventLevel.Warning,
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 60, shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(ring)
            .CreateLogger();
        return (logger, ring);
    }
}
