namespace SuperMOOO.Core;

public sealed class AppPaths
{
    private AppPaths(string dataRoot, string appRoot)
    {
        DataRoot = dataRoot;
        AppRoot = appRoot;
    }

    public string DataRoot { get; }
    public string AppRoot { get; }
    public string ToolsDir => Path.Combine(AppRoot, "tools");
    public string Database => Path.Combine(DataRoot, "aivs.db");
    public string Logs => Path.Combine(DataRoot, "logs");
    public string Cache => Path.Combine(DataRoot, "cache");
    public string Voices => Path.Combine(DataRoot, "voices");
    public string Models => Path.Combine(DataRoot, "models");
    public string WorkerVenv => Path.Combine(DataRoot, "worker", "venv");
    public string Pipelines => Path.Combine(DataRoot, "pipelines");
    public string Legacy => Path.Combine(DataRoot, "legacy");
    public string Backups => Path.Combine(DataRoot, "backups");
    public string RestorePending => Path.Combine(DataRoot, "restore-pending");

    public static string DefaultDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuperMOOO.Data");

    public static string DefaultOutputRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SuperMOOO");

    public static AppPaths Create(string? dataRoot = null, string? appRoot = null) =>
        new(Path.GetFullPath(string.IsNullOrWhiteSpace(dataRoot) ? ResolveDefaultDataRoot() : dataRoot),
            appRoot ?? ResolveAppRoot(AppContext.BaseDirectory));

    // Chạy từ bin (dotnet run / F5) không có tools\ và ai_worker\ như bản đóng gói: dùng thư mục gốc repo chứa SuperMOOO.slnx.
    private static string ResolveAppRoot(string baseDirectory)
    {
        if (Directory.Exists(Path.Combine(baseDirectory, "tools"))) return baseDirectory;
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SuperMOOO.slnx"))) return dir.FullName;
        return baseDirectory;
    }

    private static string ResolveDefaultDataRoot()
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIVideoStudio.Data");
        if (Directory.Exists(DefaultDataRoot) || !Directory.Exists(legacy)) return DefaultDataRoot;
        // App trước đây tên AIVideoStudio: chuyển cả thư mục (cùng ổ nên tức thì) để giữ model, giọng, cài đặt.
        try { Directory.Move(legacy, DefaultDataRoot); return DefaultDataRoot; }
        // Tiến trình cũ còn giữ file thì dùng tạm thư mục cũ, lần chạy sau chuyển lại.
        catch (IOException) { return legacy; }
        catch (UnauthorizedAccessException) { return legacy; }
    }

    public string CacheDir(string name) => Path.Combine(Cache, name);

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataRoot, Logs, CacheDir("tts"), CacheDir("preview"), CacheDir("frames"), CacheDir("dl-tmp"),
                     Voices, Path.Combine(Models, "asr"), Path.Combine(Models, "native_tts"), Path.Combine(Models, "denoise"),
                     Path.Combine(Models, "rvc"), Pipelines, Legacy })
            Directory.CreateDirectory(dir);
    }
}
