using System.Text.RegularExpressions;

namespace SuperMOOO.Core;

public sealed record ApiKeyCheck(bool Ok, string Message)
{
    public string Status => Ok ? "ok" : "invalid_format";
}

// Chỉ kiểm tra định dạng (không gọi mạng) để bắt key sai ngay lúc nhập, thay vì lộ lỗi HTTP 400 lúc gọi.
public static partial class ApiKeyValidator
{
    private const int MinLength = 8;

    [GeneratedRegex(@"^(AIza[0-9A-Za-z_-]{35}|AQ\.[0-9A-Za-z_.-]{20,})$")]
    private static partial Regex GeminiKey();

    public static IReadOnlyList<string> Providers { get; } = ["openai", "gemini", "groq", "openrouter", "deepseek", "hf", "anthropic", "elevenlabs", "google", "openai_compat", "pollinations"];

    public static ApiKeyCheck Validate(string provider, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new(false, "Key trống.");
        if (key.Any(char.IsWhiteSpace))
            return new(false, "Key chứa khoảng trắng.");

        return provider.ToLowerInvariant() switch
        {
            "google" => GeminiKey().IsMatch(key)
                ? Ok : new(false, "Key Google Cloud phải có dạng \"AIza\" + 35 ký tự."),
            "gemini" => GeminiKey().IsMatch(key)
                ? Ok : new(false, "Key Gemini phải có dạng \"AIza\" + 35 ký tự hoặc \"AQ.\" + chuỗi từ 20 ký tự."),
            "openrouter" => Prefixed(key, "sk-or-", "OpenRouter"),
            "anthropic" => Prefixed(key, "sk-ant-", "Anthropic"),
            "openai" or "deepseek" => Prefixed(key, "sk-", provider),
            "groq" => Prefixed(key, "gsk_", "Groq"),
            "hf" => Prefixed(key, "hf_", "Hugging Face"),
            _ => key.Length >= MinLength ? Ok : new(false, $"Key quá ngắn (tối thiểu {MinLength} ký tự).")
        };
    }

    private static ApiKeyCheck Ok { get; } = new(true, "Định dạng hợp lệ.");

    private static ApiKeyCheck Prefixed(string key, string prefix, string name) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && key.Length >= prefix.Length + MinLength
            ? Ok : new(false, $"Key {name} phải bắt đầu bằng \"{prefix}\".");
}
