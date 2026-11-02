namespace SuperMOOO.Core.Abstractions;

/// <summary>Style là mô tả phong cách (vd. "whiteboard doodle, black marker") ghép vào đầu prompt; Model rỗng = theo Cài đặt → Ảnh AI.</summary>
public sealed record ImageRequest(string Prompt, int Width, int Height, int? Seed = null, string? Style = null, string? Model = null);

/// <summary>Path là file ảnh nằm trong cache; FromCache = true khi không gọi provider.</summary>
public sealed record ImageResult(string Path, bool FromCache);

public interface IImageProvider
{
    string Id { get; }                          // "pollinations" | "openai" | "gemini" | "sdwebui" | "muse"
    string Name { get; }
    string DefaultModel { get; }
    bool RequiresKey { get; }
    Task<ImageResult> GenerateAsync(ImageRequest request, CancellationToken ct);
    Task<ProviderCheckResult> CheckAsync(CancellationToken ct);
}
