namespace RealtouchSmartTrade.Api.Strategy;

public record ConfluenceFamilyScore(string Family, int Points, int MaxPoints, string Basis);

public record ConfluenceScoreResult(int TotalScore, string Grade, IReadOnlyList<ConfluenceFamilyScore> Families)
{
    public int IndependentFamilyCount => Families.Count(f => f.Points > 0);
}

// Inputs needed to score one candidate. Kept as plain data (not a direct
// dependency on SetupCandidate) so this is independently testable.
// HtfAlignment, CalendarVeto and NewsCatalyst are all optional (null = "not
// evaluated for this call") so existing callers that don't supply them keep
// getting an honest, explicitly-labeled degraded score rather than a silent
// full mark or a hard failure.
public record ScoringInput(
    MarketCondition Condition,
    SetupDirection Direction,
    bool LocationQualified,        // premium/discount requirement met (section 13)
    bool LiquidityEventPresent,    // a matching-direction sweep exists
    StructureEventType? EntryStructureEvent,
    string? ZoneSource,            // "WillisZone" / "OrderBlock" / "FairValueGap" / null
    bool DisplacementAtEntry,
    decimal? RewardToRisk,
    HtfAlignment? HtfAlignment = null,
    CalendarVetoState? CalendarVeto = null,
    NewsCatalystState? NewsCatalyst = null
);

// Section 19: the 100-point model. Every family's basis is stated explicitly,
// including "not evaluated" ones (session/news/macro - sections 21-23 aren't
// wired yet) - those score 0 rather than being silently omitted, so the total
// visibly reflects what's genuinely missing rather than looking falsely complete.
public static class ConfluenceScorer
{
    public static ConfluenceScoreResult Score(ScoringInput input)
    {
        var families = new List<ConfluenceFamilyScore>
        {
            ScoreHtfContext(input),
            ScoreLocation(input),
            ScoreLiquidityEvent(input),
            ScoreStructure(input),
            ScoreZoneQuality(input),
            ScoreMomentum(input),
            ScoreSessionNewsMacro(input),
            ScoreRewardToRisk(input),
        };

        var total = families.Sum(f => f.Points);
        var grade = total switch
        {
            >= 90 => "A+",
            >= 85 => "A",
            >= 75 => "B",
            >= 65 => "Watchlist",
            _ => "No setup"
        };

        return new ConfluenceScoreResult(total, grade, families);
    }

    private static ConfluenceFamilyScore ScoreHtfContext(ScoringInput i)
    {
        const int max = 15;
        const string family = "Higher-timeframe context and Market Condition";

        // A real, opposing multi-timeframe verdict overrides everything else in
        // this family - per section 7, a genuine conflict should suppress the
        // setup's HTF score regardless of what the single-timeframe condition says.
        if (i.HtfAlignment == Strategy.HtfAlignment.Conflicting)
            return new(family, 0, max, $"Real multi-timeframe context CONFLICTS with this direction (single-timeframe condition: {i.Condition})");

        var matchesDirection =
            (i.Direction == SetupDirection.Long && i.Condition is MarketCondition.TrendingBullish or MarketCondition.BreakoutBullish) ||
            (i.Direction == SetupDirection.Short && i.Condition is MarketCondition.TrendingBearish or MarketCondition.BreakoutBearish);

        if (matchesDirection)
        {
            return i.HtfAlignment switch
            {
                Strategy.HtfAlignment.Aligned => new(family, max, max, $"Condition matches direction ({i.Condition}) AND real multi-timeframe context is Aligned"),
                Strategy.HtfAlignment.Neutral => new(family, 12, max, $"Condition matches direction ({i.Condition}); HTF context is Neutral (no opinion either way)"),
                null => new(family, 10, max, $"Condition matches direction ({i.Condition}); single-timeframe proxy only - true HTF alignment (section 7) was not supplied for this score")
            };
        }

        if (i.Condition == MarketCondition.ReversalDeveloping)
        {
            return i.HtfAlignment switch
            {
                Strategy.HtfAlignment.Aligned => new(family, 10, max, "Reversal developing; real multi-timeframe context is Aligned"),
                Strategy.HtfAlignment.Neutral => new(family, 8, max, "Reversal developing; HTF context is Neutral"),
                null => new(family, 8, max, "Reversal developing; single-timeframe proxy only - true HTF alignment was not supplied")
            };
        }

        return new(family, 0, max, $"Condition does not support this direction ({i.Condition})");
    }

    private static ConfluenceFamilyScore ScoreLocation(ScoringInput i)
    {
        const int max = 15;
        const string family = "Key level and premium/discount location";
        return i.LocationQualified
            ? new(family, max, max, "Price is in the required discount (long) or premium (short) zone")
            : new(family, 0, max, "Price is not in the required premium/discount zone, or no dealing range could be built");
    }

    private static ConfluenceFamilyScore ScoreLiquidityEvent(ScoringInput i)
    {
        const int max = 15;
        const string family = "Liquidity event";
        return i.LiquidityEventPresent
            ? new(family, max, max, "A confirmed, matching-direction liquidity sweep is present")
            : new(family, 0, max, "No confirmed liquidity sweep found for this direction");
    }

    private static ConfluenceFamilyScore ScoreStructure(ScoringInput i)
    {
        const int max = 20;
        const string family = "BOS, CHoCH and entry structure";
        return i.EntryStructureEvent switch
        {
            StructureEventType.BullishChoch or StructureEventType.BearishChoch => new(family, max, max, "Confirmed CHoCH in the setup's direction"),
            StructureEventType.BullishBos or StructureEventType.BearishBos => new(family, 16, max, "Confirmed BOS (continuation) in the setup's direction"),
            _ => new(family, 0, max, "No confirmed structure event in the setup's direction")
        };
    }

    private static ConfluenceFamilyScore ScoreZoneQuality(ScoringInput i)
    {
        const int max = 15;
        const string family = "Order block, FVG or Willis Zone quality";
        return i.ZoneSource switch
        {
            "WillisZone" => new(family, max, max, "Realtouch Willis Zone (order block + FVG overlap + retracement) present"),
            "OrderBlock" => new(family, 10, max, "Valid order block present (no Willis Zone / FVG overlap)"),
            "FairValueGap" => new(family, 6, max, "Fair value gap present (no order block)"),
            _ => new(family, 0, max, "No qualifying zone found")
        };
    }

    private static ConfluenceFamilyScore ScoreMomentum(ScoringInput i)
    {
        const int max = 10;
        const string family = "Momentum, displacement and participation";
        if (i.DisplacementAtEntry) return new(family, max, max, "Entry is backed by a displacement candle");
        if (i.EntryStructureEvent is not null) return new(family, 5, max, "Structure event present without a qualifying displacement candle");
        return new(family, 0, max, "No displacement or structure event to support momentum");
    }

    private static ConfluenceFamilyScore ScoreSessionNewsMacro(ScoringInput i)
    {
        const int max = 5;
        const string family = "Session, news and macro alignment";

        if (i.CalendarVeto is null && i.NewsCatalyst is null)
            return new(family, 0, max, "Not evaluated - no calendar/news state was supplied for this score");

        // A hard veto zeroes this family outright - the orchestrator additionally
        // blocks the setup from qualifying at all when this is active (section 19:
        // "no active hard news veto" is a qualification requirement, not just a
        // scoring input).
        if (i.CalendarVeto == CalendarVetoState.HardVeto)
            return new(family, 0, max, "Hard economic-calendar veto is active - see invalidation conditions");

        var points = 0;
        var reasons = new List<string>();

        switch (i.NewsCatalyst)
        {
            case NewsCatalystState.Aligned: points += 3; reasons.Add("news catalyst Aligned"); break;
            case NewsCatalystState.Mixed: points += 1; reasons.Add("news catalyst Mixed"); break;
            case NewsCatalystState.Conflict: reasons.Add("news catalyst Conflicts (0 pts)"); break;
            case NewsCatalystState.Unchecked: reasons.Add("news catalyst Unchecked (0 pts)"); break;
            case NewsCatalystState.Unavailable: reasons.Add("news Unavailable (0 pts)"); break;
        }

        switch (i.CalendarVeto)
        {
            case CalendarVetoState.NoVeto: points += 2; reasons.Add("no calendar veto active"); break;
            case CalendarVetoState.Caution: points += 1; reasons.Add("medium-impact event caution"); break;
            case CalendarVetoState.Unavailable: reasons.Add("calendar Unavailable (0 pts)"); break;
        }

        return new(family, Math.Min(points, max), max, reasons.Count > 0 ? string.Join("; ", reasons) : "No calendar/news signal");
    }

    private static ConfluenceFamilyScore ScoreRewardToRisk(ScoringInput i)
    {
        const int max = 5;
        const string family = "Reward-to-risk quality";
        if (i.RewardToRisk is null) return new(family, 0, max, "Reward-to-risk could not be computed");
        if (i.RewardToRisk >= 3) return new(family, max, max, $"R:R {i.RewardToRisk:0.0} >= 3:1");
        if (i.RewardToRisk >= 2) return new(family, 3, max, $"R:R {i.RewardToRisk:0.0} >= 2:1");
        return new(family, 0, max, $"R:R {i.RewardToRisk:0.0} is below the 2:1 minimum");
    }
}
