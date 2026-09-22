namespace RealtouchSmartTrade.Api.Strategy;

// Section 6: every model gets its own weight table and its own qualification
// threshold - NOT the single shared 100-point profile in ConfluenceScore.cs
// (kept, untouched, for the old Trend-only call sites/tests still depending
// on its exact numbers). This is the live scoring path the orchestrator now
// calls for all four models, Trend included, since the brief's Trend table
// differs from ConfluenceScorer's.
//
// Section 11's grade bands, parametrized by each model's own threshold so
// "B" only starts at THAT model's threshold (75 or 78) - a 76 on a
// threshold-78 model is Tracking, not B, exactly as section 11 states.
public static class ModelScoring
{
    public static string GradeFor(int total, int threshold) => total switch
    {
        >= 90 => "A+",
        >= 85 => "A",
        _ when total >= threshold => "B",
        >= 65 => "Tracking",
        _ => "No setup"
    };

    private static ConfluenceFamilyScore Family(string name, int points, int max, string basis) => new(name, Math.Clamp(points, 0, max), max, basis);

    private static ConfluenceFamilyScore ScoreRewardToRisk(decimal? rr, int max)
    {
        const string name = "Reward-to-risk quality";
        if (rr is null) return Family(name, 0, max, "Reward-to-risk could not be computed");
        if (rr >= 3) return Family(name, max, max, $"R:R {rr:0.0} >= 3:1");
        if (rr >= 2) return Family(name, (int)Math.Round(max * 0.6m), max, $"R:R {rr:0.0} >= 2:1");
        return Family(name, 0, max, $"R:R {rr:0.0} is below the 2:1 minimum");
    }

    private static ConfluenceFamilyScore ScoreSessionNewsMacro(CalendarVetoState? veto, NewsCatalystState? news, int max)
    {
        const string name = "Session, news and macro alignment";
        if (veto is null && news is null) return Family(name, 0, max, "Not evaluated - no calendar/news state was supplied for this score");
        if (veto == CalendarVetoState.HardVeto) return Family(name, 0, max, "Hard economic-calendar veto is active");

        var half = max / 2m;
        var points = 0m;
        var reasons = new List<string>();
        switch (news)
        {
            case NewsCatalystState.Aligned: points += half * 0.6m; reasons.Add("news catalyst Aligned"); break;
            case NewsCatalystState.Mixed: points += half * 0.2m; reasons.Add("news catalyst Mixed"); break;
            default: reasons.Add("no favorable news signal"); break;
        }
        switch (veto)
        {
            case CalendarVetoState.NoVeto: points += half * 0.4m; reasons.Add("no calendar veto active"); break;
            case CalendarVetoState.Caution: points += half * 0.2m; reasons.Add("medium-impact event caution"); break;
            default: reasons.Add("calendar Unavailable"); break;
        }
        return Family(name, (int)Math.Round(points), max, string.Join("; ", reasons));
    }

    // --- 6.1 Trend Continuation Pullback (75/15/10/15/15/10/15/10/5/5) ---
    public static ConfluenceScoreResult ScoreTrendContinuation(
        HtfAlignment? htfAlignment, bool locationQualified, string? zoneSource, bool liquidityEventPresent,
        StructureEventType? entryStructureEvent, bool displacementAtEntry, decimal? rewardToRisk,
        CalendarVetoState? calendarVeto, NewsCatalystState? newsCatalyst)
    {
        var families = new List<ConfluenceFamilyScore>
        {
            htfAlignment switch
            {
                Strategy.HtfAlignment.Conflicting => Family("Higher-timeframe directional alignment", 0, 15, "Real multi-timeframe context CONFLICTS with this direction"),
                Strategy.HtfAlignment.Aligned => Family("Higher-timeframe directional alignment", 15, 15, "Real multi-timeframe context is Aligned"),
                Strategy.HtfAlignment.Neutral => Family("Higher-timeframe directional alignment", 12, 15, "HTF context is Neutral (no opinion either way)"),
                null => Family("Higher-timeframe directional alignment", 10, 15, "Single-timeframe proxy only - true HTF alignment not supplied")
            },
            Family("Trending Market Condition quality", 10, 10, "Confirmed trending Market Condition (mandatory gate for this model)"),
            locationQualified
                ? Family("Pullback into premium/discount location", 15, 15, "Price is in the required discount (long) or premium (short) zone")
                : Family("Pullback into premium/discount location", 0, 15, "Price is not in the required premium/discount zone"),
            zoneSource switch
            {
                "WillisZone" => Family("Order block, FVG, demand/supply or Willis Zone", 15, 15, "Realtouch Willis Zone (order block + FVG overlap + retracement) present"),
                "OrderBlock" => Family("Order block, FVG, demand/supply or Willis Zone", 10, 15, "Valid order block present (no Willis Zone / FVG overlap)"),
                "FairValueGap" => Family("Order block, FVG, demand/supply or Willis Zone", 6, 15, "Fair value gap present (no order block)"),
                _ => Family("Order block, FVG, demand/supply or Willis Zone", 0, 15, "No qualifying zone found")
            },
            liquidityEventPresent
                ? Family("Liquidity sweep or rejection", 10, 10, "A confirmed, matching-direction liquidity sweep is present")
                : Family("Liquidity sweep or rejection", 0, 10, "No confirmed liquidity sweep found for this direction"),
            entryStructureEvent switch
            {
                StructureEventType.BullishChoch or StructureEventType.BearishChoch => Family("Lower-timeframe BOS/CHoCH confirmation", 15, 15, "Confirmed CHoCH in the setup's direction"),
                StructureEventType.BullishBos or StructureEventType.BearishBos => Family("Lower-timeframe BOS/CHoCH confirmation", 12, 15, "Confirmed BOS (continuation) in the setup's direction"),
                _ => Family("Lower-timeframe BOS/CHoCH confirmation", 0, 15, "No confirmed structure event in the setup's direction")
            },
            displacementAtEntry
                ? Family("Displacement and participation", 10, 10, "Entry is backed by a displacement candle")
                : entryStructureEvent is not null
                    ? Family("Displacement and participation", 5, 10, "Structure event present without a qualifying displacement candle")
                    : Family("Displacement and participation", 0, 10, "No displacement or structure event to support momentum"),
            ScoreRewardToRisk(rewardToRisk, 5),
            ScoreSessionNewsMacro(calendarVeto, newsCatalyst, 5)
        };

        var total = families.Sum(f => f.Points);
        return new ConfluenceScoreResult(total, GradeFor(total, 75), families);
    }

    // --- 6.2 Breakout and Retest (75/15/20/10/20/15/10/5/5) ---
    public static ConfluenceScoreResult ScoreBreakoutAndRetest(BreakoutEvidence e, bool displacementAtEntry, decimal? rewardToRisk, CalendarVetoState? calendarVeto, NewsCatalystState? newsCatalyst)
    {
        var families = new List<ConfluenceFamilyScore>
        {
            e.BreakoutEventFound
                ? Family("Quality and maturity of range or boundary", 15, 15, "A confirmed structural boundary was broken")
                : Family("Quality and maturity of range or boundary", 0, 15, "No confirmed boundary break found in the detection window"),
            Family("Breakout-close strength", (int)Math.Round(e.BreakoutBodyRatio * 20), 20, $"Breaking candle body ratio {e.BreakoutBodyRatio:P0}"),
            displacementAtEntry
                ? Family("Displacement and participation", 10, 10, "Entry is backed by a displacement candle")
                : Family("Displacement and participation", 0, 10, "No qualifying displacement candle"),
            e.RetestReached
                ? Family("Retest quality", 20, 20, "Price has returned to the broken level or its evidence zone")
                : Family("Retest quality", 0, 20, "Price has not yet retested the broken level"),
            e.ContinuationConfirmed
                ? Family("Rejection or continuation confirmation", 15, 15, "A real structure event confirms continuation after the retest")
                : Family("Rejection or continuation confirmation", 0, 15, "No confirming structure event after the retest"),
            e.ClearSpaceToOpposingLiquidity
                ? Family("Clear space to opposing liquidity", 10, 10, $"Breakout distance {e.BreakoutAtrMultiple:0.00}x ATR - meaningful space covered")
                : Family("Clear space to opposing liquidity", 0, 10, "Breakout distance is not yet meaningful relative to ATR"),
            ScoreRewardToRisk(rewardToRisk, 5),
            ScoreSessionNewsMacro(calendarVeto, newsCatalyst, 5)
        };

        var total = families.Sum(f => f.Points);
        return new ConfluenceScoreResult(total, GradeFor(total, 75), families);
    }

    // --- 6.3 Liquidity-Sweep Reversal (78/20/20/10/20/15/5/5/5) ---
    public static ConfluenceScoreResult ScoreLiquiditySweepReversal(ReversalEvidence e, bool reversalZoneEvidence, decimal? rewardToRisk, CalendarVetoState? calendarVeto, NewsCatalystState? newsCatalyst)
    {
        var families = new List<ConfluenceFamilyScore>
        {
            e.ReversalLocationSignificant
                ? Family("Weekly, Daily or 4-hour reversal location", 20, 20, "The swept level coincides with a real catalogued key level")
                : Family("Weekly, Daily or 4-hour reversal location", 0, 20, "No significant higher-timeframe level behind the sweep"),
            e.SweepConfirmed
                ? Family("External liquidity-sweep quality", (int)Math.Round(e.SweepQuality * 20), 20, $"Sweep quality {e.SweepQuality:P0} of full ATR-scaled distance")
                : Family("External liquidity-sweep quality", 0, 20, "No confirmed external liquidity sweep"),
            e.CloseBackThroughSweptLevel
                ? Family("Completed close back through swept level", 10, 10, "A completed candle closed back through the swept level")
                : Family("Completed close back through swept level", 0, 10, "No completed close back through the swept level yet"),
            e.ChochFound
                ? Family("CHoCH confirmation", 20, 20, "Confirmed CHoCH in the new direction")
                : Family("CHoCH confirmation", 0, 20, "No confirmed CHoCH"),
            reversalZoneEvidence
                ? Family("Displacement, FVG or reversal order block", 15, 15, "Displacement created a valid FVG or order block in the new direction")
                : Family("Displacement, FVG or reversal order block", 0, 15, "No qualifying displacement evidence"),
            e.ControlledRetestReached
                ? Family("Controlled retest quality", 5, 5, "Entry is on a controlled retest of the reversal evidence zone")
                : Family("Controlled retest quality", 0, 5, "Price has not returned to the reversal evidence zone"),
            ScoreRewardToRisk(rewardToRisk, 5),
            ScoreSessionNewsMacro(calendarVeto, newsCatalyst, 5)
        };

        var total = families.Sum(f => f.Points);
        return new ConfluenceScoreResult(total, GradeFor(total, 78), families);
    }

    // --- 6.4 Range Boundary Rejection (78/20/20/20/10/10/10/5/5) ---
    public static ConfluenceScoreResult ScoreRangeBoundaryRejection(RangeEvidence e, decimal? rewardToRisk, CalendarVetoState? calendarVeto, NewsCatalystState? newsCatalyst)
    {
        var families = new List<ConfluenceFamilyScore>
        {
            e.RangeEstablished
                ? Family("Confirmed range quality", (int)Math.Round(e.RangeQualityRatio * 20), 20, $"{e.RangeQualityRatio:P0} of the lookback window respected the range")
                : Family("Confirmed range quality", 0, 20, "No established range in the lookback window"),
            e.NearBoundaryNotEquilibrium
                ? Family("Entry location near range boundary", 20, 20, "Price is near a range boundary, not equilibrium")
                : Family("Entry location near range boundary", 0, 20, "Price is not near a range boundary"),
            e.BoundarySweepOrRejection
                ? Family("Boundary sweep or rejection", 20, 20, "A confirmed, matching-direction liquidity sweep or rejection is present at the boundary")
                : Family("Boundary sweep or rejection", 0, 20, "No confirmed sweep or rejection at the boundary"),
            Family("Ranging Market Condition and low-trend evidence", 10, 10, "Confirmed Ranging Market Condition (mandatory gate for this model)"),
            e.LowerTimeframeConfirmation
                ? Family("Lower-timeframe entry confirmation", 10, 10, "A real structure event confirms entry in this direction")
                : Family("Lower-timeframe entry confirmation", 0, 10, "No confirming structure event"),
            e.SpaceToEquilibriumOrOppositeBoundary
                ? Family("Space to equilibrium or opposite boundary", 10, 10, "Meaningful space exists to the opposite boundary")
                : Family("Space to equilibrium or opposite boundary", 0, 10, "Insufficient space to the opposite boundary"),
            ScoreRewardToRisk(rewardToRisk, 5),
            ScoreSessionNewsMacro(calendarVeto, newsCatalyst, 5)
        };

        var total = families.Sum(f => f.Points);
        return new ConfluenceScoreResult(total, GradeFor(total, 78), families);
    }
}
