using LibVLCSharp.Shared;

namespace SuperMOOO.App.Controls;

/// <summary>Một LibVLC dùng chung cho preview và audio; tạo lười để màn không phát media không phải nạp thư viện native.</summary>
[Service]
public sealed class LibVlcHost : IDisposable
{
    private readonly Lazy<LibVLC> _instance = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--no-video-title-show");
    });

    public LibVLC Instance => _instance.Value;

    public void Dispose()
    {
        if (_instance.IsValueCreated) _instance.Value.Dispose();
    }
}
