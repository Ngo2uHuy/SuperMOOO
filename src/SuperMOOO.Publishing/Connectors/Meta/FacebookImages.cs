using System.Text.Json;
using System.Text.RegularExpressions;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.Meta;

public sealed partial class FacebookPageConnector
{
    private static List<ValidationIssue> ValidateImages(PublishVariant v, MediaFile media)
    {
        var issues = MediaFile.ValidateImages(media, 10, 10L * 1024 * 1024, ".jpg", ".jpeg", ".png");
        if (v.Privacy != PublishPrivacy.Public) issues.Add(new("PRIVACY_UNSUPPORTED", "Ảnh đăng lên Page phải để Công khai; dùng Lưu nháp để giữ trong app."));
        if (v.PublishAtUtc is not null) issues.Add(new("NATIVE_SCHEDULE_UNSUPPORTED", "Ảnh Page cần lên lịch bằng app."));
        if (PlatformText.Caption(v).Length == 0) issues.Add(new("CAPTION_EMPTY", "Cần tiêu đề hoặc mô tả."));
        return issues;
    }

    private async Task<UploadOutcome> UploadImagesAsync(ConnectorSession session, PublishVariant variant, MediaFile media, UploadState state,
        IUploadCheckpoint checkpoint, CancellationToken ct)
    {
        // /feed không có khóa idempotency: sau khi đã gửi lệnh đăng chỉ cho người dùng đối soát, không gửi lần hai.
        if (state.FinalChunkSent) return new UploadOutcome.Ambiguous("Đã gửi lệnh đăng ảnh Facebook nhưng chưa lưu được kết quả. Kiểm tra Page trước khi đăng lại.");
        var pageId = ConnectorHttp.Str((await ResolvePageAsync(session, ct))["id"])!;
        var uploaded = new List<string>();
        if (state.SessionUri is { } saved)
        {
            if (!saved.StartsWith("photos:", StringComparison.Ordinal))
                throw new ConnectorException(ConnectorErrorKind.Validation, "Phiên đăng ảnh Facebook không hợp lệ.");
            try { uploaded = JsonSerializer.Deserialize<List<string>>(saved[7..]) ?? []; }
            catch (JsonException) { throw new ConnectorException(ConnectorErrorKind.Validation, "Không đọc được phiên đăng ảnh Facebook."); }
        }
        if (uploaded.Count > media.Images!.Count) throw new ConnectorException(ConnectorErrorKind.Validation, "Số ảnh đã thay đổi trong lúc đăng.");
        if (state.SessionUri is null) await checkpoint.BeforeCreateSessionAsync(ct);
        for (var index = uploaded.Count; index < media.Images.Count; index++)
        {
            var image = media.Images[index];
            var json = await MetaGraphApi.CallAsync(session, token =>
            {
                var request = MetaGraphApi.Authorized(HttpMethod.Post, $"{MetaGraphApi.Api}/{pageId}/photos", token);
                var body = new MultipartFormDataContent();
                body.Add(new StringContent("false"), "published");
                body.Add(new FileSliceContent(image.Path, 0, image.SizeBytes, image.ImageMime), "source", Path.GetFileName(image.Path));
                request.Content = body;
                return request;
            }, options.UploadTimeout, Name, ct);
            uploaded.Add(ConnectorHttp.Str(json?["id"]) ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Facebook không trả id ảnh."));
            await checkpoint.SaveSessionAsync("photos:" + JsonSerializer.Serialize(uploaded), media.SizeBytes, ct);
            await checkpoint.SaveProgressAsync(media.Images.Take(uploaded.Count).Sum(i => i.SizeBytes), ct);
        }
        await checkpoint.VerifyRouteAsync(ct);
        await checkpoint.MarkFinalChunkSendingAsync(ct);
        try
        {
            var result = await MetaGraphApi.PostFormAsync(session, $"{MetaGraphApi.Api}/{pageId}/feed", options.ApiTimeout, Name, ct,
                ("message", PlatformText.Caption(variant)), ("attached_media", JsonSerializer.Serialize(uploaded.Select(id => new { media_fbid = id }))));
            var postId = ConnectorHttp.Str(result?["id"]);
            return postId is null ? new UploadOutcome.Ambiguous("Facebook không trả id bài ảnh; kiểm tra Page trước khi đăng lại.")
                : new UploadOutcome.Completed(postId.Contains('_') ? postId : pageId + "_" + postId);
        }
        catch (ConnectorException e) when (e.IsRetryable || e.Kind == ConnectorErrorKind.BadResponse)
        {
            return new UploadOutcome.Ambiguous("Mất phản hồi lệnh đăng ảnh Facebook; kiểm tra Page trước khi đăng lại.");
        }
    }

    private async Task<RemoteStatus> GetImagePostStatusAsync(ConnectorSession session, string remoteId, CancellationToken ct)
    {
        try
        {
            var json = await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{Uri.EscapeDataString(remoteId)}?fields=id,is_published,permalink_url", options.ApiTimeout, Name, ct);
            if (ConnectorHttp.Str(json?["id"]) is null) return new RemoteStatus(RemoteState.NotFound, null, "Không tìm thấy bài ảnh trên Page.");
            return PlatformHttp.Bool(json?["is_published"]) == true
                ? new RemoteStatus(RemoteState.Ready, "published", ConnectorHttp.Str(json?["permalink_url"]))
                : new RemoteStatus(RemoteState.Processing, null, "Bài ảnh chưa được Facebook xuất bản.");
        }
        catch (ConnectorException e) when (MetaGraphApi.IsNotFound(e))
        {
            return new RemoteStatus(RemoteState.NotFound, null, "Không tìm thấy bài ảnh trên Page.");
        }
    }

    [GeneratedRegex(@"^\d{5,25}_\d{5,25}$")]
    private static partial Regex PostIdPattern();
}
