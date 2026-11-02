using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.YouTube;

[Service]
public sealed class YouTubeConnectorOptions
{
    public const int ChunkUnit = 256 * 1024;

    /// <summary>Bội số 256 KB (yêu cầu của resumable upload, trừ chunk cuối).</summary>
    public int ChunkSizeBytes { get; set; } = 32 * ChunkUnit;
    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan ChunkTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Độ lệch cho phép quanh khoảng thời gian upload khi đối soát.</summary>
    public TimeSpan ReconcileSlack { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>22 = People &amp; Blogs; bắt buộc có categoryId khi insert.</summary>
    public string CategoryId { get; set; } = "22";

    public static int NormalizeChunkSize(long bytes) => (int)Math.Clamp(bytes / ChunkUnit * ChunkUnit, ChunkUnit, 256L * ChunkUnit);
}

/// <summary>YouTube Data API v3 qua HttpClient (không dùng Google client library).</summary>
public sealed partial class YouTubeConnector(YouTubeConnectorOptions options) : ISocialConnector
{
    public const string Api = "https://www.googleapis.com/youtube/v3";
    public const string UploadApi = "https://www.googleapis.com/upload/youtube/v3";
    public const int MaxTitleChars = 100;
    public const int MaxDescriptionBytes = 5000;
    public const int MaxTagsChars = 500;
    public const long MaxFileBytes = 256L * 1024 * 1024 * 1024;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan LongUploadThreshold = TimeSpan.FromMinutes(15);

    private static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4", [".m4v"] = "video/x-m4v", [".mov"] = "video/quicktime", [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm", [".avi"] = "video/x-msvideo", [".wmv"] = "video/x-ms-wmv", [".flv"] = "video/x-flv",
        [".3gp"] = "video/3gpp", [".mpg"] = "video/mpeg", [".mpeg"] = "video/mpeg"
    };

    public string Platform => nameof(SocialPlatform.YouTube);

    public IReadOnlyList<CapabilityInfo> GetCapabilities(string? grantedScopes)
    {
        var scopes = (grantedScopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CapabilityStatus Need(string scope) => scopes.Contains(scope) ? CapabilityStatus.Supported : CapabilityStatus.MissingPermission;
        var upload = Need(GoogleOAuthProvider.UploadScope);
        var read = Need(GoogleOAuthProvider.ReadonlyScope);
        var analytics = Need(GoogleOAuthProvider.AnalyticsScope);
        return
        [
            new(Capability.UploadVideo, upload, "Project chưa qua audit của YouTube: video upload qua API bị khóa ở chế độ riêng tư."),
            new(Capability.ResumableUpload, upload),
            new(Capability.ReadPublishStatus, read),
            new(Capability.ScheduleNative, upload, "status.publishAt: upload sớm ở chế độ riêng tư, YouTube tự công khai đúng giờ."),
            new(Capability.ReconcilePost, read),
            new(Capability.UpdateChannelInfo, CapabilityStatus.NotSupported, "Chưa triển khai."),
            new(Capability.CreateChannel, CapabilityStatus.NotSupported, "API YouTube không có tạo kênh: tạo kênh trên YouTube rồi kết nối."),
            new(Capability.ReadAnalytics, analytics, analytics == CapabilityStatus.Supported
                ? "View/like/comment/subscriber (Data API) và watch time/chia sẻ (YouTube Analytics)."
                : "Đọc được view/like/comment/subscriber (Data API). Watch time và chia sẻ cần quyền yt-analytics.readonly: bật \"YouTube Analytics\" trong Cài đặt rồi kết nối lại.")
        ];
    }

    public IReadOnlyList<ValidationIssue> Validate(PublishVariant v, MediaFile media, DestinationInfo? destination)
    {
        if (media.IsImagePost) return [new("IMAGES_UNSUPPORTED", "YouTube Data API không hỗ trợ đăng ảnh/carousel; hãy chọn video hoặc kênh khác.")];
        var issues = new List<ValidationIssue>();
        if (!Mimes.ContainsKey(Path.GetExtension(media.Path)))
            issues.Add(new("FORMAT", $"Định dạng {Path.GetExtension(media.Path)} không nằm trong danh sách YouTube hỗ trợ."));
        if (!File.Exists(media.Path)) issues.Add(new("FILE_MISSING", "Không tìm thấy file video trong kho media."));
        if (media.SizeBytes <= 0) issues.Add(new("FILE_EMPTY", "File video rỗng."));
        if (media.SizeBytes > MaxFileBytes) issues.Add(new("FILE_TOO_LARGE", "File vượt giới hạn 256 GB của YouTube."));

        var title = v.Title.Trim();
        if (title.Length == 0) issues.Add(new("TITLE_EMPTY", "Tiêu đề không được rỗng."));
        if (title.Length > MaxTitleChars) issues.Add(new("TITLE_TOO_LONG", $"Tiêu đề dài {title.Length} ký tự (tối đa {MaxTitleChars})."));
        if (title.IndexOfAny(['<', '>']) >= 0) issues.Add(new("TITLE_INVALID_CHAR", "Tiêu đề không được chứa ký tự < hoặc >."));
        var descriptionBytes = Encoding.UTF8.GetByteCount(v.Description);
        if (descriptionBytes > MaxDescriptionBytes)
            issues.Add(new("DESCRIPTION_TOO_LONG", $"Mô tả dài {descriptionBytes} byte (tối đa {MaxDescriptionBytes} byte UTF-8)."));
        if (v.Description.IndexOfAny(['<', '>']) >= 0) issues.Add(new("DESCRIPTION_INVALID_CHAR", "Mô tả không được chứa ký tự < hoặc >."));

        if (v.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.IndexOfAny([',', '<', '>']) >= 0))
            issues.Add(new("TAG_INVALID", "Thẻ không được rỗng hoặc chứa dấu phẩy, < >."));
        if (TagsLength(v.Tags) > MaxTagsChars) issues.Add(new("TAGS_TOO_LONG", $"Tổng độ dài thẻ vượt {MaxTagsChars} ký tự."));

        if (v.DurationMs is { } ms)
        {
            var duration = TimeSpan.FromMilliseconds(ms);
            if (duration > MaxDuration) issues.Add(new("DURATION_TOO_LONG", "Video dài hơn 12 giờ."));
            else if (duration > LongUploadThreshold && destination?.Attributes.GetValueOrDefault("longUploadsStatus") is { } longStatus
                     && longStatus != "allowed")
                issues.Add(new("LONG_UPLOAD_NOT_ALLOWED", "Kênh chưa được phép đăng video dài hơn 15 phút (cần xác minh tài khoản YouTube)."));
        }
        if (v.PublishAtUtc is not null && v.Privacy != PublishPrivacy.Public)
            issues.Add(new("NATIVE_SCHEDULE_PRIVACY", "Hẹn giờ bằng YouTube chỉ dùng khi chế độ hiển thị đích là Công khai."));
        return issues;
    }

    /// <summary>YouTube tính thẻ có dấu cách như có thêm cặp nháy, cộng dấu phẩy ngăn cách.</summary>
    public static int TagsLength(IReadOnlyList<string> tags) =>
        tags.Sum(t => t.Length + (t.Contains(' ') ? 2 : 0)) + Math.Max(0, tags.Count - 1);

    public async Task<IReadOnlyList<DestinationInfo>> ListDestinationsAsync(ConnectorSession session, CancellationToken ct)
    {
        var json = await GetJsonAsync(session, $"{Api}/channels?part=snippet,contentDetails,status&mine=true", ct);
        var result = new List<DestinationInfo>();
        foreach (var item in json?["items"]?.AsArray() ?? [])
        {
            var id = ConnectorHttp.Str(item?["id"]);
            if (id is null) continue;
            var attributes = new Dictionary<string, string>();
            if (ConnectorHttp.Str(item?["status"]?["longUploadsStatus"]) is { } longUploads) attributes["longUploadsStatus"] = longUploads;
            if (ConnectorHttp.Str(item?["snippet"]?["customUrl"]) is { } custom) attributes["customUrl"] = custom;
            result.Add(new DestinationInfo(id, ConnectorHttp.Str(item?["snippet"]?["title"]) ?? id, "https://www.youtube.com/channel/" + id,
                ConnectorHttp.Str(item?["contentDetails"]?["relatedPlaylists"]?["uploads"]), attributes));
        }
        return result;
    }

    public async Task<UploadOutcome> UploadAsync(ConnectorSession session, PublishVariant variant, MediaFile media, UploadState state,
        IUploadCheckpoint checkpoint, CancellationToken ct)
    {
        var total = media.SizeBytes;
        var mime = Mimes.GetValueOrDefault(Path.GetExtension(media.Path)) ?? "application/octet-stream";
        var chunk = YouTubeConnectorOptions.NormalizeChunkSize(options.ChunkSizeBytes);
        var uri = state.SessionUri;
        var offset = 0L;
        var finalSent = state.FinalChunkSent;

        if (uri is not null)
        {
            switch (await QueryAsync(session, uri, total, ct))
            {
                case SessionProbe.Done done:
                    return new UploadOutcome.Completed(done.RemoteId);
                case SessionProbe.Partial partial:
                    offset = partial.Confirmed;
                    await checkpoint.SaveProgressAsync(offset, ct);
                    break;
                default:
                    if (finalSent) return new UploadOutcome.Ambiguous("Phiên upload đã hết hạn sau khi gửi byte cuối mà chưa nhận được video id.");
                    await checkpoint.ClearSessionAsync(ct);
                    uri = null;
                    break;
            }
        }

        while (true)
        {
            if (uri is null)
            {
                await checkpoint.BeforeCreateSessionAsync(ct);
                uri = await InitAsync(session, variant, total, mime, ct);
                // Lưu session trước byte đầu tiên: crash sau đó vẫn tiếp tục được đúng phiên này.
                await checkpoint.SaveSessionAsync(uri, total, ct);
                offset = 0;
                finalSent = false;
            }

            var length = Math.Min(chunk, total - offset);
            var isFinal = offset + length >= total;
            // Upload resumable không có bước công khai riêng: chunk cuối tạo video với quyền riêng tư đã khai lúc init.
            if (isFinal) await checkpoint.VerifyRouteAsync(ct);
            if (isFinal && !finalSent)
            {
                await checkpoint.MarkFinalChunkSendingAsync(ct);
                finalSent = true;
            }

            SessionProbe probe;
            try
            {
                probe = await SendChunkAsync(session, uri, media.Path, mime, offset, length, total, ct);
            }
            catch (ConnectorException e) when (isFinal && e.IsRetryable)
            {
                // Chunk cuối có thể đã tới máy chủ: hỏi lại phiên trước khi kết luận.
                probe = await QueryAsync(session, uri, total, ct);
                if (probe is SessionProbe.Partial p && p.Confirmed < total) throw;
            }

            switch (probe)
            {
                case SessionProbe.Done done:
                    return new UploadOutcome.Completed(done.RemoteId);
                case SessionProbe.Partial partial:
                    if (partial.Confirmed >= total)
                        return new UploadOutcome.Ambiguous("Máy chủ đã nhận đủ byte nhưng chưa trả video id.");
                    offset = partial.Confirmed;
                    await checkpoint.SaveProgressAsync(offset, ct);
                    break;
                default:
                    if (finalSent) return new UploadOutcome.Ambiguous("Phiên upload hết hạn sau khi gửi byte cuối mà chưa nhận được video id.");
                    await checkpoint.ClearSessionAsync(ct);
                    uri = null;
                    break;
            }
        }
    }

    public async Task<RemoteStatus> GetStatusAsync(ConnectorSession session, string remoteId, CancellationToken ct)
    {
        var json = await GetJsonAsync(session, $"{Api}/videos?part=status,processingDetails&id={Uri.EscapeDataString(remoteId)}", ct);
        var item = json?["items"]?.AsArray().FirstOrDefault();
        if (item is null) return new RemoteStatus(RemoteState.NotFound, null, "Không tìm thấy video trên YouTube (có thể đã bị xóa).");

        var privacy = ConnectorHttp.Str(item["status"]?["privacyStatus"]);
        var processing = ConnectorHttp.Str(item["processingDetails"]?["processingStatus"]);
        return ConnectorHttp.Str(item["status"]?["uploadStatus"]) switch
        {
            "processed" => new RemoteStatus(RemoteState.Ready, privacy, null),
            "failed" => new RemoteStatus(RemoteState.Failed, privacy, "YouTube xử lý video thất bại: " + ConnectorHttp.Str(item["status"]?["failureReason"])),
            "rejected" => new RemoteStatus(RemoteState.Failed, privacy, "YouTube từ chối video: " + ConnectorHttp.Str(item["status"]?["rejectionReason"])),
            "deleted" => new RemoteStatus(RemoteState.Failed, privacy, "Video đã bị xóa trên YouTube."),
            _ when processing is "failed" or "terminated" => new RemoteStatus(RemoteState.Failed, privacy, "YouTube dừng xử lý video (" + processing + ")."),
            _ => new RemoteStatus(RemoteState.Processing, privacy, processing)
        };
    }

    public async Task<ReconcileResult> ReconcileAsync(ConnectorSession session, DestinationInfo destination, ReconcileQuery query, CancellationToken ct)
    {
        var playlist = destination.UploadsPlaylistId;
        if (playlist is null)
        {
            var channel = await GetJsonAsync(session, $"{Api}/channels?part=contentDetails&id={Uri.EscapeDataString(destination.RemoteId)}", ct);
            playlist = ConnectorHttp.Str(channel?["items"]?.AsArray().FirstOrDefault()?["contentDetails"]?["relatedPlaylists"]?["uploads"])
                       ?? throw new ConnectorException(ConnectorErrorKind.NotFound, "Không tìm thấy danh sách video đã tải lên của kênh.");
        }

        var json = await GetJsonAsync(session,
            $"{Api}/playlistItems?part=snippet,contentDetails&maxResults=50&playlistId={Uri.EscapeDataString(playlist)}", ct);
        var from = query.UploadStartedUtc - options.ReconcileSlack;
        var to = query.NowUtc + options.ReconcileSlack;
        var title = query.Title.Trim();
        var candidates = (json?["items"]?.AsArray() ?? [])
            .Select(i => (Id: ConnectorHttp.Str(i?["contentDetails"]?["videoId"]), Title: ConnectorHttp.Str(i?["snippet"]?["title"]),
                At: ParseTime(ConnectorHttp.Str(i?["snippet"]?["publishedAt"]))))
            .Where(c => c.Id is not null && !query.ExcludeRemoteIds.Contains(c.Id)
                        && string.Equals(c.Title?.Trim(), title, StringComparison.Ordinal)
                        && c.At is { } at && at >= from && at <= to)
            .Select(c => c.Id!)
            .Distinct()
            .ToList();
        if (candidates.Count == 0) return new ReconcileResult.NotFound();

        if (query.SizeBytes is { } size)
        {
            var details = await GetJsonAsync(session,
                $"{Api}/videos?part=fileDetails&id={Uri.EscapeDataString(string.Join(',', candidates))}", ct);
            var sizes = (details?["items"]?.AsArray() ?? [])
                .Select(i => (Id: ConnectorHttp.Str(i?["id"]), Size: ConnectorHttp.Str(i?["fileDetails"]?["fileSize"])))
                .Where(x => x.Id is not null)
                .ToDictionary(x => x.Id!, x => long.TryParse(x.Size, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : (long?)null);
            // Kích thước biết được mà lệch → chắc chắn không phải video này; không biết thì giữ lại (khớp tiêu đề + thời gian).
            candidates = candidates.Where(id => sizes.GetValueOrDefault(id) is not { } known || known == size).ToList();
        }

        return candidates.Count switch
        {
            0 => new ReconcileResult.NotFound(),
            1 => new ReconcileResult.Found(candidates[0]),
            _ => new ReconcileResult.Ambiguous(candidates)
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    public string? ParseRemoteId(string url)
    {
        if (VideoIdPattern().IsMatch(url.Trim())) return url.Trim();
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")) return null;
        var host = u.Host.ToLowerInvariant();
        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        if (host == "youtu.be") id = segments.FirstOrDefault();
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "studio.youtube.com")
        {
            if (segments is ["watch"])
                id = u.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "v" && p.Length == 2)?[1];
            else if (segments is ["shorts" or "video" or "live", var second, ..]) id = second;
        }
        return id is not null && VideoIdPattern().IsMatch(id) ? id : null;
    }

    public string RemoteUrl(string remoteId) => "https://www.youtube.com/watch?v=" + remoteId;

    // --- HTTP ---

    private abstract record SessionProbe
    {
        public sealed record Done(string RemoteId) : SessionProbe;
        public sealed record Partial(long Confirmed) : SessionProbe;
        public sealed record Expired : SessionProbe;
    }

    private async Task<string> InitAsync(ConnectorSession session, PublishVariant v, long total, string mime, CancellationToken ct)
    {
        var status = new JsonObject
        {
            ["privacyStatus"] = v.PublishAtUtc is null ? v.Privacy.ToString().ToLowerInvariant() : "private",
            ["selfDeclaredMadeForKids"] = v.MadeForKids,
            ["containsSyntheticMedia"] = v.ContainsSyntheticMedia
        };
        if (v.PublishAtUtc is { } at) status["publishAt"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'.000Z'", CultureInfo.InvariantCulture);
        var body = new JsonObject
        {
            ["snippet"] = new JsonObject
            {
                ["title"] = v.Title.Trim(),
                ["description"] = v.Description,
                ["tags"] = new JsonArray(v.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["categoryId"] = options.CategoryId
            },
            ["status"] = status
        }.ToJsonString();

        using var response = await SendAuthorizedAsync(session, token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{UploadApi}/videos?uploadType=resumable&part=snippet,status")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = ConnectorHttp.Bearer(token);
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", mime);
            return request;
        }, options.ApiTimeout, ct);
        if (!response.IsSuccessStatusCode) throw await MapErrorAsync(response, ct);

        var location = response.Headers.Location?.OriginalString;
        if (!ConnectorHttp.IsHttpsHost(location, "googleapis.com"))
            throw new ConnectorException(ConnectorErrorKind.BadResponse, "YouTube không trả địa chỉ phiên upload hợp lệ.");
        return location!;
    }

    private async Task<SessionProbe> SendChunkAsync(ConnectorSession session, string uri, string path, string mime,
        long offset, long length, long total, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(session, token =>
        {
            var content = new FileSliceContent(path, offset, length, mime);
            content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, total);
            var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
            request.Headers.Authorization = ConnectorHttp.Bearer(token);
            return request;
        }, options.ChunkTimeout, ct);
        return await ReadProbeAsync(response, ct);
    }

    /// <summary>Hỏi máy chủ đã nhận bao nhiêu byte: PUT rỗng với Content-Range: bytes */total.</summary>
    private async Task<SessionProbe> QueryAsync(ConnectorSession session, string uri, long total, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(session, token =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(total);
            var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
            request.Headers.Authorization = ConnectorHttp.Bearer(token);
            return request;
        }, options.ApiTimeout, ct);
        return await ReadProbeAsync(response, ct);
    }

    private static async Task<SessionProbe> ReadProbeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        if (code is 200 or 201)
        {
            var json = await ConnectorHttp.ReadJsonAsync(response, ct);
            var id = ConnectorHttp.Str(json?["id"]);
            return id is null
                ? throw new ConnectorException(ConnectorErrorKind.BadResponse, "YouTube hoàn tất upload nhưng không trả video id.")
                : new SessionProbe.Done(id);
        }
        if (code == 308) return new SessionProbe.Partial(ConfirmedBytes(response));
        if (code is 404 or 410) return new SessionProbe.Expired();
        throw await MapErrorAsync(response, ct);
    }

    /// <summary>Header Range "bytes=0-N" của 308 → N+1 byte đã xác nhận; không có header = 0.</summary>
    private static long ConfirmedBytes(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;
        var value = values.FirstOrDefault() ?? "";
        var dash = value.LastIndexOf('-');
        return dash > 0 && long.TryParse(value[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var last) ? last + 1 : 0;
    }

    private async Task<JsonNode?> GetJsonAsync(ConnectorSession session, string url, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(session, token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = ConnectorHttp.Bearer(token);
            return request;
        }, options.ApiTimeout, ct);
        if (!response.IsSuccessStatusCode) throw await MapErrorAsync(response, ct);
        return await ConnectorHttp.ReadJsonAsync(response, ct);
    }

    /// <summary>401 → làm mới token đúng 1 lần rồi gửi lại; vẫn 401 → Auth (tầng ứng dụng đánh dấu NeedsReauth + breaker).</summary>
    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        ConnectorSession session, Func<string, HttpRequestMessage> build, TimeSpan timeout, CancellationToken ct)
    {
        var response = await SendOnceAsync(session.Http, build(await session.Tokens.GetAsync(ct)), timeout, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();

        response = await SendOnceAsync(session.Http, build(await session.Tokens.RefreshAsync(ct)), timeout, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        throw new ConnectorException(ConnectorErrorKind.Auth, "YouTube từ chối xác thực (401) kể cả sau khi làm mới token; cần kết nối lại.", null, 401);
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var _ = request;
        request.Options.Set(ConnectorHttp.CallerHandlesAuth, true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { return await http.SendAsync(request, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ConnectorErrorKind.Timeout, "Hết thời gian chờ phản hồi từ YouTube.");
        }
        catch (HttpRequestException)
        {
            throw new ConnectorException(ConnectorErrorKind.Transient, "Không kết nối được tới YouTube.");
        }
    }

    private static async Task<ConnectorException> MapErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        var json = await ConnectorHttp.ReadJsonAsync(response, ct);
        var error = json?["error"];
        var reason = ConnectorHttp.Str(error?["errors"]?.AsArray().FirstOrDefault()?["reason"]) ?? ConnectorHttp.Str(error?["status"]);
        var detail = ConnectorHttp.Str(error?["message"]);
        if (detail is { Length: > 200 }) detail = detail[..200];
        var message = $"YouTube trả HTTP {code}{(reason is null ? "" : " (" + reason + ")")}{(detail is null ? "" : ": " + detail)}";
        var retryAfter = ConnectorHttp.RetryAfter(response);

        var kind = reason switch
        {
            "quotaExceeded" or "dailyLimitExceeded" => ConnectorErrorKind.QuotaExceeded,
            "rateLimitExceeded" or "userRateLimitExceeded" => ConnectorErrorKind.RateLimited,
            "uploadLimitExceeded" or "youtubeSignupRequired" => ConnectorErrorKind.Policy,
            "forbidden" or "insufficientPermissions" or "channelClosed" or "channelSuspended" => ConnectorErrorKind.Permission,
            _ => code switch
            {
                400 => ConnectorErrorKind.Validation,
                401 => ConnectorErrorKind.Auth,
                403 => ConnectorErrorKind.Permission,
                404 => ConnectorErrorKind.NotFound,
                429 => ConnectorErrorKind.RateLimited,
                >= 500 => ConnectorErrorKind.Transient,
                _ => ConnectorErrorKind.BadResponse
            }
        };
        return new ConnectorException(kind, message, retryAfter, code, reason);
    }

    private static DateTimeOffset? ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
