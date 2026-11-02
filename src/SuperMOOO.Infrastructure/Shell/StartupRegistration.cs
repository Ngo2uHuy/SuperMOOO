using Microsoft.Win32;

namespace SuperMOOO.Infrastructure.Shell;

/// <summary>Khởi động cùng Windows (HKCU\...\Run), chạy ở chế độ --background để chỉ hiện icon khay.</summary>
[Service]
public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SuperMOOO";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ResolveLaunchExe()}\" --background");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Cài qua Velopack thì exe đang chạy nằm trong thư mục "current"; stub gốc ở thư mục cha luôn trỏ tới bản mới nhất sau cập nhật.</summary>
    public static string ResolveLaunchExe()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Không xác định được đường dẫn ứng dụng.");
        var dir = Path.GetDirectoryName(exe);
        var parent = dir is null ? null : Directory.GetParent(dir)?.FullName;
        if (parent is not null && string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(parent, "Update.exe")))
        {
            var stub = Path.Combine(parent, Path.GetFileName(exe));
            if (File.Exists(stub)) return stub;
        }

        return exe;
    }
}
