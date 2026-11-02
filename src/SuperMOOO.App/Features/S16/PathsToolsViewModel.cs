using System.Collections.ObjectModel;

namespace SuperMOOO.App.Features.S16;

public sealed partial class ToolRow(ToolKind kind, string label) : ObservableObject
{
    public ToolKind Kind => kind;
    public string Label => label;

    [ObservableProperty]
    public partial string? Path { get; set; }

    [ObservableProperty]
    public partial string Resolved { get; set; } = "";
}

[Service(ServiceLifetime.Transient)]
public sealed partial class PathsToolsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IToolLocator _locator;

    public PathsToolsViewModel(ISettingsStore settings, IToolLocator locator, AppPaths paths)
    {
        _settings = settings;
        _locator = locator;
        DataRoot = paths.DataRoot;

        var tools = settings.Get<ToolsSettings>("tools");
        Tools =
        [
            new(ToolKind.Ffmpeg, "FFmpeg") { Path = tools.Ffmpeg },
            new(ToolKind.Ffprobe, "FFprobe") { Path = tools.Ffprobe },
            new(ToolKind.YtDlp, "yt-dlp") { Path = tools.YtDlp },
            new(ToolKind.NativeTtsServer, "audiocpp_server (TTS local)") { Path = tools.NativeTtsServer },
            new(ToolKind.Python, "python (AI worker)") { Path = tools.Python }
        ];
        OutputRoot = settings.Get<PathsSettings>("paths").OutputRoot ?? AppPaths.DefaultOutputRoot;
        Refresh();
    }

    public string DataRoot { get; }
    public ObservableCollection<ToolRow> Tools { get; }

    [ObservableProperty]
    public partial string? OutputRoot { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        string? Value(ToolKind kind) => Tools.First(t => t.Kind == kind).Path is { } p && !string.IsNullOrWhiteSpace(p) ? p.Trim() : null;

        await _settings.SetAsync("tools", new ToolsSettings
        {
            Ffmpeg = Value(ToolKind.Ffmpeg),
            Ffprobe = Value(ToolKind.Ffprobe),
            YtDlp = Value(ToolKind.YtDlp),
            NativeTtsServer = Value(ToolKind.NativeTtsServer),
            Python = Value(ToolKind.Python)
        });
        await _settings.SetAsync("paths", new PathsSettings { OutputRoot = string.IsNullOrWhiteSpace(OutputRoot) ? null : OutputRoot.Trim() });
        Refresh();
        Status = "Đã lưu.";
    }

    [RelayCommand]
    private void Refresh()
    {
        foreach (var tool in Tools)
            tool.Resolved = _locator.TryResolve(tool.Kind, out var path) ? path : "Chưa tìm thấy";
    }
}
