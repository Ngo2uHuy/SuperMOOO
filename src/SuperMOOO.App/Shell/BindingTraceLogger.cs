using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.App.Shell;

/// <summary>`--trace-binding` (dev): ghi lỗi binding WPF vào log app để test UI đọc lại.</summary>
public sealed class BindingTraceLogger(ILogger logger) : TraceListener
{
    public static bool IsRequested => Environment.GetCommandLineArgs().Contains("--trace-binding", StringComparer.OrdinalIgnoreCase);

    public static void Attach(ILogger logger)
    {
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingTraceLogger(logger));
    }

    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) logger.LogWarning("Lỗi binding: {Message}", message);
    }
}
