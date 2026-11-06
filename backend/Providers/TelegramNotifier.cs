namespace RealtouchSmartTrade.Api.Providers;

// Sends messages via the Telegram Bot API. Credentials are server-side only
// (Telegram:BotToken, Telegram:ChatId via dotnet user-secrets), never sent
// to the frontend.
//
// IMPORTANT: this is plumbing only - nothing calls SendAsync automatically
// yet. Per an explicit decision, real signal alerts wait until setups can
// actually report Qualified: true (entry/stop/target + confluence scoring,
// sections 16-19 of STRATEGY.md), so a Telegram message means something
// real rather than the unvalidated v1 heuristic's rough score. The only
// current caller is the manual /api/telegram/test endpoint in Program.cs.
public class TelegramNotifier(IHttpClientFactory httpClientFactory, IConfiguration config)
{
    public async Task<(bool Success, string? Error)> SendAsync(string text, CancellationToken ct = default)
    {
        var token = config["Telegram:BotToken"];
        var chatId = config["Telegram:ChatId"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            return (false, "Telegram not configured (Telegram:BotToken / Telegram:ChatId).");

        try
        {
            var client = httpClientFactory.CreateClient();
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var response = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = text,
                ["parse_mode"] = "Markdown"
            }), ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return (false, $"Telegram {(int)response.StatusCode}: {body}");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Telegram request failed: {ex.Message}");
        }
    }
}
