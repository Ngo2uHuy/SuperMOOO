using System.IO;
using SuperMOOO.App.Shell;
using Microsoft.Data.Sqlite;
using Velopack;

namespace SuperMOOO.App;

public static class Program
{
    private const string MutexName = @"Local\SuperMOOO.SingleInstance";
    private const string ActivateEventName = @"Local\SuperMOOO.Activate";
    private const string BackgroundArg = "--background";

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().OnBeforeUninstallFastCallback(_ => Features.S16.ComponentSetup.StartUninstallCleanup()).Run();

        var smoke = SmokeRunner.ParseArgs(args);

        // Smoke chạy song song với bản đang mở được, nên không giữ mutex.
        using var mutex = new Mutex(smoke is null, smoke is null ? MutexName : null, out var created);
        if (smoke is null && !created)
        {
            // Bản đang chạy (có thể đang ẩn ở khay) hiện cửa sổ thay cho tiến trình này.
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            signal.Set();
            return 0;
        }

        // Cờ của riêng app: bỏ khỏi args để bộ đọc command-line của host không nuốt tham số kế tiếp làm giá trị.
        var background = smoke is null && args.Contains(BackgroundArg);
        args = [.. args.Where(a => a != BackgroundArg)];
        using var activation = smoke is null ? new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName) : null;

        // Smoke không được đụng dữ liệu thật: dùng DataRoot tạm (trừ khi test đã chỉ định sẵn) và xoá khi xong.
        string? tempRoot = null;
        if (smoke is not null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Paths__DataRoot")))
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "aivs-smoke", Guid.NewGuid().ToString("N"));
            args = [.. args, $"--Paths:DataRoot={tempRoot}"];
        }

        try
        {
            var host = AppHost.Build(args);
            return new App(host, smoke, background, activation).Run();
        }
        finally
        {
            if (tempRoot is not null) DeleteQuietly(tempRoot);
        }
    }

    private static void DeleteQuietly(string dir)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Thư mục tạm trong %TEMP%: còn sót file bị khoá thì để hệ điều hành dọn.
        }
    }
}
