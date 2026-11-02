namespace SuperMOOO.Core.Models;

/// <summary>Bảng avatar. FaceStatus: none | ready | failed; FaceSeconds: độ dài đã dò mặt (0 khi chưa dò).</summary>
public sealed record Avatar(string Id, string Name, string SourcePath, string NormalizedPath, string FaceStatus, int FaceSeconds,
    double DurationSec, DateTimeOffset CreatedAt);

public static class AvatarFaceStatus
{
    public const string None = "none";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public static class AvatarLimits
{
    public const long MaxBytes = 250L * 1024 * 1024;
    public const double MaxSeconds = 180;
    public static readonly string[] Extensions = [".mp4", ".mov", ".webm", ".mkv"];
}

// Section "lipsync".
public sealed class LipSyncSettings
{
    public string Python310 { get; set; } = "";      // rỗng = tự tìm `py -3.10`
    public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    public string OutputFolder { get; set; } = "";
    public string AvatarId { get; set; } = "";        // avatar đang chọn ở S19, S20 dùng khi dựng clip
}
