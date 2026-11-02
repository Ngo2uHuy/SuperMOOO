namespace SuperMOOO.Publishing.Algorithms;

public enum PlatformKind { YouTube, Facebook, Instagram, TikTok, Other }

public sealed record PlatformLimit(int MaxPostsPerDay, TimeSpan MinGap);

/// <summary>
/// Bảng trần mặc định theo nền tảng. Đây là CẤU HÌNH bảo thủ để điều chỉnh, không phải sự thật:
/// cần xác minh với tài liệu nền tảng (PLAN mục 4) trước khi dựa vào.
/// </summary>
public static class PlatformLimits
{
    public static IReadOnlyDictionary<PlatformKind, PlatformLimit> Defaults { get; } = new Dictionary<PlatformKind, PlatformLimit>
    {
        [PlatformKind.YouTube] = new(6, TimeSpan.FromHours(1)),
        [PlatformKind.Facebook] = new(25, TimeSpan.FromMinutes(15)),
        [PlatformKind.Instagram] = new(25, TimeSpan.FromMinutes(30)),
        [PlatformKind.TikTok] = new(15, TimeSpan.FromMinutes(30)),
        [PlatformKind.Other] = new(10, TimeSpan.FromMinutes(30)),
    };
}
