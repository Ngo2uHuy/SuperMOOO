using System.Diagnostics;

namespace SuperMOOO.Infrastructure.Shell;

[Service]
public sealed class ShellService
{
    public void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Thư mục không tồn tại: {folder}");
        StartExplorer(folder);
    }

    public void RevealFile(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException($"File không tồn tại: {file}", file);
        StartExplorer("/select," + file);
    }

    public void OpenFile(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException($"File không tồn tại: {file}", file);
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
    }

    // Chỉ http/https: tránh mở scheme lạ (file:, ms-*:) từ dữ liệu crawl.
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"URL không hợp lệ: {url}", nameof(url));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    private static void StartExplorer(string argument)
    {
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(argument);
        Process.Start(psi)?.Dispose();
    }
}
