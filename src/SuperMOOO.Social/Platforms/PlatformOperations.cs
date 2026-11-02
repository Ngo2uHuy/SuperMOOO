using System.ComponentModel.DataAnnotations;

namespace SuperMOOO.Social.Platforms;

public sealed class NoRequest;

public sealed record PlatformRow(string Platform, string Name, string AuthType, string Status, string? Message, string Domains);

[Operation("operations.get_platforms", "Danh sách nền tảng", OperationGroup.System)]
public sealed class GetPlatformsOperation(PlatformSessionService sessions) : IOperationHandler<NoRequest, IReadOnlyList<PlatformRow>>
{
    public async Task<IReadOnlyList<PlatformRow>> HandleAsync(NoRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        (await sessions.ListAsync(ct)).Select(s => new PlatformRow(s.Platform.Id, s.Platform.Name, s.AuthType, s.Status, s.Message,
            string.Join(", ", s.Platform.Domains))).ToList();
}

public sealed class ValidateUrlRequest
{
    [Required, Field("Nền tảng", Example = "youtube")]
    public string Platform { get; set; } = "";

    [Required, Field("Liên kết", Kind = FieldKind.Url)]
    public string Url { get; set; } = "";
}

[Operation("operations.validate_url", "Kiểm tra liên kết", OperationGroup.System)]
public sealed class ValidateUrlOperation : IOperationHandler<ValidateUrlRequest, UrlValidation>
{
    public Task<UrlValidation> HandleAsync(ValidateUrlRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        Task.FromResult(PlatformDetector.Validate(request.Platform, request.Url));
}

public sealed class SavePlatformSessionRequest
{
    [Required, Field("Nền tảng", Example = "tiktok")]
    public string Platform { get; set; } = "";

    [Field("Loại thông tin", Help = "cookie, token hoặc tên khác của nền tảng (api_key, sessdata…).")]
    public string AuthType { get; set; } = "cookie";

    [Required, Field("Giá trị", Kind = FieldKind.Secret)]
    public string Value { get; set; } = "";
}

[Operation("operations.save_platform_session", "Lưu cookie / API key nền tảng", OperationGroup.System)]
public sealed class SavePlatformSessionOperation(PlatformSessionService sessions) : IOperationHandler<SavePlatformSessionRequest, PlatformStatus>
{
    public Task<PlatformStatus> HandleAsync(SavePlatformSessionRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        sessions.SaveAsync(request.Platform, request.Value, request.AuthType, ct);
}

public sealed class PlatformRequest
{
    [Required, Field("Nền tảng", Example = "tiktok")]
    public string Platform { get; set; } = "";
}

[Operation("operations.check_session", "Kiểm tra đăng nhập", OperationGroup.System)]
public sealed class CheckSessionOperation(PlatformSessionService sessions) : IOperationHandler<PlatformRequest, PlatformStatus>
{
    public Task<PlatformStatus> HandleAsync(PlatformRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        sessions.CheckAsync(request.Platform, ct);
}

[Operation("operations.clear_session", "Xóa phiên đăng nhập", OperationGroup.System)]
public sealed class ClearSessionOperation(PlatformSessionService sessions) : IOperationHandler<PlatformRequest, PlatformStatus>
{
    public Task<PlatformStatus> HandleAsync(PlatformRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        sessions.ClearAsync(request.Platform, ct);
}
