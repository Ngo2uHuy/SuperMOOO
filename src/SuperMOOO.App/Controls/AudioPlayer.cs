using System.IO;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Hosting;

namespace SuperMOOO.App.Controls;

/// <summary>Phát/dừng âm thanh cục bộ; VM phụ thuộc interface này để test không cần LibVLC.</summary>
public interface IAudioPlayer
{
    bool IsPlaying { get; }
    event EventHandler? Stopped;
    void Play(string path);
    void Stop();
}

/// <summary>Nghe thử âm thanh (giọng TTS, BGM); dừng và giải phóng khi app tắt để không còn tiếng sau khi đóng cửa sổ (F29).</summary>
[Service(As = [typeof(IAudioPlayer)])]
public sealed class AudioPlayer : IAudioPlayer, IDisposable
{
    private readonly LibVlcHost _host;
    private MediaPlayer? _player;

    public AudioPlayer(LibVlcHost host, IHostApplicationLifetime lifetime)
    {
        _host = host;
        lifetime.ApplicationStopping.Register(Dispose);
    }

    public bool IsPlaying => _player?.IsPlaying == true;

    public event EventHandler? Stopped;

    public void Play(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Không tìm thấy file âm thanh: {path}", path);

        _player ??= CreatePlayer();
        using var media = new LibVLCSharp.Shared.Media(_host.Instance, path, FromType.FromPath);
        _player.Play(media);
    }

    public void Stop() => _player?.Stop();

    public void Dispose()
    {
        var player = Interlocked.Exchange(ref _player, null);
        if (player is null) return;
        player.Stop();
        player.Dispose();
    }

    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer(_host.Instance);
        player.EndReached += (_, _) => Stopped?.Invoke(this, EventArgs.Empty);
        return player;
    }
}
