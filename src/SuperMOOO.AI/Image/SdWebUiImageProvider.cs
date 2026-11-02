using SuperMOOO.AI.Http;

namespace SuperMOOO.AI.Image;

/// <summary>Stable Diffusion WebUI (AUTOMATIC1111) chạy máy; cần mở WebUI với --api. Không cần key.</summary>
[Service(As = [typeof(IImageProvider)])]
public sealed class SdWebUiImageProvider(IHttpClientProvider http, AppPaths paths, ISettingsStore settings) : ImageProviderBase(paths, settings)
{
    private const string NegativePrompt = "text, watermark, blurry, photo, realistic, 3d";
    private static readonly TimeSpan SdTimeout = TimeSpan.FromMinutes(10);

    public override string Id => "sdwebui";
    public override string Name => "Stable Diffusion WebUI";
    public override string DefaultModel => "";       // rỗng = checkpoint đang nạp trong WebUI
    public override bool RequiresKey => false;

    private string BaseUrl => Settings.Get<AiImagesSettings>("ai.images").SdWebUiUrl.Trim().TrimEnd('/');

    protected override async Task<byte[]> FetchAsync(ImageRequest request, string model, CancellationToken ct)
    {
        var scale = Math.Min(1.0, 1024.0 / Math.Max(request.Width, request.Height));
        static int Round64(double n) => Math.Max(64, (int)Math.Round(n / 64) * 64);
        var body = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["negative_prompt"] = NegativePrompt,
            ["width"] = Round64(request.Width * scale),
            ["height"] = Round64(request.Height * scale),
            ["steps"] = 25,
            ["seed"] = request.Seed ?? -1
        };
        if (model.Length > 0) body["override_settings"] = new JsonObject { ["sd_model_checkpoint"] = model };

        var text = await AiHttp.SendTextAsync(http, Id, AiHttp.Json(HttpMethod.Post, BaseUrl + "/sdapi/v1/txt2img", body), SdTimeout, ct, MaxImageBytes);
        var b64 = (AiHttp.ParseJson(Id, text)["images"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
        return string.IsNullOrEmpty(b64) ? throw NoImage("phản hồi không có ảnh.") : Convert.FromBase64String(b64);
    }

    public override async Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            await AiHttp.SendTextAsync(http, Id, AiHttp.Json(HttpMethod.Get, BaseUrl + "/sdapi/v1/sd-models", null), CheckTimeout, ct);
            return new ProviderCheckResult(true, null, $"Kết nối {BaseUrl} thành công.");
        }
        catch (ProviderException e)
        {
            return new ProviderCheckResult(false, e.Kind, $"Không kết nối được {BaseUrl}; mở WebUI với tham số --api. ({e.Message})");
        }
    }
}
