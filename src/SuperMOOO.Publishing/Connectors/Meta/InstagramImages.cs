using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Connectors.MediaHost;

namespace SuperMOOO.Publishing.Connectors.Meta;

public sealed partial class InstagramConnector
{
    private List<ValidationIssue> ValidateImages(PublishVariant v, MediaFile media)
    {
        var issues = MediaFile.ValidateImages(media, 10, 8L * 1024 * 1024, ".jpg", ".jpeg");
        if (mediaHost is null) issues.Add(new("MEDIA_HOST_MISSING", MissingHostMessage));
        var caption = PlatformText.Caption(v);
        if (caption.Length > InstagramConnectorOptions.MaxCaptionChars) issues.Add(new("CAPTION_TOO_LONG", "Caption Instagram tối đa 2200 ký tự."));
        if (PlatformText.HashtagCount(caption) > InstagramConnectorOptions.MaxHashtags) issues.Add(new("HASHTAGS_TOO_MANY", "Instagram cho tối đa 30 hashtag."));
        if (v.Privacy != PublishPrivacy.Public) issues.Add(new("PRIVACY_UNSUPPORTED", "Bài Instagram luôn công khai theo tài khoản."));
        if (v.PublishAtUtc is not null) issues.Add(new("NATIVE_SCHEDULE_UNSUPPORTED", "Ảnh Instagram cần lên lịch bằng app."));
        return issues;
    }

    private async Task<string> CreateMediaContainerAsync(ConnectorSession session, string igId, MediaFile media, PublishVariant variant, CancellationToken ct)
    {
        if (!media.IsImagePost)
        {
            var mime = Path.GetExtension(media.Path).Equals(".mov", StringComparison.OrdinalIgnoreCase) ? "video/quicktime" : "video/mp4";
            var lease = await mediaHost!.PublishAsync(S3CompatibleMediaHost.StableKey(session.ConnectionId, media), media, mime, options.MediaUrlLifetime, ct);
            return await CreateContainerAsync(session, igId, lease.Url, variant, ct);
        }
        var children = new List<string>();
        foreach (var image in media.Images!)
        {
            var lease = await mediaHost!.PublishAsync(S3CompatibleMediaHost.StableKey(session.ConnectionId, image), image, image.ImageMime, options.MediaUrlLifetime, ct);
            var fields = new List<(string, string)> { ("image_url", lease.Url.AbsoluteUri) };
            if (media.Images.Count > 1) fields.Add(("is_carousel_item", "true"));
            else fields.Add(("caption", PlatformText.Caption(variant)));
            var json = await MetaGraphApi.PostFormAsync(session, $"{MetaGraphApi.Api}/{igId}/media", options.ApiTimeout, Name, ct, fields.ToArray());
            var id = ConnectorHttp.Str(json?["id"]) ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Instagram không trả id ảnh.");
            if (media.Images.Count == 1) return id;
            var status = await WaitContainerAsync(session, id, ct);
            if (status.Code != "FINISHED") throw new ConnectorException(ConnectorErrorKind.Validation, "Instagram không xử lý được ảnh trong carousel.");
            children.Add(id);
        }
        var parent = await MetaGraphApi.PostFormAsync(session, $"{MetaGraphApi.Api}/{igId}/media", options.ApiTimeout, Name, ct,
            ("media_type", "CAROUSEL"), ("children", string.Join(',', children)), ("caption", PlatformText.Caption(variant)));
        return ConnectorHttp.Str(parent?["id"]) ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Instagram không trả id carousel.");
    }

    private async Task CleanupMediaAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys) await CleanupAsync(key);
    }
}
