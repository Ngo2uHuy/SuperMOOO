using SuperMOOO.Core;
using SuperMOOO.Core.Abstractions;
using SuperMOOO.Core.Conventions;
using SuperMOOO.Core.Models;
using SuperMOOO.AI.Tts;

namespace SuperMOOO.AI.Audio;

/// <summary>WAV tạm do ffmpeg chuẩn bị; Dispose xoá file (trừ khi dùng thẳng file gốc đã đúng định dạng).</summary>
public sealed class PreparedWav(string path, bool owned = true) : IDisposable
{
    public string Path => path;

    public void Dispose()
    {
        if (!owned) return;
        try { File.Delete(path); }
        catch (IOException) { /* thư mục cache, dọn sau */ }
    }
}

/// <summary>Worker chỉ nhận WAV PCM; mọi định dạng khác đi qua ffmpeg ở đây.</summary>
[Service]
public sealed class WavPreparer(IProcessRunner runner, IToolLocator tools, AppPaths paths)
{
    public async Task<PreparedWav> PrepareAsync(string inputPath, int sampleRate, CancellationToken ct)
    {
        if (IsReady(inputPath, sampleRate)) return new PreparedWav(inputPath, owned: false);
        var dir = paths.CacheDir("worker");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, $"{Guid.NewGuid():N}.wav");
        var result = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.Ffmpeg),
            ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-vn", "-ac", "1", "-ar", sampleRate.ToString(),
                "-c:a", "pcm_s16le", output]), null, ct);
        if (result.ExitCode != 0 || !File.Exists(output))
        {
            File.Delete(output);
            throw new InvalidOperationException($"FFmpeg không chuẩn bị được audio: {string.Join(" | ", result.OutputTail.TakeLast(3))}");
        }

        return new PreparedWav(output);
    }

    private static bool IsReady(string path, int sampleRate)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[(int)Math.Min(stream.Length, 4096)];
            stream.ReadExactly(head);
            var (format, _, length) = WavFile.Parse(head);
            return format == new WavFormat(sampleRate, 1, 16) && length > 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }
}
