namespace RealtouchSmartTrade.Api.Strategy;

public record PositionSizeResult(
    decimal RiskAmount,
    decimal PositionSize,
    string Units,
    decimal RiskPercentUsed,
    string Basis
);

// Section 20. Position sizing is correct for same-account-currency instruments
// (e.g. a USD account trading USDT pairs, or GBP account trading GBP/XXX).
// True cross-currency pip-value conversion (e.g. a GBP account trading
// EUR/USD) is NOT implemented - that needs a live FX conversion rate, which
// this calculator doesn't fetch. Flagged explicitly in the result's Basis
// rather than silently assumed away. Futures contract multipliers are also
// not modeled (none of the current instruments use one).
public static class RiskSizing
{
    public const decimal HardCapPercent = 2.0m;

    public static decimal DefaultRiskPercent(string grade) => grade switch
    {
        "A+" => 1.25m,
        "A" => 1.0m,
        "B" => 0.5m,
        _ => 0m // Tracking / No setup - no default risk, spec requires a qualified grade to size at all
    };

    public static PositionSizeResult Compute(
        decimal accountBalance,
        decimal requestedRiskPercent,
        string grade,
        decimal entry,
        decimal stop,
        bool isFx,
        decimal feePercent = 0,
        decimal slippagePercent = 0)
    {
        var riskPercent = requestedRiskPercent > 0 ? requestedRiskPercent : DefaultRiskPercent(grade);
        var cappedRisk = Math.Min(riskPercent, HardCapPercent); // leverage is never permission to exceed this
        var riskAmount = accountBalance * cappedRisk / 100m;
        var haircut = 1 - Math.Clamp((feePercent + slippagePercent) / 100m, 0, 0.5m);
        var effectiveRiskAmount = riskAmount * haircut;

        var priceDistance = Math.Abs(entry - stop);
        if (priceDistance == 0)
            return new(riskAmount, 0, isFx ? "units" : "quantity", cappedRisk, "Zero stop distance - cannot size a position");

        var size = effectiveRiskAmount / priceDistance;
        var units = isFx
            ? "units (same-account-currency approximation; cross-currency pip conversion not implemented)"
            : "quantity";
        var basis = $"Risk {cappedRisk:0.##}% of {accountBalance:0.00} = {riskAmount:0.00}, minus {feePercent + slippagePercent:0.##}% fee/slippage haircut, / price distance {priceDistance}";
        return new(riskAmount, size, units, cappedRisk, basis);
    }
}

// Section 20 portfolio safeguards, as pure functions over caller-supplied
// state. No persistence/tracking of open positions or daily PnL exists yet
// (that needs the signal-lifecycle storage layer, section 26) - these
// functions are ready to be wired to real state once that exists.
public record PortfolioCheckResult(bool Allowed, string Reason);

public static class PortfolioSafeguards
{
    public static PortfolioCheckResult CheckAggregateRisk(decimal currentOpenRiskPercent, decimal newRiskPercent, decimal maxAggregateRiskPercent)
    {
        var total = currentOpenRiskPercent + newRiskPercent;
        return total <= maxAggregateRiskPercent
            ? new(true, $"Aggregate open risk {total:0.##}% within the {maxAggregateRiskPercent}% limit")
            : new(false, $"Aggregate open risk would reach {total:0.##}%, exceeding the {maxAggregateRiskPercent}% limit");
    }

    public static PortfolioCheckResult CheckDailyLoss(decimal todaysRealizedLossPercent, decimal dailyLossLimitPercent)
    {
        return Math.Abs(todaysRealizedLossPercent) < dailyLossLimitPercent
            ? new(true, "Daily loss limit not reached")
            : new(false, $"Daily realized loss {todaysRealizedLossPercent:0.##}% has reached the {dailyLossLimitPercent}% limit");
    }

    public static PortfolioCheckResult CheckMaxSimultaneousPositions(int currentOpenPositions, int maxSimultaneousPositions)
    {
        return currentOpenPositions < maxSimultaneousPositions
            ? new(true, $"{currentOpenPositions}/{maxSimultaneousPositions} positions open")
            : new(false, $"Already at the maximum of {maxSimultaneousPositions} simultaneous positions");
    }

    public static PortfolioCheckResult CheckDuplicateSignal(IEnumerable<string> openSignalIds, string candidateSignalId)
    {
        return openSignalIds.Contains(candidateSignalId)
            ? new(false, "An identical signal is already open")
            : new(true, "No duplicate open signal");
    }

    public static PortfolioCheckResult CheckCorrelatedExposure(IEnumerable<string> openSymbols, string candidateSymbol, IReadOnlyDictionary<string, string[]> correlationGroups)
    {
        var group = correlationGroups.FirstOrDefault(kv => kv.Value.Contains(candidateSymbol));
        if (group.Value is null) return new(true, "No configured correlation group for this symbol");
        var overlap = openSymbols.Intersect(group.Value).ToList();
        return overlap.Count == 0
            ? new(true, "No correlated symbols currently open")
            : new(false, $"Correlated exposure already open in the '{group.Key}' group: {string.Join(", ", overlap)}");
    }
}
