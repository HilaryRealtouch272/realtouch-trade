using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

// Publishes market-signal intents only. Broker credentials, positions and account
// state stay on the demo worker. Telegram delivery is verified before publication.
public sealed class AutoTradingSignalSink(IConfiguration config, ILogger<AutoTradingSignalSink> logger, IHostEnvironment env)
{
    public record Intent(string Id, string Flow, string Symbol, string Side,
        DateTime QualifiedAtUtc, DateTime EntryDeadlineUtc, DateTime CloseByUtc,
        decimal Entry, decimal ZoneMin, decimal ZoneMax, decimal Stop,
        decimal[] Targets, decimal RiskMultiplier, string? NotificationId = null,
        DateTime? TelegramDeliveredAtUtc = null);
    public record Candidate(Intent Signal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private string CandidatesPath => Path.Combine(env.ContentRootPath, ".cache", "auto-trading-candidates.json");

    public void Main(SignalResult signal, DateTime now, string? notificationId = null)
    {
        if (!signal.Triggered || signal.Grade is not ("A+" or "A" or "B")) return;
        var duration = TimeframeConfig.Duration(TimeframeIntervals.ParseLabel(signal.AnalysisTimeframe) ?? Timeframe.H1);
        Write(new("main:" + (notificationId ?? SignalIdentity.For(signal)), "main", signal.Symbol, signal.Direction.ToString(),
            signal.DetectedAtUtc, signal.DetectedAtUtc.AddMinutes(5), now + duration * 30,
            signal.PreferredEntry, signal.EntryZoneMin, signal.EntryZoneMax, signal.Stop,
            [signal.Tp1, signal.Tp2, signal.Tp3], 1m, notificationId));
    }

    public void Vtb(VtbSignalService.VtbLogEntry entry, string? notificationId = null)
    {
        // Daily EntryTimeUtc labels the bar OPEN; confirmation is its close.
        var confirmed = entry.EntryTimeUtc.AddDays(1);
        Write(new("vtb:" + entry.Id, "vtb", entry.Symbol, entry.Side, confirmed,
            confirmed.AddMinutes(15), confirmed.AddDays(VtbRules.TimeExitDays), entry.EntryRef,
            entry.EntryRef, entry.EntryRef, entry.Stop, [entry.Target], entry.RiskMultiplier, notificationId));
    }

    private void Write(Intent intent)
    {
        if (!config.GetValue<bool>("AutoTrading:Enabled")) return;
        try
        {
            lock (gate)
            {
                var candidates = DiskCache.Load<Dictionary<string, Candidate>>(CandidatesPath) ?? new();
                candidates.TryAdd(intent.Id, new(intent)); // Never renew entry deadlines on retry.
                DiskCache.Save(CandidatesPath, candidates);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Autotrading handoff failed for {Id}; no broker execution confirmed", intent.Id);
        }
    }

    public bool Publish(IReadOnlyList<QualificationLogEntry> mainEntries,
        IReadOnlyList<VtbSignalService.VtbLogEntry> vtbEntries,
        Func<string?, TelegramDeliveryReceipt?> mainReceipt,
        Func<string?, TelegramDeliveryReceipt?> vtbReceipt, string outputPath, DateTime? atUtc = null)
    {
        if (!config.GetValue<bool>("AutoTrading:Enabled")) return false;
        try
        {
            var secret = config["AutoTrading:FeedSigningKey"];
            if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
                throw new InvalidOperationException("A private feed-signing key of at least 32 bytes is required");
            var now = atUtc ?? DateTime.UtcNow;
            List<Intent> approved = new();
            lock (gate)
            {
                var candidates = DiskCache.Load<Dictionary<string, Candidate>>(CandidatesPath) ?? new();
                candidates = candidates.Where(p => p.Value.Signal.QualifiedAtUtc >= now.AddDays(-2))
                    .OrderByDescending(p => p.Value.Signal.QualifiedAtUtc).Take(512).ToDictionary();
                DiskCache.Save(CandidatesPath, candidates);
                foreach (var candidate in candidates.Values)
                {
                    var s = candidate.Signal;
                    if (!MarketHours.NewEntriesAllowed(now) || s.QualifiedAtUtc > now || now > s.EntryDeadlineUtc ||
                        s.NotificationId is null || s.CloseByUtc <= now) continue;
                    bool stillOpen;
                    TelegramDeliveryReceipt? receipt;
                    if (s.Flow == "main")
                    {
                        stillOpen = mainEntries.Any(e => e.Status == "Open" && e.EntryNotificationId == s.NotificationId &&
                            e.Symbol == s.Symbol && e.Entry == s.Entry && e.Stop == s.Stop &&
                            e.Tp1 == s.Targets[0] && e.Tp2 == s.Targets[1] && e.Tp3 == s.Targets[2]);
                        receipt = mainReceipt(s.NotificationId);
                    }
                    else
                    {
                        stillOpen = vtbEntries.Any(e => "vtb:" + e.Id == s.Id && e.Status == "Open");
                        receipt = vtbReceipt(s.NotificationId);
                    }
                    if (!stillOpen || receipt is not { Status: "Delivered", DeliveredAtUtc: not null }) continue;
                    approved.Add(s with { TelegramDeliveredAtUtc = receipt.DeliveredAtUtc });
                }
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, generatedAtUtc = now, signals = approved }, Json);
            var signedBytes = Encoding.UTF8.GetBytes("realtouch-demo-feed-v1\n").Concat(payload).ToArray();
            var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedBytes)).ToLowerInvariant();
            var envelope = JsonSerializer.Serialize(new { payload = Convert.ToBase64String(payload), signature }, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(outputPath + ".tmp", envelope);
            File.Move(outputPath + ".tmp", outputPath, overwrite: true);
            logger.LogInformation("Demo execution feed published: {Count} fresh Telegram-delivered entries", approved.Count);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Demo execution feed unavailable; no new broker entry is authorized");
            return false;
        }
    }
}
