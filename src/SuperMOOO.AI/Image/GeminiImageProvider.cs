using SuperMOOO.AI.Http;
using SuperMOOO.AI.Keys;

namespace SuperMOOO.AI.Image;

[Service(As = [typeof(IImageProvider)])]
public sealed class GeminiImageProvider(IHttpClientProvider http, ApiKeyPool keys, AppPaths paths, ISettingsStore settings) : ImageProviderBase(paths, settings)
{
    private string BaseUrl => Settings.Get<AiImagesSettings>("ai.images").GeminiUrl.Trim().TrimEnd('/') is { Length: > 0 } u ? u : "https://generativelanguage.googleapis.com/v1beta";
    private static readonly (string Name, double Ratio)[] Aspects =
    [
        ("1:1", 1), ("2:3", 2 / 3.0), ("3:2", 1.5), ("3:4", 0.75), ("4:3", 4 / 3.0), ("4:5", 0.8), ("5:4", 1.25), ("9:16", 9 / 16.0), ("16:9", 16 / 9.0), ("21:9", 21 / 9.0)
    ];

    public override string Id => "gemini";
    public override string Name => "Google Gemini";
    public override string DefaultModel => "gemini-2.5-flash-image";

    protected override async Task<byte[]> FetchAsync(ImageRequest request, string model, CancellationToken ct)
    {
        var ratio = (double)request.Width / request.Height;
        var aspect = Aspects.MinBy(a => Math.Abs(Math.Log(a.Ratio / ratio))).Name;
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.Prompt }) }),
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("IMAGE"),
                ["imageConfig"] = new JsonObject { ["aspectRatio"] = aspect }
            }
        };

        var text = await keys.ExecuteAsync(Id, (key, c) => AiHttp.SendTextAsync(http, Id,
            AiHttp.Json(HttpMethod.Post, $"{BaseUrl}/models/{Uri.EscapeDataString(model)}:generateContent", body, googKey: key), GenerateTimeout, c, MaxImageBytes), ct);
        var parts = AiHttp.ParseJson(Id, text)["candidates"]?[0]?["content"]?["parts"] as JsonArray;
        var b64 = parts?.Select(p => (p?["inlineData"] ?? p?["inline_data"])?["data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d))
            ?? throw NoImage("phản hồi không có ảnh (có thể bị bộ lọc an toàn chặn).");
        return Convert.FromBase64String(b64);
    }

    public override async Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            await keys.ExecuteAsync(Id, (key, c) => AiHttp.SendTextAsync(http, Id,
                AiHttp.Json(HttpMethod.Get, BaseUrl + "/models?pageSize=1", null, googKey: key), CheckTimeout, c), ct);
            return new ProviderCheckResult(true, null, "Key Gemini hợp lệ.");
        }
        catch (ProviderException e)
        {
            return new ProviderCheckResult(false, e.Kind, e.Message);
        }
    }
}
