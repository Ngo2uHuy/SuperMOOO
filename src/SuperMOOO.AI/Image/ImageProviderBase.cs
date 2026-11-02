using System.Security.Cryptography;
using System.Text;

namespace SuperMOOO.AI.Image;

/// <summary>Cache + chọn model dùng chung; provider mới chỉ cần cài FetchAsync trả byte ảnh và đăng ký [Service(As = [typeof(IImageProvider)])].</summary>
public abstract class ImageProviderBase(AppPaths paths, ISettingsStore settings) : IImageProvider
{
    protected static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(4);
    protected static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    protected const int MaxImageBytes = 32 * 1024 * 1024;

    protected ISettingsStore Settings => settings;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string DefaultModel { get; }
    public virtual bool RequiresKey => true;

    public async Task<ImageResult> GenerateAsync(ImageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt ảnh trống.");
        var model = ResolveModel(request.Model);
        var prompt = string.IsNullOrWhiteSpace(request.Style) ? request.Prompt.Trim() : $"{request.Style.Trim()}. {request.Prompt.Trim()}";

        var dir = paths.CacheDir("images");
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{prompt}|{Id}|{model}|{request.Width}x{request.Height}|{request.Seed?.ToString() ?? "-"}")));
        if (Directory.EnumerateFiles(dir, hash + ".*").FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.Ordinal)) is { } hit)
            return new ImageResult(hit, true);

        var bytes = await FetchAsync(request with { Prompt = prompt }, model, ct);
        var path = Path.Combine(dir, hash + Extension(bytes));
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);
        return new ImageResult(path, false);
    }

    public abstract Task<ProviderCheckResult> CheckAsync(CancellationToken ct);

    protected abstract Task<byte[]> FetchAsync(ImageRequest request, string model, CancellationToken ct);

    protected ProviderException NoImage(string detail) => new(Id, ProviderErrorKind.BadResponse, $"{Name}: {detail}");

    private string ResolveModel(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        var s = settings.Get<AiImagesSettings>("ai.images");
        return s.Provider == Id && !string.IsNullOrWhiteSpace(s.Model) ? s.Model.Trim() : DefaultModel;
    }

    private static string Extension(byte[] b) =>
        b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? ".jpg"
        : b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[8] == 'W' && b[9] == 'E' ? ".webp"
        : ".png";
}

/// <summary>Chọn provider ảnh theo Cài đặt → Ảnh AI.</summary>
[Service]
public sealed class ImageProviderRegistry(IEnumerable<IImageProvider> providers, ISettingsStore settings)
{
    public IReadOnlyList<IImageProvider> All { get; } = providers.OrderBy(p => p.RequiresKey).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

    public IImageProvider Get(string id) => All.FirstOrDefault(p => p.Id == id)
        ?? throw new ProviderException(id, ProviderErrorKind.BadResponse, $"Provider ảnh '{id}' không tồn tại.");

    public IImageProvider Selected => Get(settings.Get<AiImagesSettings>("ai.images").Provider);
}
