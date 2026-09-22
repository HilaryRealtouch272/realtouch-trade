namespace RealtouchSmartTrade.Api.Strategy;

// Section 12: a raw win rate from a handful of trades is not a reliable
// estimate - the Wilson score interval gives an honest confidence range
// instead of pretending a point estimate from 7 trades means anything on
// its own. Standard 95% Wilson score interval for a binomial proportion.
public record WilsonInterval(double PointEstimate, double Lower, double Upper, int SampleSize)
{
    // Section 12's explicit thresholds: fewer than 30 trades, any
    // conclusion is provisional; 50-100+ before touching core thresholds.
    public bool IsProvisional => SampleSize < 30;
    public string SampleSizeWarning => SampleSize switch
    {
        < 30 => $"Provisional - only {SampleSize} resolved trade(s). Section 12 requires at least 30 before an initial strategy assessment, and 50-100 before changing core thresholds.",
        < 50 => $"Early sample - {SampleSize} resolved trades. Preferably 50-100 before treating this as reliable enough to change core thresholds.",
        < 100 => $"Growing sample - {SampleSize} resolved trades. Approaching the 50-100 range section 12 recommends before adjusting core thresholds.",
        _ => $"{SampleSize} resolved trades - a reasonable sample size per section 12."
    };
}

public static class WilsonIntervalCalculator
{
    // z = 1.96 for a 95% confidence interval.
    private const double Z = 1.96;

    public static WilsonInterval Compute(int wins, int total)
    {
        if (total <= 0) return new WilsonInterval(0, 0, 0, 0);

        var p = (double)wins / total;
        var z2 = Z * Z;
        var denominator = 1 + z2 / total;
        var center = p + z2 / (2 * total);
        var margin = Z * Math.Sqrt(p * (1 - p) / total + z2 / (4 * total * total));

        var lower = (center - margin) / denominator;
        var upper = (center + margin) / denominator;

        return new WilsonInterval(p, Math.Max(0, lower), Math.Min(1, upper), total);
    }
}
