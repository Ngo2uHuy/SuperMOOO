using System.IO;
namespace SuperMOOO.App.Controls;

/// <summary>Hướng dẫn từng màn: file `Help\Sxx.txt` nhúng trong app; màn nào chưa có thì báo chưa có.</summary>
[Service]
public sealed class HelpService(IDialogService dialogs)
{
    public string Get(string screenId)
    {
        var assembly = typeof(HelpService).Assembly;
        using var stream = assembly.GetManifestResourceStream($"SuperMOOO.App.Help.{screenId.ToUpperInvariant()}.txt");
        if (stream is null) return "Chưa có hướng dẫn cho màn này.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public Task ShowAsync(string screenId) => dialogs.ShowTextAsync($"Hướng dẫn {screenId}", Get(screenId));
}
