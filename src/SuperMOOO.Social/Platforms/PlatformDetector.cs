namespace SuperMOOO.Social.Platforms;

public sealed record UrlValidation(bool Valid, string? DetectedPlatform, string ExpectedPlatform, string Message);

public static class PlatformDetector
{
    /// <summary>Id nền tảng theo tên miền (khớp đúng host hoặc subdomain); null nếu không nhận ra.</summary>
    public static string? Detect(string? url)
    {
        var text = (url ?? "").Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://")) text = "https://" + text.TrimStart('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.ToLowerInvariant().TrimEnd('.');
        return PlatformRegistry.All.FirstOrDefault(p => p.Domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal)))?.Id;
    }

    public static UrlValidation Validate(string platform, string? url)
    {
        var expected = PlatformRegistry.NormalizeId(platform) ?? platform;
        var detected = Detect(url);
        var ok = detected == expected;
        return new UrlValidation(ok, detected, expected, ok ? "URL đúng nền tảng." : "URL không khớp nền tảng đã chọn.");
    }
}
