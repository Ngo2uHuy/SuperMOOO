namespace SuperMOOO.Publishing.Connectors;

/// <summary>
/// Scope chỉ xin khi người dùng bật trong Cài đặt (xin scope chưa được duyệt làm hỏng trang đăng nhập, và đổi scope phải kết nối lại).
/// Dùng chung giữa host cấu hình và OAuth provider; đổi cờ có hiệu lực ở lần kết nối kế tiếp.
/// </summary>
[Service]
public sealed class OptionalScopeFlags
{
    private volatile bool _youTubeAnalytics;
    private volatile bool _metaInsights;

    public bool YouTubeAnalytics { get => _youTubeAnalytics; set => _youTubeAnalytics = value; }
    public bool MetaInsights { get => _metaInsights; set => _metaInsights = value; }
}
