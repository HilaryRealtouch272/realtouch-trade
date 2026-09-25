using System.Text.RegularExpressions;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// Turns a real SignalResult into a compact, icon-led Telegram message. Used
// both by the manual /api/telegram/send-sample endpoint and by the automatic
// SignalAlertService - one format, so a manual preview and a real alert look
// the same shape and are easy to tell apart only by the qualified banner.
public static class TelegramSignalFormatter
{
    // Decimal places scaled to price magnitude (a $77k BTC print doesn't need
    // 5 decimals; a sub-1 FX/metal-style price does) - callers don't carry
    // the instrument's own display-decimals setting, so this is a reasonable
    // general rule, not a per-instrument lookup. Public so any human-facing
    // Telegram message formats prices the same way, not just the initial
    // qualification alert (see SignalLogService.FormatOutcomeMessage).
    public static string FormatPrice(decimal v)
    {
        var abs = Math.Abs(v);
        var decimals = abs >= 100 ? 2 : abs >= 1 ? 4 : 6;
        return Math.Round(v, decimals).ToString($"F{decimals}");
    }

    public static string Format(SignalResult s, bool qualified)
    {
        var Fmt = FormatPrice;
        var directionIcon = s.Direction.ToString() == "Long" ? "🟢" : "🔴";
        var topConfluences = s.ConfluenceFamilies
            .Where(f => f.Points > 0)
            .OrderByDescending(f => f.Points)
            .Take(3)
            .Select(f => $"{FamilyIcon(f.Family)} {f.Basis}")
            .ToList();
        var invalidationShort = s.InvalidationConditions.Split(". ")[0];

        var header = qualified
            ? $"✅ *{s.Symbol}* · {s.AnalysisTimeframe} · {directionIcon} *{s.Direction.ToString().ToUpperInvariant()}*"
            : $"⚠️ *NOT QUALIFIED — PREVIEW ONLY*\n{s.Symbol} · {s.AnalysisTimeframe} · {directionIcon} {s.Direction.ToString().ToUpperInvariant()}";

        return
            $"{header}\n" +
            $"📊 *{s.Grade}* ({s.SetupQualityScore}/100) · {SpaceWords(s.SetupModel.ToString())}\n" +
            $"🧭 {SpaceWords(s.Condition.ToString())}\n\n" +
            $"🎯 Entry {Fmt(s.PreferredEntry)}  (zone {Fmt(s.EntryZoneMin)}–{Fmt(s.EntryZoneMax)})\n" +
            $"🛑 Stop {Fmt(s.Stop)}\n" +
            $"🏁 TP1 {Fmt(s.Tp1)} · TP2 {Fmt(s.Tp2)} · TP3 {Fmt(s.Tp3)}\n" +
            $"⚖️ R:R {s.RewardToRisk:0.0}\n\n" +
            $"{(topConfluences.Count > 0 ? string.Join("\n", topConfluences) : "• No confluence scored above zero")}\n\n" +
            $"📰 News: {ShortState(s.NewsState)}   📅 Calendar: {ShortState(s.EconomicCalendarState)}\n" +
            $"🚫 {invalidationShort}" +
            // The economic calendar feed being down no longer blocks a signal, so
            // say plainly that no news check happened - never imply it was clear.
            (s.EconomicCalendarState.StartsWith("Unavailable", StringComparison.OrdinalIgnoreCase)
                ? "\n\n⚠️ Economic calendar could not be checked. Check high-impact news yourself before entering."
                : "") +
            (s.ScoreFloorNote is null ? "" : $"\n\n⚠️ {s.ScoreFloorNote}") +
            (qualified ? "" : "\n\n⚠️ Not a trade recommendation.");
    }

    private static string SpaceWords(string pascalCase) => Regex.Replace(pascalCase, "([a-z0-9])([A-Z])", "$1 $2");

    // Long free-text state fields ("NoVeto: no active hard news/economic..."
    // or "Unchecked - no news state supplied...") reduced to just the leading
    // state word for a compact line - the full reason is only meaningful in
    // the dashboard, not a glanceable alert.
    private static string ShortState(string full) => full.Split(':', '-')[0].Trim();


    private static string FamilyIcon(string family) => family switch
    {
        _ when family.Contains("structure", StringComparison.OrdinalIgnoreCase) => "🧱",
        _ when family.Contains("liquidity", StringComparison.OrdinalIgnoreCase) => "💧",
        _ when family.Contains("zone", StringComparison.OrdinalIgnoreCase) => "📦",
        _ when family.Contains("higher-timeframe", StringComparison.OrdinalIgnoreCase) => "⏫",
        _ when family.Contains("momentum", StringComparison.OrdinalIgnoreCase) => "⚡",
        _ when family.Contains("session", StringComparison.OrdinalIgnoreCase) => "🗞️",
        _ when family.Contains("reward-to-risk", StringComparison.OrdinalIgnoreCase) => "⚖️",
        _ when family.Contains("location", StringComparison.OrdinalIgnoreCase) => "📍",
        _ => "•"
    };
}
