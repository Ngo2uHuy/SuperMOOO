using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.Publishing.Library;

/// <param name="Level">Warning khi có rủi ro cần xem lại, Info khi không có gì đáng chú ý.</param>
public sealed record LibraryRiskLine(RiskLevel Level, string Message);

/// <summary>
/// Cảnh báo rủi ro ngay trong kho (S21), dùng cùng ngưỡng với <see cref="RiskAssessor"/> lúc đăng: trùng/gần trùng video (sha256 + hash khung hình),
/// caption gần trùng, nội dung AI, reup. Chỉ chạy khi người dùng bấm kiểm tra (chữ ký khung hình cần ffmpeg).
/// </summary>
[Service]
public sealed class LibraryRiskService(IContentRepository contents, IContentSignatureRepository signatures, ContentSignatureService signatureService)
{
    private const int MaxListed = 5;

    public async Task<IReadOnlyList<LibraryRiskLine>> CheckAsync(string contentId, CancellationToken ct)
    {
        var detail = await contents.GetAsync(contentId, ct);
        if (detail is null) return [new(RiskLevel.Warning, "Nội dung không còn trong kho.")];
        var item = detail.Item;
        var lines = new List<LibraryRiskLine>();
        if (item.IsAiGenerated) lines.Add(new(RiskLevel.Warning, "Nội dung tạo bằng AI: khai báo AI khi đăng (YouTube/TikTok/Meta yêu cầu)."));
        if (item.IsReupload) lines.Add(new(RiskLevel.Warning, "Nội dung đăng lại (reup): có rủi ro bản quyền, cần xác nhận quyền sử dụng."));

        var options = RiskAssessor.Options;
        var others = (await contents.ListReadyAsync(ct)).Where(c => c.Id != item.Id).ToList();

        await signatureService.EnsureAsync([item.Id], ct);
        var sigs = await signatures.ListAsync(ct);
        if (sigs.TryGetValue(item.Id, out var mine))
        {
            var own = new VideoSignature(mine);
            var similar = others.Where(c => sigs.TryGetValue(c.Id, out var h) && VideoSignature.Similarity(own, new VideoSignature(h)) >= options.VideoSimilarityThreshold).ToList();
            if (similar.Count > 0) lines.Add(new(RiskLevel.Warning, $"Video gần trùng {similar.Count} video khác trong kho: {Titles(similar)}."));
        }
        else lines.Add(new(RiskLevel.Info, "Chưa kiểm tra được trùng khung hình (thiếu ffmpeg hoặc chưa đọc được thời lượng); chỉ so được sha256."));

        var shingles = PublishRiskAdvisor.Shingles(item.Caption, options.CaptionShingleSize);
        if (shingles.Count > 0)
        {
            var same = others.Where(c => PublishRiskAdvisor.Jaccard(shingles, PublishRiskAdvisor.Shingles(c.Caption, options.CaptionShingleSize)) >= options.CaptionJaccardThreshold).ToList();
            if (same.Count > 0) lines.Add(new(RiskLevel.Warning, $"Caption gần trùng {same.Count} video khác: {Titles(same)}."));
        }

        if (lines.Count == 0) lines.Add(new(RiskLevel.Info, "Không thấy video hoặc caption trùng trong kho."));
        return lines;
    }

    private static string Titles(IReadOnlyList<FinishedContent> items) =>
        string.Join("; ", items.Take(MaxListed).Select(c => $"\"{(c.Title.Length > 40 ? c.Title[..40] + "…" : c.Title)}\"")) + (items.Count > MaxListed ? "…" : "");
}
