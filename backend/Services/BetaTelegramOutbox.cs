using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace RealtouchSmartTrade.Api.Services;

// Existing alert/ledger services acknowledge a send only after its beta message is durably
// queued. Failed deliveries remain here across workflow runs, including outcome messages.
internal sealed class BetaTelegramOutbox(
    IHttpClientFactory httpClients, IConfiguration config, IHostEnvironment env, ILogger<BetaTelegramOutbox> logger) : IHttpClientFactory
{
    private readonly string _path = Path.Combine(env.ContentRootPath, ".cache", "notification-outbox.json");
    private readonly Dictionary<string, string> _pending =
        DiskCache.Load<Dictionary<string, string>>(Path.Combine(env.ContentRootPath, ".cache", "notification-outbox.json")) ?? new();
    private readonly object _lock = new();
    private DateTime _lastSendUtc = DateTime.MinValue;

    public HttpClient CreateClient(string name) => new(new QueueHandler(this));

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_pending));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }

    public async Task FlushAsync()
    {
        var chatId = config["Telegram:ChatId"];
        var token = config["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(token)) return;
        KeyValuePair<string, string>[] pending;
        lock (_lock) pending = _pending.ToArray();
        var client = httpClients.CreateClient();
        foreach (var message in pending)
        {
            try
            {
                var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastSendUtc);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                using var response = await client.PostAsync($"https://api.telegram.org/bot{token}/sendMessage",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["chat_id"] = chatId, ["text"] = message.Value, ["parse_mode"] = "Markdown"
                    }));
                _lastSendUtc = DateTime.UtcNow;
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Beta Telegram returned HTTP {Status}; queued messages will retry next scan", (int)response.StatusCode);
                    break;
                }
                lock (_lock)
                {
                    _pending.Remove(message.Key);
                    Persist();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Beta Telegram unavailable; queued messages will retry next scan");
                break;
            }
        }
    }

    private sealed class QueueHandler(BetaTelegramOutbox owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            var text = "🧪 *BETA · 15m confirmation*\n" + fields["text"].ToString();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            lock (owner._lock)
            {
                owner._pending.TryAdd(key, text);
                owner.Persist();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }
}
