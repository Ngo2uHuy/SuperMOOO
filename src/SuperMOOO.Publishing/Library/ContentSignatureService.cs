using Microsoft.Extensions.Logging;

namespace SuperMOOO.Publishing.Library;

/// <summary>
/// Tính chữ ký khung hình (dHash 8 khung) cho nội dung chưa có, chỉ khi cần: lúc đánh giá rủi ro (tạo/duyệt/đổi lịch bài) hoặc người dùng bấm kiểm tra
/// ở S21. Không tính lúc nhập để khỏi tốn CPU cho video có thể không bao giờ đăng. Thiếu ffmpeg/lỗi thì bỏ qua, cảnh báo gần trùng chỉ yếu đi.
/// </summary>
[Service]
public sealed class ContentSignatureService(IContentRepository contents, IContentSignatureRepository signatures, IFrameSampler sampler, IClock clock,
    ILogger<ContentSignatureService> log)
{
    public const int FrameCount = 8;

    /// <returns>Số chữ ký mới tính được.</returns>
    public async Task<int> EnsureAsync(IEnumerable<string> contentIds, CancellationToken ct)
    {
        var missing = contentIds.Distinct().ToList();
        if (missing.Count == 0) return 0;
        var known = await signatures.ListAsync(ct);
        var added = 0;
        foreach (var id in missing.Where(i => !known.ContainsKey(i)))
        {
            ct.ThrowIfCancellationRequested();
            var detail = await contents.GetAsync(id, ct);
            var video = detail?.Assets.FirstOrDefault(a => a.Kind == MediaKind.Video);
            if (detail is null || video is null || detail.Item.DurationMs is not > 0 || !File.Exists(video.StoredPath)) continue;
            var result = await sampler.SampleAsync(video.StoredPath, detail.Item.DurationMs.Value / 1000.0, FrameCount, ct);
            if (result.Status != FrameSampleStatus.Ok || result.FrameHashes is not { Count: > 0 } hashes)
            {
                log.LogInformation("Bỏ qua chữ ký video {Id}: {Reason}", id, result.Message);
                continue;
            }
            await signatures.SaveAsync(id, hashes, clock.UtcNow, ct);
            added++;
        }
        return added;
    }
}
