namespace SuperMOOO.Core.Abstractions;

public interface INetworkRouter
{
    RouteSnapshot Current { get; }
    HttpMessageHandler CreateHandler();          // mọi HTTP ra ngoài (cloud, crawl, publish) đi qua đây
    string? ProxyUriForTools();                  // --proxy cho yt-dlp; proxy cho Playwright
    Task<IpCheckResult> VerifyAsync(CancellationToken ct);
    event EventHandler<RouteSnapshot>? Changed;
}

public interface IHttpClientProvider { HttpClient Get(string name); }   // tạo lại khi route đổi

public enum ToolKind { Ffmpeg, Ffprobe, YtDlp, NativeTtsServer, Python }
public interface IToolLocator { string Resolve(ToolKind tool); bool TryResolve(ToolKind tool, out string path); }

public interface IProcessRunner { Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? lines, CancellationToken ct); } // ArgumentList, Job Object, huỷ = kill cây
public interface IMediaProbe { Task<MediaInfo> ProbeAsync(string path, CancellationToken ct); }  // ffprobe JSON; lỗi → MediaProbeException
public interface INotifier { Task SendAsync(string message, CancellationToken ct); }               // Telegram
public interface ICapabilityReporter { string Area { get; } Task<CapabilityReport> ReportAsync(CancellationToken ct); }
