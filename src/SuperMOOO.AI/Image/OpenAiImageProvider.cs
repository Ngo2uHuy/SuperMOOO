using SuperMOOO.AI.Http;
using SuperMOOO.AI.Keys;

namespace SuperMOOO.AI.Image;

[Service(As = [typeof(IImageProvider)])]
public sealed class OpenAiImageProvider(IHttpClientProvider http, ApiKeyPool keys, AppPaths paths, ISettingsStore settings) : ImageProviderBase(paths, settings)
{
    private string BaseUrl => Settings.Get<AiImagesSettings>("ai.images").OpenAiUrl.Trim().TrimEnd('/') is { Length: > 0 } u ? u : "https://api.openai.com/v1";

    public override string Id => "openai";
    public override string Name => "OpenAI";
    public override string DefaultModel => "gpt-image-1";

    protected override async Task<byte[]> FetchAsync(ImageRequest request, string model, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["prompt"] = request.Prompt, ["n"] = 1, ["size"] = PickSize(model, request.Width, request.Height) };
        if (model.Contains("dall-e", StringComparison.OrdinalIgnoreCase)) body["response_format"] = "b64_json";

        var text = await keys.ExecuteAsync(Id, (key, c) => AiHttp.SendTextAsync(http, Id,
            AiHttp.Json(HttpMethod.Post, BaseUrl + "/images/generations", body, bearer: key), GenerateTimeout, c, MaxImageBytes), ct);
        var item = (AiHttp.ParseJson(Id, text)["data"] as JsonArray)?.FirstOrDefault();
        if (item?["b64_json"]?.GetValue<string>() is { Length: > 0 } b64) return Convert.FromBase64String(b64);
        if (item?["url"]?.GetValue<string>() is { Length: > 0 } url)
            return await AiHttp.SendAsync(http, Id, AiHttp.Json(HttpMethod.Get, url, null), GenerateTimeout, ct, MaxImageBytes);
        throw NoImage("phản hồi không có ảnh.");
    }

    public override async Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            await keys.ExecuteAsync(Id, (key, c) => AiHttp.SendTextAsync(http, Id,
                AiHttp.Json(HttpMethod.Get, BaseUrl + "/models", null, bearer: key), CheckTimeout, c), ct);
            return new ProviderCheckResult(true, null, "Key OpenAI hợp lệ.");
        }
        catch (ProviderException e)
        {
            return new ProviderCheckResult(false, e.Kind, e.Message);
        }
    }

    // gpt-image-* và dall-e-3 nhận cỡ ngang/dọc khác nhau; dall-e-2 chỉ vuông.
    private static string PickSize(string model, int w, int h)
    {
        if (model.Contains("dall-e-2", StringComparison.OrdinalIgnoreCase)) return "1024x1024";
        var (land, port) = model.Contains("dall-e", StringComparison.OrdinalIgnoreCase) ? ("1792x1024", "1024x1792") : ("1536x1024", "1024x1536");
        var r = (double)w / h;
        return r > 1.2 ? land : r < 0.83 ? port : "1024x1024";
    }
}
