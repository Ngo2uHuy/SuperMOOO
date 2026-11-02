using System.Text.Json.Nodes;
using SuperMOOO.Publishing.Connectors.MediaHost;

namespace SuperMOOO.Publishing.Connectors.TikTok;

public sealed partial class TikTokConnector
{
    private List<ValidationIssue> ValidateImages(PublishVariant v, MediaFile media)
    {
        var issues = MediaFile.ValidateImages(media, 35, 20L * 1024 * 1024, ".jpg", ".jpeg", ".webp");
        if (mediaHost is null) issues.Add(new("MEDIA_HOST_MISSING", "Ảnh TikTok cần media host HTTPS. Cấu hình host và xác minh domain/prefix URL trong TikTok Developer Portal."));
        if (v.Title.Length > 90) issues.Add(new("TITLE_TOO_LONG", "Tiêu đề bài ảnh TikTok tối đa 90 ký tự."));
        if (PhotoDescription(v).Length > 4000) issues.Add(new("DESCRIPTION_TOO_LONG", "Mô tả bài ảnh TikTok tối đa 4000 ký tự."));
        if (v.PublishAtUtc is not null) issues.Add(new("NATIVE_SCHEDULE_UNSUPPORTED", "Ảnh TikTok cần lên lịch bằng app."));
        return issues;
    }

    private static string PhotoDescription(PublishVariant variant) => PlatformText.Caption(variant with { Title = "" });

    private async Task<UploadOutcome> UploadImagesAsync(ConnectorSession session, PublishVariant variant, MediaFile media, UploadState state,
        IUploadCheckpoint checkpoint, CancellationToken ct)
    {
        if (state.SessionUri is { Length: > 0 } publishId) return new UploadOutcome.Completed(publishId);
        if (state.FinalChunkSent) return new UploadOutcome.Ambiguous("Đã gửi bài ảnh TikTok nhưng chưa lưu được publish_id. Kiểm tra TikTok trước khi đăng lại.");
        if (mediaHost is null) throw new ConnectorException(ConnectorErrorKind.Permission, "Chưa cấu hình media host cho ảnh TikTok.");
        var direct = settings is null ? null : await settings.GetAsync(session.ConnectionId, variant, ct);
        var post = new JsonObject { ["title"] = variant.Title, ["description"] = PhotoDescription(variant) };
        if (direct is not null)
        {
            CheckDirectPost(direct);
            var creator = await QueryCreatorInfoAsync(session, ct);
            if (!creator.PrivacyLevelOptions.Contains(direct.PrivacyLevel))
                throw new ConnectorException(ConnectorErrorKind.Validation, "Quyền riêng tư đã chọn không còn được tài khoản TikTok cho phép.", reason: "privacy_level_option_mismatch");
            post["privacy_level"] = direct.PrivacyLevel;
            post["disable_comment"] = !direct.AllowComment || creator.CommentDisabled;
            post["brand_content_toggle"] = direct.CommercialContent && direct.BrandedContent;
            post["brand_organic_toggle"] = direct.CommercialContent && direct.YourBrand;
            post["auto_add_music"] = false;
        }
        var urls = new JsonArray();
        foreach (var image in media.Images!)
        {
            var lease = await mediaHost.PublishAsync(S3CompatibleMediaHost.StableKey(session.ConnectionId, image), image, image.ImageMime, TimeSpan.FromHours(2), ct);
            urls.Add(lease.Url.AbsoluteUri);
        }
        var body = new JsonObject
        {
            ["media_type"] = "PHOTO", ["post_mode"] = direct is null ? "MEDIA_UPLOAD" : "DIRECT_POST",
            ["post_info"] = post, ["is_aigc"] = variant.ContainsSyntheticMedia,
            ["source_info"] = new JsonObject { ["source"] = "PULL_FROM_URL", ["photo_cover_index"] = 0, ["photo_images"] = urls }
        };
        await checkpoint.BeforeCreateSessionAsync(ct);
        await checkpoint.VerifyRouteAsync(ct);
        // Với PULL_FROM_URL, init đã bắt đầu đăng. Ghi mốc trước request để crash/timeout không tự tạo bài thứ hai.
        await checkpoint.MarkFinalChunkSendingAsync(ct);
        try
        {
            var data = await TikTokApi.PostJsonAsync(session, "/post/publish/content/init/", body, options.ApiTimeout, ct);
            var id = ConnectorHttp.Str(data["publish_id"]);
            if (id is null) return new UploadOutcome.Ambiguous("TikTok không trả publish_id cho bài ảnh; kiểm tra tài khoản trước khi đăng lại.");
            await checkpoint.SaveSessionAsync(id, media.SizeBytes, ct);
            await checkpoint.SaveProgressAsync(media.SizeBytes, ct);
            // TikTok tải URL bất đồng bộ; bucket lifecycle dọn file sau khi URL hết hạn.
            return new UploadOutcome.Completed(id);
        }
        catch (ConnectorException e) when (e.IsRetryable || e.Kind == ConnectorErrorKind.BadResponse)
        {
            return new UploadOutcome.Ambiguous("Mất phản hồi khởi tạo bài ảnh TikTok; kiểm tra tài khoản trước khi đăng lại.");
        }
    }
}
