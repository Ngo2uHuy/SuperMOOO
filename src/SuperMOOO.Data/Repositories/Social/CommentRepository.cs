using SuperMOOO.Data.Sqlite;

namespace SuperMOOO.Data.Repositories.Social;

[Service(As = [typeof(ICommentStore)])]
public sealed class CommentRepository(IDbConnectionFactory factory) : ICommentStore
{
    private sealed class CampaignRow
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string Url { get; set; } = "";
        public string? Platform { get; set; }
        public string? ContentType { get; set; }
        public int CommentCount { get; set; }
        public int PositiveCount { get; set; }
        public int NegativeCount { get; set; }
        public int NeutralCount { get; set; }
        public int LeadCount { get; set; }
        public string? Summary { get; set; }
        public long CreatedAt { get; set; }
    }

    private sealed class CommentRow
    {
        public long Id { get; set; }
        public string? Author { get; set; }
        public string? ProfileUrl { get; set; }
        public string? Text { get; set; }
        public int? Likes { get; set; }
        public string? Created { get; set; }
        public string? Sentiment { get; set; }
        public int? HasPhone { get; set; }
        public int? HasEmail { get; set; }
        public int? IsLead { get; set; }
        public string? LeadType { get; set; }
    }

    private const string CampaignColumns = """
        id, title, url, platform, content_type, comment_count, positive_count, negative_count, neutral_count, lead_count, summary, created_at
        """;

    public async Task<long> SaveCampaignAsync(CommentCampaign campaign, IReadOnlyList<CommentRecord> comments, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await using var tx = await db.BeginTransactionAsync(ct);
        var id = await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO comment_campaign(title, url, platform, content_type, comment_count, positive_count, negative_count,
                neutral_count, lead_count, summary, created_at)
            VALUES (@Title, @Url, @Platform, @ContentType, @CommentCount, @PositiveCount, @NegativeCount, @NeutralCount,
                @LeadCount, @Summary, @now)
            RETURNING id
            """,
            new
            {
                campaign.Title, campaign.Url, campaign.Platform, campaign.ContentType, campaign.CommentCount, campaign.PositiveCount,
                campaign.NegativeCount, campaign.NeutralCount, campaign.LeadCount, campaign.Summary, now = DapperSetup.NowMs
            }, tx, cancellationToken: ct));

        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO comment(campaign_id, author, profile_url, text, likes, created, sentiment, has_phone, has_email, is_lead, lead_type)
            VALUES (@id, @Author, @ProfileUrl, @Text, @Likes, @Created, @Sentiment, @phone, @email, @lead, @LeadType)
            """,
            comments.Select(c => new
            {
                id, c.Author, c.ProfileUrl, c.Text, c.Likes, c.Created, c.Sentiment, c.LeadType,
                phone = c.HasPhone ? 1 : 0, email = c.HasEmail ? 1 : 0, lead = c.IsLead ? 1 : 0
            }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<CommentCampaign>> ListCampaignsAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<CampaignRow>(new CommandDefinition(
            $"SELECT {CampaignColumns} FROM comment_campaign ORDER BY created_at DESC, id DESC LIMIT @limit",
            new { limit }, cancellationToken: ct));
        return rows.Select(ToCampaign).ToList();
    }

    public async Task<CommentCampaign?> GetCampaignAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var row = await db.QuerySingleOrDefaultAsync<CampaignRow>(new CommandDefinition(
            $"SELECT {CampaignColumns} FROM comment_campaign WHERE id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : ToCampaign(row);
    }

    public async Task<IReadOnlyList<CommentRecord>> ListCommentsAsync(long campaignId, bool leadsOnly = false, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<CommentRow>(new CommandDefinition("""
            SELECT id, author, profile_url, text, likes, created, sentiment, has_phone, has_email, is_lead, lead_type
            FROM comment WHERE campaign_id = @campaignId AND (@leadsOnly = 0 OR is_lead = 1) ORDER BY id
            """, new { campaignId, leadsOnly = leadsOnly ? 1 : 0 }, cancellationToken: ct));
        return rows.Select(r => new CommentRecord(r.Author ?? "", r.ProfileUrl, r.Text ?? "", r.Likes ?? 0, r.Created,
            r.Sentiment ?? "neutral", r.HasPhone == 1, r.HasEmail == 1, r.IsLead == 1, r.LeadType, r.Id)).ToList();
    }

    public async Task<bool> DeleteCampaignAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM comment_campaign WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    private static CommentCampaign ToCampaign(CampaignRow r) => new(r.Title, r.Url, r.Platform, r.ContentType, r.CommentCount,
        r.PositiveCount, r.NegativeCount, r.NeutralCount, r.LeadCount, r.Summary, DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAt), r.Id);
}
