namespace RealtouchSmartTrade.Api.Strategy;

// Section 10: how volume is treated is a deterministic function of the asset
// class and of whether reliable volume actually exists in the feed - never a
// guess and never a silent inflation of the score. The profile that produced a
// score is stored on every signal and ledger row.
//
// Only the 10-point "Displacement and participation" family (Trend and Breakout
// models) contains any participation weight; the other families and the other
// two models never use volume.
public record ScoringProfile(string Id, string VolumeSource, int ParticipationWeight)
{
    public const int FamilyWeight = 10;

    // Points of the family that are earned by displacement; the rest (if any)
    // reward volume expansion.
    public int DisplacementWeight => FamilyWeight - ParticipationWeight;

    // No volume used: displacement carries the whole family.
    public static readonly ScoringProfile Default = new("no-volume-v1", "none", 0);

    public static ScoringProfile For(string assetClass, bool reliableVolume) => (assetClass, reliableVolume) switch
    {
        // Exchange volume (e.g. Coinbase) where it is genuinely available.
        ("crypto", true) => new("crypto-exchange-volume-v1", "exchange", 3),
        // Crypto feed without usable volume: the 3 points go back to displacement.
        ("crypto", false) => new("crypto-volume-unavailable-v1", "none", 0),
        // FX, metals and energy: volume is absent or only tick counts of unclear
        // meaning, so it is not used; displacement is judged by candle body,
        // ATR expansion and follow-through.
        _ => new($"{(string.IsNullOrEmpty(assetClass) ? "generic" : assetClass)}-no-volume-v1", "none", 0)
    };
}
