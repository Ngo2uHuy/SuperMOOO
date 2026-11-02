using SuperMOOO.Infrastructure.Operations;
using SuperMOOO.Social.Operations;

namespace SuperMOOO.App.Features.S10;

public static class CrawlOperationGroups
{
    public static IReadOnlyList<string> All { get; } =
        ["Quét kênh & hồ sơ", "Tìm kiếm & hashtag", "Khám phá & xu hướng", "Bình luận", "Video từ liên kết"];

    public static string For(OperationDescriptor operation) => operation.RequestType switch
    {
        var t when typeof(ChannelRequest).IsAssignableFrom(t) || t == typeof(ScanRequest) => All[0],
        var t when typeof(KeywordRequest).IsAssignableFrom(t) => All[1],
        var t when typeof(ExploreRequest).IsAssignableFrom(t) || t == typeof(DiscoverRequest) => All[2],
        var t when typeof(CommentRequest).IsAssignableFrom(t) => All[3],
        _ => All[4]
    };
}
