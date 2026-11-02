using System.Text;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Connectors.Meta;
using SuperMOOO.Publishing.Connectors.TikTok;
using SuperMOOO.Publishing.Connectors.YouTube;
using SuperMOOO.Publishing.Contracts;

namespace SuperMOOO.App.Features.S14;

public sealed partial class ComposeViewModel
{
    public string CharacterSummary
    {
        get
        {
            var selected = Targets.Where(t => t.IsSelected).ToList();
            if (selected.Count == 0) return $"{Description.Length:N0} ký tự chú thích · chọn kênh để xem giới hạn nội dung.";
            var lines = new List<string>();
            foreach (var group in selected.GroupBy(t => t.Destination.Platform))
            {
                var lengths = new List<int>();
                foreach (var target in group)
                {
                    var caption = PlatformCaptions.First(p => p.Platform == group.Key);
                    foreach (var contentTitle in SelectedMedia.Select(m => m.Title).DefaultIfEmpty(""))
                    {
                        var title = FirstText(target.Title, caption.Title, Title, contentTitle);
                        var description = FirstText(target.Description, caption.Description, Description);
                        var tags = SplitTags(FirstText(target.TagsText, caption.TagsText, TagsText));
                        lengths.Add(group.Key == "YouTube" ? Encoding.UTF8.GetByteCount(description)
                            : Caption(title, description, tags).Length);
                    }
                }
                var limit = TextLimit(group.Key);
                var size = lengths.Max();
                var unit = group.Key == "YouTube" ? "byte UTF-8 mô tả" : "ký tự caption";
                lines.Add($"{group.Key}: {size:N0}{(limit is { } max ? $" / {max:N0}" : "")} {unit}{(size > limit ? " — vượt giới hạn" : "")}");
            }
            return string.Join(" · ", lines) + ". Tính cả tiêu đề/hashtag trong caption; biến thể được kiểm tra chính xác khi xem trước.";
        }
    }

    private static int? TextLimit(string platform) => platform switch
    {
        "Instagram" => InstagramConnectorOptions.MaxCaptionChars,
        "TikTok" => TikTokConnector.MaxTitleChars,
        "YouTube" => YouTubeConnector.MaxDescriptionBytes,
        _ => null
    };

    private static string[] SplitTags(string text) => text.Split([',', '\n', '#'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Caption(string title, string description, IReadOnlyList<string> tags) =>
        PlatformText.Caption(new PublishVariant(title, description, tags, PublishPrivacy.Private, false, false, null));

    private static void ValidateText(TargetViewModel target, PublicationTargetRequest item)
    {
        var platform = target.Destination.Platform;
        var title = item.Title ?? "";
        var description = item.Description ?? "";
        var caption = Caption(title, description, item.Tags ?? []);
        var length = platform == "YouTube" ? Encoding.UTF8.GetByteCount(description) : caption.Length;
        if (TextLimit(platform) is { } limit && length > limit)
            throw new InvalidOperationException($"{target.Display}: nội dung dài {length:N0}/{limit:N0} {(platform == "YouTube" ? "byte UTF-8" : "ký tự")}. Rút gọn nội dung riêng của kênh/nền tảng rồi xem trước lại.");
        if (platform == "YouTube" && title.Trim().Length > YouTubeConnector.MaxTitleChars)
            throw new InvalidOperationException($"{target.Display}: tiêu đề vượt {YouTubeConnector.MaxTitleChars} ký tự.");
        if (platform == "Instagram" && PlatformText.HashtagCount(caption) > InstagramConnectorOptions.MaxHashtags)
            throw new InvalidOperationException($"{target.Display}: tối đa {InstagramConnectorOptions.MaxHashtags} hashtag.");
    }
}
