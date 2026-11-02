using SuperMOOO.AI.Http;
using SuperMOOO.AI.Keys;

namespace SuperMOOO.AI.Image;

/// <summary>Key tuỳ chọn (có thì gửi Bearer). Không key: server giới hạn ~1 ảnh/45 s mỗi IP nên mọi lời gọi trong app xếp hàng FIFO (singleton) và cách nhau tối thiểu từng ấy giây.</summary>
[Service(As = [typeof(IImageProvider)])]
public sealed class PollinationsImageProvider(IHttpClientProvider http, ApiKeyPool keys, AppPaths paths, ISettingsStore settings) : ImageProviderBase(paths, settings)
{
    private const int MaxRateRetries = 3;
    private const int MaxPromptChars = 1500;     // prompt nằm trong URL

    private readonly SemaphoreSlim _lane = new(1, 1);
    private DateTime _lastStartUtc = DateTime.MinValue;

    public override string Id => "pollinations";
    public override string Name => "Pollinations";
    public override string DefaultModel => "flux";
    public override bool RequiresKey => false;

    protected override async Task<byte[]> FetchAsync(ImageRequest request, string model, CancellationToken ct)
    {
        // Không key thì giữ 45 s của server; có key mới theo cài đặt.
        var interval = TimeSpan.FromSeconds(await keys.HasKeyAsync(Id, ct) ? Math.Max(1, Settings.Get<AiImagesSettings>("ai.images").PollinationsIntervalSeconds) : 45);
        var prompt = request.Prompt.Length > MaxPromptChars ? request.Prompt[..MaxPromptChars] : request.Prompt;
        var url = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(prompt)}?width={request.Width}&height={request.Height}&nologo=true&model={Uri.EscapeDataString(model)}"
            + (request.Seed is { } seed ? $"&seed={seed}" : "");

        await _lane.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = _lastStartUtc + interval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastStartUtc = DateTime.UtcNow;
                try
                {
                    return await keys.ExecuteOptionalAsync(Id, (key, c) => AiHttp.SendAsync(http, Id,
                        AiHttp.Json(HttpMethod.Get, url, null, bearer: key), GenerateTimeout, c, MaxImageBytes), ct);
                }
                catch (ProviderException e) when (attempt < MaxRateRetries && e.Kind is ProviderErrorKind.RateLimited or ProviderErrorKind.QuotaExceeded)
                {
                    // Giữ lượt trong hàng đợi chung: chờ hết chu kỳ rồi thử lại, không nhường lượt cho ảnh khác.
                }
            }
        }
        finally { _lane.Release(); }
    }

    public override async Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            await AiHttp.SendTextAsync(http, Id, AiHttp.Json(HttpMethod.Get, "https://image.pollinations.ai/models", null), CheckTimeout, ct);
            return new ProviderCheckResult(true, null, "Kết nối Pollinations thành công.");
        }
        catch (ProviderException e)
        {
            return new ProviderCheckResult(false, e.Kind, e.Message);
        }
    }
}
