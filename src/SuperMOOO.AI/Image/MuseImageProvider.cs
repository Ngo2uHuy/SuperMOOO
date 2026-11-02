using SuperMOOO.AI.Http;
using SuperMOOO.AI.Keys;

namespace SuperMOOO.AI.Image;

/// <summary>Meta Muse Image qua OpenRouter (chat completions, không stream: OpenRouter ghi nhận lỗi với streaming ảnh); dùng key "openrouter" có sẵn.</summary>
[Service(As = [typeof(IImageProvider)])]
public sealed class MuseImageProvider(IHttpClientProvider http, ApiKeyPool keys, AppPaths paths, ISettingsStore settings) : ImageProviderBase(paths, settings)
{
    private const string KeyProvider = "openrouter";
    private const string BaseUrl = "https://openrouter.ai/api/v1";

    public override string Id => "muse";
    public override string Name => "Muse Image (Meta, qua OpenRouter)";
    public override string DefaultModel => "meta/muse-image";

    protected override async Task<byte[]> FetchAsync(ImageRequest request, string model, CancellationToken ct)
    {
        var ratio = (double)request.Width / request.Height;
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = request.Prompt }),
            ["modalities"] = new JsonArray("image", "text"),
            ["image_config"] = new JsonObject { ["aspect_ratio"] = ratio > 1.2 ? "16:9" : ratio < 0.83 ? "9:16" : "1:1" }
        };

        var text = await keys.ExecuteAsync(KeyProvider, (key, c) => AiHttp.SendTextAsync(http, Id,
            AiHttp.Json(HttpMethod.Post, BaseUrl + "/chat/completions", body, bearer: key), GenerateTimeout, c, MaxImageBytes), ct);
        var images = AiHttp.ParseJson(Id, text)["choices"]?[0]?["message"]?["images"] as JsonArray;
        var url = images?.Select(i => i?["image_url"]?["url"]?.GetValue<string>()).FirstOrDefault(u => !string.IsNullOrEmpty(u))
            ?? throw NoImage("phản hồi không có ảnh.");

        var comma = url.IndexOf(',');
        if (!url.StartsWith("data:", StringComparison.Ordinal) || comma < 0)
            return await AiHttp.SendAsync(http, Id, AiHttp.Json(HttpMethod.Get, url, null), GenerateTimeout, ct, MaxImageBytes);
        return Convert.FromBase64String(url[(comma + 1)..]);
    }

    public override async Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            await keys.ExecuteAsync(KeyProvider, (key, c) => AiHttp.SendTextAsync(http, Id,
                AiHttp.Json(HttpMethod.Get, BaseUrl + "/models", null, bearer: key), CheckTimeout, c), ct);
            return new ProviderCheckResult(true, null, "Key OpenRouter hợp lệ.");
        }
        catch (ProviderException e)
        {
            return new ProviderCheckResult(false, e.Kind, e.Message);
        }
    }
}
