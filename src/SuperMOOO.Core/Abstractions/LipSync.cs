namespace SuperMOOO.Core.Abstractions;

public sealed record LipSyncRequest(string AvatarVideoPath, string AudioPath, string OutputPath);
public sealed record LipSyncResult(string Path, TimeSpan Duration);

/// <summary>Missing: tên các thành phần còn thiếu (python, venv, mã, weights, ffmpeg) để màn hình báo cụ thể.</summary>
public sealed record LipSyncStatus(bool Ready, IReadOnlyList<string> Missing, string Message);

/// <summary>Seconds: ước tính thời gian dựng; FaceDetectionNeeded: avatar chưa có toạ độ mặt cho độ dài này nên lần đầu chậm hơn.</summary>
public sealed record LipSyncEstimate(double Seconds, bool FaceDetectionNeeded);

/// <summary>Khớp môi video avatar theo audio (MuseTalk ở GĐ4); chưa có runtime thì Live chỉ phát TTS trên nền video lặp.</summary>
public interface ILipSyncEngine
{
    string Id { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct);
    LipSyncStatus GetStatus();
    LipSyncEstimate Estimate(string avatarVideoPath, double audioSeconds);
    /// <summary>Dò mặt trước cho avatar (chạy một lần, lưu cạnh avatar) để các lần dựng sau bỏ bước chậm nhất.</summary>
    Task PrepareAvatarAsync(string avatarVideoPath, double forSeconds, IProgress<string>? progress, CancellationToken ct);
    Task<LipSyncResult> RenderAsync(LipSyncRequest request, IProgress<string>? progress, CancellationToken ct);
}

public sealed class LipSyncUnavailableException(string message) : InvalidOperationException(message);
