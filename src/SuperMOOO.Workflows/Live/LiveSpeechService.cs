using SuperMOOO.AI.Tts;

namespace SuperMOOO.Workflows.Live;

/// <summary>Đọc câu trả lời/lời thoại bằng TTS của app (mặc định VieNeu) ra file WAV trong cache\live.</summary>
[Service]
public sealed class LiveSpeechService(TtsService tts, ISettingsStore settings, AppPaths paths)
{
    private const int KeepFiles = 40;

    public async Task<string> SpeakAsync(string text, CancellationToken ct)
    {
        var voice = settings.Get<LiveSettings>("live").VoiceId;
        var dir = paths.CacheDir("live");
        Directory.CreateDirectory(dir);
        var result = await tts.SynthesizeAsync(new TtsRequest(text, voice, Path.Combine(dir, $"say-{Guid.NewGuid():N}.wav"), "vi"), ct);
        Prune(dir);
        return result.Path;
    }

    // Mỗi câu trả lời một file; giữ vài chục file gần nhất cho trang phát và nút nghe lại.
    private static void Prune(string dir)
    {
        try
        {
            foreach (var old in new DirectoryInfo(dir).GetFiles("say-*.wav").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles))
                old.Delete();
        }
        catch (IOException) { /* file đang được phát; lần sau dọn tiếp */ }
        catch (UnauthorizedAccessException) { }
    }
}
