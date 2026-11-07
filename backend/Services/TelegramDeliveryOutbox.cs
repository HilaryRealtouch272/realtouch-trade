using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace RealtouchSmartTrade.Api.Services;

public record TelegramDeliveryReceipt(
    string Id, string Status, DateTime CreatedAtUtc, int Attempts = 0,
    DateTime? DeliveredAtUtc = null, string? LastError = null);

// Each flow receives its own instance and disk root. Credentials never enter the queue.
public sealed class TelegramDeliveryOutbox : IHttpClientFactory
{
    private sealed class State
    {
        public State() { }
        public Dictionary<string, string> Pending { get; set; } = new();
        public Dictionary<string, TelegramDeliveryReceipt> Receipts { get; set; } = new();
    }

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger _logger;
    private readonly bool _beta;
    private readonly string _path;
    private readonly State _state;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private DateTime _lastSendUtc = DateTime.MinValue;

    public TelegramDeliveryOutbox(
        IHttpClientFactory httpClients, IConfiguration config, IHostEnvironment env, ILogger logger, bool beta = false)
    {
        _http = httpClients;
        _config = config;
        _logger = logger;
        _beta = beta;
        _path = Path.Combine(env.ContentRootPath, ".cache", "telegram-delivery.json");
        _state = DiskCache.Load<State>(_path) ?? new();
        if (beta && !File.Exists(_path))
        {
            // Preserve pending notifications from the first beta implementation.
            _state.Pending = DiskCache.Load<Dictionary<string, string>>(
                Path.Combine(env.ContentRootPath, ".cache", "notification-outbox.json")) ?? new();
        }
    }

    public HttpClient CreateClient(string name) => new(new QueueHandler(this));

    public TelegramDeliveryReceipt? Get(string? id)
    {
        lock (_lock) return id is not null ? _state.Receipts.GetValueOrDefault(id) : null;
    }

    public object Summarize()
    {
        lock (_lock) return new
        {
            configured = !string.IsNullOrWhiteSpace(_config["Telegram:ChatId"]) && !string.IsNullOrWhiteSpace(_config["Telegram:BotToken"]),
            queued = _state.Pending.Count,
            retrying = _state.Pending.Keys.Count(id => _state.Receipts.GetValueOrDefault(id)?.LastError is not null),
            delivered = _state.Receipts.Values.Count(r => r.Status == "Delivered")
        };
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_state));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }

    public async Task FlushAsync()
    {
        await _flushGate.WaitAsync();
        try
        {
            var chatId = _config["Telegram:ChatId"];
            var token = _config["Telegram:BotToken"];
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(token)) return;
            KeyValuePair<string, string>[] pending;
            lock (_lock) pending = _state.Pending.ToArray();
            var client = _http.CreateClient();
            var deadline = DateTime.UtcNow.AddSeconds(45);
            foreach (var message in pending)
            {
                if (DateTime.UtcNow >= deadline) break;
                try
                {
                    var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastSendUtc);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var response = await client.PostAsync($"https://api.telegram.org/bot{token}/sendMessage",
                        new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["chat_id"] = chatId, ["text"] = message.Value, ["parse_mode"] = "Markdown"
                        }), timeout.Token);
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    var accepted = response.IsSuccessStatusCode &&
                        body.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                    _lastSendUtc = DateTime.UtcNow;
                    RecordAttempt(message.Key, accepted,
                        accepted ? null : $"Telegram rejected the request (HTTP {(int)response.StatusCode})");
                    if (!accepted) break;
                }
                catch (Exception)
                {
                    RecordAttempt(message.Key, false, "Telegram request failed");
                    _logger.LogWarning("Telegram unavailable; queued notifications will retry next scan");
                    break;
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification queue could not be processed this scan"); }
        finally { _flushGate.Release(); }
    }

    private void RecordAttempt(string id, bool delivered, string? error)
    {
        lock (_lock)
        {
            var previous = _state.Receipts.GetValueOrDefault(id)
                ?? new TelegramDeliveryReceipt(id, "Queued", DateTime.UtcNow);
            _state.Receipts[id] = previous with
            {
                Status = delivered ? "Delivered" : "Retrying", Attempts = previous.Attempts + 1,
                DeliveredAtUtc = delivered ? DateTime.UtcNow : null, LastError = error
            };
            if (delivered) _state.Pending.Remove(id);
            Persist();
        }
    }

    private sealed class QueueHandler(TelegramDeliveryOutbox owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            var text = (owner._beta ? "🧪 *BETA · 15m confirmation*\n" : "") + fields["text"].ToString();
            var id = request.Headers.TryGetValues("X-Signal-Notification", out var ids) ? ids.First()
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            lock (owner._lock)
            {
                if (owner._state.Receipts.GetValueOrDefault(id)?.Status != "Delivered")
                {
                    owner._state.Pending.TryAdd(id, text);
                    owner._state.Receipts.TryAdd(id, new TelegramDeliveryReceipt(id, "Queued", DateTime.UtcNow));
                    owner.Persist();
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }
}
