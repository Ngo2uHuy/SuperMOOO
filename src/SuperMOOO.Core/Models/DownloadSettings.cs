namespace SuperMOOO.Core.Models;

// Section "download"; DownloadDir rỗng = <OutputRoot>\download.
public sealed class DownloadSettings
{
    public string? DownloadDir { get; set; }
    public DownloadFormat Format { get; set; } = DownloadFormat.Best;
    public bool NoWatermark { get; set; } = true;
}
