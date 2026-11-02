namespace SuperMOOO.Core.Models;

// Mỗi class là một section của ISettingsStore; chuỗi rỗng/null = dùng mặc định.
public sealed class PathsSettings
{
    public string? OutputRoot { get; set; }
}

public sealed class ToolsSettings
{
    public string? Ffmpeg { get; set; }
    public string? Ffprobe { get; set; }
    public string? YtDlp { get; set; }
    public string? NativeTtsServer { get; set; }
    public string? Python { get; set; }
}

public sealed class UiSettings
{
    public string Theme { get; set; } = "System";    // System | Light | Dark
}

public sealed class BackgroundSettings
{
    public bool RunInBackground { get; set; }   // bật thì đóng cửa sổ → ẩn xuống khay thay vì thoát
    public bool HintShown { get; set; }
}
