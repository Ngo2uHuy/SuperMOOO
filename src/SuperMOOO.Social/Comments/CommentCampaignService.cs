using System.Text;
using SuperMOOO.Social.Crawlers;

namespace SuperMOOO.Social.Comments;

public sealed record CampaignResult(long CampaignId, string Platform, string ContentType, string Title, string Url,
    IReadOnlyList<CommentRecord> Comments, int Positive, int Negative, int Neutral, int LeadCount, IReadOnlyList<KeywordCount> Keywords, string Summary);

/// <summary>Quét bình luận, phân tích cảm xúc/lead, lưu chiến dịch và báo Telegram khi có lead.</summary>
[Service]
public sealed class CommentCampaignService(CommentCrawler crawler, ICommentStore store, INotifier notifier)
{
    public async Task<CampaignResult> CrawlAsync(string url, int maxComments = 5000, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var fetch = await crawler.FetchAsync(url, maxComments, progress, ct);
        return await AnalyzeAndSaveAsync(fetch.Title, url, fetch.Platform, fetch.ContentType, fetch.Comments, ct);
    }

    public async Task<CampaignResult> AnalyzeAndSaveAsync(string title, string url, string platform, string contentType,
        IReadOnlyList<RawComment> raws, CancellationToken ct = default)
    {
        var analytics = LeadAnalyzer.Analyze(raws);
        var summary = LeadAnalyzer.Summary(analytics.Comments, title);
        var campaign = new CommentCampaign(title, url, platform, contentType, analytics.Comments.Count, analytics.Positive, analytics.Negative,
            analytics.Neutral, analytics.LeadCount, summary, DateTimeOffset.UtcNow);
        var id = await store.SaveCampaignAsync(campaign, analytics.Comments, ct);

        if (analytics.LeadCount > 0) await NotifyAsync(title, url, analytics, ct);
        return new CampaignResult(id, platform, contentType, title, url, analytics.Comments, analytics.Positive, analytics.Negative,
            analytics.Neutral, analytics.LeadCount, analytics.Keywords, summary);
    }

    private async Task NotifyAsync(string title, string url, CommentAnalytics a, CancellationToken ct)
    {
        var message = new StringBuilder()
            .AppendLine("Lead alert")
            .AppendLine($"Nội dung: [{title}]({url})")
            .AppendLine($"Tổng bình luận: {a.Comments.Count:N0}")
            .Append($"Khách tiềm năng: {a.LeadCount:N0}").ToString();
        try
        {
            await notifier.SendAsync(message, ct);
        }
        catch (ProviderException)
        {
            // Thông báo chỉ là phụ; kết quả quét đã lưu nên không làm hỏng chiến dịch.
        }
    }

    public Task<IReadOnlyList<CommentCampaign>> ListAsync(int limit = 200, CancellationToken ct = default) => store.ListCampaignsAsync(limit, ct);

    public Task<CommentCampaign?> GetAsync(long id, CancellationToken ct = default) => store.GetCampaignAsync(id, ct);

    public Task<IReadOnlyList<CommentRecord>> GetCommentsAsync(long campaignId, bool leadsOnly = false, CancellationToken ct = default) =>
        store.ListCommentsAsync(campaignId, leadsOnly, ct);

    public Task<bool> DeleteAsync(long id, CancellationToken ct = default) => store.DeleteCampaignAsync(id, ct);
}
