namespace SuperMOOO.Core.Models;

public sealed record CommentRecord(string Author, string? ProfileUrl, string Text, int Likes, string? Created,
    string Sentiment, bool HasPhone, bool HasEmail, bool IsLead, string? LeadType, long Id = 0);

public sealed record CommentCampaign(string? Title, string Url, string? Platform, string? ContentType, int CommentCount,
    int PositiveCount, int NegativeCount, int NeutralCount, int LeadCount, string? Summary, DateTimeOffset CreatedAt, long Id = 0);

public sealed record WatchTarget(string Name, string Url, string Platform, string TargetType, int FrequencyHours, string Status,
    long? ContentItemId, DateTimeOffset? LastCheckedAt, DateTimeOffset CreatedAt, long Id = 0);

public sealed record MetricSample(string Platform, string EntityKey, string? EntityTitle, string? Url, string Metric, double Value,
    DateTimeOffset RecordedAt, long? ContentItemId = null, long Id = 0);

public sealed record MetricEntity(string Platform, string EntityKey, string? EntityTitle);

public sealed record AuditEntry(string Source, string Action, string? Target, string Status, int? RecordsCount,
    DateTimeOffset CreatedAt, long Id = 0);

public sealed record CrawlRunInfo(string Id, string Platform, string RunType, string? Query, string Status, int ItemCount,
    string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
