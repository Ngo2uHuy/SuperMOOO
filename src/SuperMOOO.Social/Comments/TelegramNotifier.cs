using SuperMOOO.Social.Crawlers;

namespace SuperMOOO.Social.Comments;

// Section "notify.telegram": chat id (bot token nằm ở secret notify:telegram/bot_token).
public sealed class TelegramSettings
{
    public string? ChatId { get; set; }
}

/// <summary>Gửi thông báo Telegram; chưa cấu hình token/chat id thì bỏ qua êm (không phải lỗi).</summary>
[Service(As = [typeof(INotifier)])]
public sealed class TelegramNotifier(CrawlHttp http, ISecretStore secrets, ISettingsStore settings) : INotifier
{
    public static readonly SecretKey TokenKey = new("notify:telegram", "bot_token");

    public async Task SendAsync(string message, CancellationToken ct)
    {
        var token = await secrets.GetAsync(TokenKey, ct);
        var chat = settings.Get<TelegramSettings>("notify.telegram").ChatId;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chat)) return;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = chat, ["text"] = message, ["parse_mode"] = "Markdown" });
        await http.SendAsync("telegram", HttpMethod.Post, $"https://api.telegram.org/bot{token}/sendMessage", content: form, timeout: TimeSpan.FromSeconds(6), ct: ct);
    }
}
