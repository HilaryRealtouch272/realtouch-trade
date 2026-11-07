using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RealtouchSmartTrade.Api.Models;

// The plan's identity is shared by real and beta even when their entry times differ.
public static class SignalIdentity
{
    public static string For(SignalResult signal)
    {
        if (signal.SourceSetupId is not null) return signal.SourceSetupId;
        var prices = new[] { signal.EntryZoneMin, signal.EntryZoneMax, signal.PreferredEntry,
            signal.Stop, signal.Tp1, signal.Tp2, signal.Tp3 };
        var plan = string.Join("|", signal.Symbol, signal.AnalysisTimeframe, signal.Direction, signal.SetupModel,
            signal.ExpiryUtc.ToUniversalTime().ToString("O"),
            string.Join("|", prices.Select(p => p.ToString("G29", CultureInfo.InvariantCulture))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan)))[..20];
    }
}
