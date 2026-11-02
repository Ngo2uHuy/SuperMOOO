namespace SuperMOOO.Core.Publishing;

public sealed class ContentSource
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; }
    public string RootPath { get; set; } = "";
    public ImportMode ImportMode { get; set; }
    public bool Recursive { get; set; }
    public bool WatchEnabled { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public static ImportMode DefaultModeFor(SourceKind kind) =>
        kind == SourceKind.VideoC ? ImportMode.VideoCMeta : ImportMode.LooseFiles;
}

public sealed class FinishedContent
{
    public string Id { get; init; } = "";
    public string? SourceId { get; init; }
    public string? SourceContentId { get; init; }
    public string? SourceJobId { get; init; }
    public string Title { get; init; } = "";
    public string? Caption { get; init; }
    public string? Language { get; init; }
    public string TagsJson { get; init; } = "[]";
    public long? DurationMs { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string VideoSha256 { get; init; } = "";
    public ContentStatus Status { get; init; } = ContentStatus.Ready;
    public DateTimeOffset ImportedUtc { get; init; }
    public string OriginalPath { get; init; } = "";
    /// <summary>Nội dung tạo bằng AI (mặc định bật cho nguồn AIVideoStudio/VideoC; người dùng sửa được).</summary>
    public bool IsAiGenerated { get; init; }
    /// <summary>Manifest ghi "origin": "reup": nội dung đăng lại từ nơi khác, rủi ro bản quyền.</summary>
    public bool IsReupload { get; init; }
    /// <summary>requestId trong manifest: nội dung được sản xuất theo yêu cầu của chiến dịch (S22) (null = không đến từ yêu cầu).</summary>
    public string? RequestId { get; init; }
}

public sealed class MediaAsset
{
    public string Id { get; init; } = "";
    public string ContentItemId { get; init; } = "";
    public MediaKind Kind { get; init; }
    public string StoredPath { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public string OriginalPath { get; init; } = "";
}

public sealed class ActionRequired
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string RefId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? ResolvedUtc { get; init; }
}

public sealed class SocialConnection
{
    public string Id { get; init; } = "";
    public SocialPlatform Platform { get; set; }
    public string DisplayName { get; set; } = "";
    public string HomeUrl { get; set; } = "";
    /// <summary>Tuyến mạng riêng của tài khoản (hồ sơ proxy/VPN); null = tuyến chung của app. Chừa cho VPN theo tài khoản.</summary>
    public string? RouteProfileId { get; set; }
    /// <summary>Chính sách IP (JSON: quốc gia cho phép, bắt buộc IP cố định...); null = không kiểm.</summary>
    public string? IpPolicyJson { get; set; }
    public ApiStatus ApiStatus { get; set; } = ApiStatus.NotConnected;
    /// <summary>Nhà cấp OAuth (vd "Google"); null khi chưa kết nối API.</summary>
    public string? Provider { get; set; }
    /// <summary>Định danh tài khoản phía nền tảng (YouTube: channel id) để chặn kết nối lại nhầm tài khoản khác.</summary>
    public string? ExternalAccountId { get; set; }
    public string? Scopes { get; set; }
    /// <summary>Hạn access token (token nằm trong ITokenVault, không bao giờ trong entity).</summary>
    public DateTimeOffset? TokenExpiresUtc { get; set; }
    public string? StatusDetail { get; set; }
    /// <summary>Giới hạn riêng tài khoản; chỉ có thể chặt hơn PlatformLimits.</summary>
    public int? MaxPostsPerDay { get; set; }
    public int? MinGapMinutes { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public static string TokenKeyFor(string connectionId) => "oauth-token:" + connectionId;

    /// <summary>Token riêng của một đích (Meta: Page token, dùng chung cho Page và Instagram liên kết).</summary>
    public static string DestinationTokenKeyFor(string connectionId, string tokenRef) => $"oauth-dest-token:{connectionId}:{tokenRef}";
}
