using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum SetupModelType { TrendContinuationPullback, BreakoutAndRetest, LiquiditySweepReversal, RangeBoundaryRejection }
public enum SetupDirection { Long, Short }

public enum RequirementStatus { Met, NotMet, NotEvaluated }

// Every requirement a model checks is recorded explicitly - NotEvaluated is
// used (never silently treated as Met) when this implementation doesn't yet
// have the data to check a spec requirement (e.g. multi-timeframe alignment,
// which needs Weekly/Daily/4H candles fetched together - not wired yet).
public record RequirementCheck(string Description, RequirementStatus Status);

public record SetupCandidate(
    SetupModelType Model,
    SetupDirection Direction,
    Timeframe Timeframe,
    IReadOnlyList<RequirementCheck> Requirements
)
{
    // Qualified only if every checked requirement is Met AND none are NotMet.
    // A candidate with any NotEvaluated requirement is never "qualified" -
    // it is surfaced as a partial/watchlist candidate so nothing is silently
    // upgraded past unchecked criteria.
    public bool AllCheckedRequirementsMet => Requirements.All(r => r.Status != RequirementStatus.NotMet);
    public bool FullyEvaluated => Requirements.All(r => r.Status != RequirementStatus.NotEvaluated);
    public bool Qualified => AllCheckedRequirementsMet && FullyEvaluated;
}

// Section 15: the four approved setup models. Each method returns a
// SetupCandidate only when the model's basic precondition (Market Condition)
// holds; otherwise null (the model doesn't apply at all, distinct from
// "evaluated and failed"). Entry/stop/target/R:R (sections 16-18) are NOT
// computed here - that is explicitly listed as NotEvaluated on every
// candidate produced by this file, pending the next implementation phase.
public static class SetupModels
{
    private const string HtfAlignmentPending = "Weekly/Daily or Daily/4H directional alignment - not evaluated (multi-timeframe candle fetch not yet wired)";

    // HTF alignment (section 7) and R:R (sections 16-18) are optional
    // parameters: pass null when that data isn't available yet (single-
    // timeframe callers, or older test call sites) and the requirement stays
    // honestly NotEvaluated; pass a real value once computed (see
    // Services/SignalOrchestrator.cs) and it becomes a genuine Met/NotMet.
    private static RequirementCheck EvaluateHtf(HtfAlignment? htf) => htf switch
    {
        null => new(HtfAlignmentPending, RequirementStatus.NotEvaluated),
        Strategy.HtfAlignment.Conflicting => new("Weekly/Daily or Daily/4H directional alignment", RequirementStatus.NotMet),
        _ => new("Weekly/Daily or Daily/4H directional alignment", RequirementStatus.Met) // Aligned or Neutral both proceed per section 7
    };

    private static RequirementCheck EvaluateRr(decimal? rewardToRisk) => rewardToRisk switch
    {
        null => new("Minimum 2:1 projected reward-to-risk", RequirementStatus.NotEvaluated),
        >= 2m => new("Minimum 2:1 projected reward-to-risk", RequirementStatus.Met),
        _ => new("Minimum 2:1 projected reward-to-risk", RequirementStatus.NotMet)
    };

    // Section 19 requires "no active hard news veto" to qualify at all - a
    // Conflicting HTF check dents the score but an active HardVeto blocks
    // qualification outright. Unavailable is treated the same as not-supplied
    // (NotEvaluated, blocking) rather than silently assumed clear - we never
    // claim "no veto" when we couldn't actually check.
    private static RequirementCheck EvaluateNewsVeto(CalendarVetoState? veto) => veto switch
    {
        null or CalendarVetoState.Unavailable => new("No active hard news/economic-calendar veto", RequirementStatus.NotEvaluated),
        CalendarVetoState.HardVeto => new("No active hard news/economic-calendar veto", RequirementStatus.NotMet),
        _ => new("No active hard news/economic-calendar veto", RequirementStatus.Met)
    };

    public static SetupCandidate? EvaluateTrendContinuationPullback(
        IReadOnlyList<NormalizedCandle> candles, Timeframe timeframe, MarketCondition condition, StructureResult structure,
        IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<RealtouchWillisZone> willisZones,
        IReadOnlyList<LiquiditySweep> sweeps, HtfAlignment? htfAlignment = null, decimal? rewardToRisk = null,
        CalendarVetoState? calendarVeto = null)
    {
        if (condition != MarketCondition.TrendingBullish && condition != MarketCondition.TrendingBearish) return null;
        var direction = condition == MarketCondition.TrendingBullish ? SetupDirection.Long : SetupDirection.Short;
        var completed = CompletedOnly(candles);
        var lastClose = completed[^1].Close;

        var range = PremiumDiscount.BuildRange(candles, timeframe, structure);
        var locationReq = EvaluateLocation(range, lastClose, direction);

        var zoneReq = EvaluateZoneInteraction(lastClose, orderBlocks, fvgs, willisZones, direction);

        var sweepReq = new RequirementCheck(
            "Liquidity sweep or clear rejection into the zone",
            sweeps.Any(s => MatchesDirection(s.Direction, direction)) ? RequirementStatus.Met : RequirementStatus.NotMet);

        // Fixed: previously required the SINGLE last structure event (of
        // ANY direction) to match - a real, still-tradeable pullback lost
        // this gate the moment any later opposite-direction noise event
        // occurred after the real confirming one. Search recent events for
        // the most recent MATCHING-direction one instead.
        var matchingEvent = structure.Events.LastOrDefault(e => MatchesEventDirection(e.Type, direction));
        var structureReq = new RequirementCheck(
            "Lower-timeframe CHoCH/BOS confirming the pullback direction",
            matchingEvent is not null ? RequirementStatus.Met : RequirementStatus.NotMet);

        var requirements = new List<RequirementCheck>
        {
            new("Confirmed trending Market Condition", RequirementStatus.Met),
            EvaluateHtf(htfAlignment),
            locationReq,
            zoneReq,
            sweepReq,
            structureReq,
            EvaluateRr(rewardToRisk),
            EvaluateNewsVeto(calendarVeto)
        };

        return new SetupCandidate(SetupModelType.TrendContinuationPullback, direction, timeframe, requirements);
    }

    // Fixed real audit finding: previously required the CURRENT single-scan
    // Market Condition to still equal BreakoutBullish/Bearish - but the
    // classifier only reports Breakout for the ~3 candles immediately after
    // the break, and a real retest usually develops well after that window
    // closes, by which point the condition has already relabeled as
    // Trending/Ranging. Detection now searches recent structure history
    // directly (via ModelEvidenceBuilder) instead of depending on the
    // classifier's current-instant label - condition is still used for
    // ROUTING (which models to try first) at the orchestrator level, not as
    // a hard gate here.
    public static SetupCandidate? EvaluateBreakoutAndRetest(
        IReadOnlyList<NormalizedCandle> candles, Timeframe timeframe, StructureResult structure,
        IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<FairValueGap> fvgs, decimal? rewardToRisk = null,
        CalendarVetoState? calendarVeto = null, SetupDirection? preferredDirection = null)
    {
        // Try the preferred direction first (from the current condition, when
        // it still says Breakout), else whichever direction has the more
        // recent confirmed break.
        var candidateDirections = preferredDirection is not null
            ? new[] { preferredDirection.Value }
            : new[] { SetupDirection.Long, SetupDirection.Short };

        BreakoutEvidence? bestEvidence = null;
        SetupDirection bestDirection = SetupDirection.Long;
        foreach (var dir in candidateDirections)
        {
            var evidence = ModelEvidenceBuilder.BuildBreakout(candles, timeframe, structure, orderBlocks, fvgs, dir);
            if (!evidence.BreakoutEventFound) continue;
            if (bestEvidence is null || evidence.BreakoutCandleTimeUtc > bestEvidence.BreakoutCandleTimeUtc)
            {
                bestEvidence = evidence;
                bestDirection = dir;
            }
        }

        if (bestEvidence is null)
        {
            return new SetupCandidate(SetupModelType.BreakoutAndRetest, SetupDirection.Long, timeframe, new[]
            {
                new RequirementCheck("A confirmed breakout BOS exists on this timeframe", RequirementStatus.NotMet)
            });
        }

        var direction = bestDirection;
        var e = bestEvidence;

        var retestZoneReq = new RequirementCheck(
            "Retest of the broken level, FVG, or breakout order block", e.RetestReached ? RequirementStatus.Met : RequirementStatus.NotMet);

        var notChasing = e.BreakoutAtrMultiple <= 3m;
        var chaseReq = new RequirementCheck(
            "Entry does not chase an already-extended breakout (within 3x ATR of the break level)",
            notChasing ? RequirementStatus.Met : RequirementStatus.NotMet);

        var requirements = new List<RequirementCheck>
        {
            new("Confirmed breakout Market Condition with displacement", RequirementStatus.Met),
            retestZoneReq,
            chaseReq,
            EvaluateRr(rewardToRisk),
            EvaluateNewsVeto(calendarVeto)
        };

        return new SetupCandidate(SetupModelType.BreakoutAndRetest, direction, timeframe, requirements);
    }

    // Fixed real audit finding: previously required the CHoCH to be the
    // literal LAST structure event overall - a real, still-tradeable
    // reversal lost this gate the moment any later event (even a boring
    // continuation BOS) occurred after it. Detection now searches recent
    // history (via ModelEvidenceBuilder) for the most recent matching CHoCH,
    // independent of what happened afterward, same fix as Breakout above.
    public static SetupCandidate? EvaluateLiquiditySweepReversal(
        IReadOnlyList<NormalizedCandle> candles, Timeframe timeframe, MarketCondition condition, StructureResult structure,
        IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<LiquiditySweep> sweeps,
        HtfAlignment? htfAlignment = null, CalendarVetoState? calendarVeto = null, IReadOnlyList<KeyLevel>? keyLevels = null)
    {
        ReversalEvidence? bestEvidence = null;
        SetupDirection bestDirection = SetupDirection.Long;
        foreach (var dir in new[] { SetupDirection.Long, SetupDirection.Short })
        {
            var evidence = ModelEvidenceBuilder.BuildReversal(candles, structure, sweeps, orderBlocks, fvgs, keyLevels ?? Array.Empty<KeyLevel>(), dir);
            if (!evidence.ChochFound) continue;
            if (bestEvidence is null || evidence.ChochCandleTimeUtc > bestEvidence.ChochCandleTimeUtc)
            {
                bestEvidence = evidence;
                bestDirection = dir;
            }
        }
        if (bestEvidence is null) return null;

        var direction = bestDirection;
        var e = bestEvidence;

        var sweepReq = new RequirementCheck("External liquidity sweep preceding the CHoCH", e.SweepConfirmed ? RequirementStatus.Met : RequirementStatus.NotMet);
        var chochReq = new RequirementCheck("CHoCH confirmed in the new direction", RequirementStatus.Met);
        var evidenceReq = new RequirementCheck(
            "Displacement created a valid FVG or order block in the new direction",
            fvgs.Any(f => MatchesFvgDirection(f.Direction, direction) && f.Status != MitigationStatus.Invalidated) ||
            orderBlocks.Any(o => MatchesOrderBlockDirection(o.Direction, direction) && OrderBlockDetector.IsValid(o))
                ? RequirementStatus.Met : RequirementStatus.NotMet);
        var controlledRetestReq = new RequirementCheck(
            "Entry on the controlled retest", e.ControlledRetestReached ? RequirementStatus.Met : RequirementStatus.NotMet);

        // A reversal AGAINST the broader Weekly trend is real, elevated risk -
        // this only reports whether that's been accounted for (real risk
        // reduction happens in SignalOrchestrator's position sizing once
        // htfAlignment is known), not whether the trade happens to align.
        var riskAdjustmentReq = new RequirementCheck(
            "Reduced risk when against the broader Weekly trend",
            htfAlignment is not null ? RequirementStatus.Met : RequirementStatus.NotEvaluated);

        var requirements = new List<RequirementCheck>
        {
            EvaluateHtf(htfAlignment), // "Weekly, Daily or 4H key level" context
            sweepReq,
            chochReq,
            evidenceReq,
            controlledRetestReq,
            riskAdjustmentReq,
            EvaluateNewsVeto(calendarVeto)
        };

        return new SetupCandidate(SetupModelType.LiquiditySweepReversal, direction, timeframe, requirements);
    }

    public static SetupCandidate? EvaluateRangeBoundaryRejection(
        IReadOnlyList<NormalizedCandle> candles, Timeframe timeframe, MarketCondition condition, StructureResult structure,
        IReadOnlyList<LiquiditySweep> sweeps, decimal? rewardToRisk = null, CalendarVetoState? calendarVeto = null,
        decimal? targetPrice = null)
    {
        if (condition != MarketCondition.Ranging) return null;

        var completed = CompletedOnly(candles);
        const int rangeLookback = 20;
        var window = completed.Skip(Math.Max(0, completed.Count - rangeLookback)).ToList();
        var rangeHigh = window.Max(c => c.High);
        var rangeLow = window.Min(c => c.Low);
        var equilibrium = (rangeHigh + rangeLow) / 2;
        var lastClose = completed[^1].Close;
        var atr = Indicators.Atr(completed, 14)[^1];

        var nearHigh = atr is not null && Math.Abs(lastClose - rangeHigh) <= atr.Value * 0.5m;
        var nearLow = atr is not null && Math.Abs(lastClose - rangeLow) <= atr.Value * 0.5m;
        if (!nearHigh && !nearLow)
        {
            return new SetupCandidate(SetupModelType.RangeBoundaryRejection, SetupDirection.Long, timeframe, new[]
            {
                new RequirementCheck("Price is near a range boundary, not equilibrium", RequirementStatus.NotMet)
            });
        }

        var direction = nearHigh ? SetupDirection.Short : SetupDirection.Long;
        var boundaryReq = new RequirementCheck("Entry only near the range boundary, never equilibrium", RequirementStatus.Met);

        var sweepOrRejection = sweeps.Any(s => MatchesDirection(s.Direction, direction));
        var sweepReq = new RequirementCheck(
            "Boundary liquidity sweep or strong rejection",
            sweepOrRejection ? RequirementStatus.Met : RequirementStatus.NotMet);

        var lastEvent = structure.Events.LastOrDefault();
        var confirmReq = new RequirementCheck(
            "Lower-timeframe confirmation",
            lastEvent is not null && MatchesEventDirection(lastEvent.Type, direction) ? RequirementStatus.Met : RequirementStatus.NotMet);

        var notDuringBreakout = new RequirementCheck(
            "Not during a confirmed breakout", RequirementStatus.Met); // guarded by the Ranging precondition above

        // The logical objective for a boundary rejection is the range's own
        // equilibrium or its opposite boundary - not some arbitrary ATR
        // extension. Only checkable once a real target exists (Pass 2, after
        // EntryStopTargetCalculator has run), same pattern as R:R below.
        var oppositeBoundary = direction == SetupDirection.Long ? rangeHigh : rangeLow;
        var targetTolerance = atr is null ? decimal.MaxValue : atr.Value * 0.5m;
        var logicalTargetReq = targetPrice is null
            ? new RequirementCheck("Logical target at equilibrium or opposite boundary", RequirementStatus.NotEvaluated)
            : new RequirementCheck("Logical target at equilibrium or opposite boundary",
                Math.Abs(targetPrice.Value - equilibrium) <= targetTolerance || Math.Abs(targetPrice.Value - oppositeBoundary) <= targetTolerance
                    ? RequirementStatus.Met : RequirementStatus.NotMet);

        var requirements = new List<RequirementCheck> { boundaryReq, sweepReq, confirmReq, notDuringBreakout,
            logicalTargetReq,
            EvaluateRr(rewardToRisk),
            EvaluateNewsVeto(calendarVeto) };

        return new SetupCandidate(SetupModelType.RangeBoundaryRejection, direction, timeframe, requirements);
    }

    // --- shared helpers ---

    private static List<NormalizedCandle> CompletedOnly(IReadOnlyList<NormalizedCandle> candles) =>
        candles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok).OrderBy(c => c.OpenTimeUtc).ToList();

    private static bool MatchesDirection(SweepDirection sweep, SetupDirection setup) =>
        (sweep == SweepDirection.Bullish && setup == SetupDirection.Long) ||
        (sweep == SweepDirection.Bearish && setup == SetupDirection.Short);

    private static bool MatchesEventDirection(StructureEventType type, SetupDirection setup) =>
        setup == SetupDirection.Long
            ? type is StructureEventType.BullishBos or StructureEventType.BullishChoch
            : type is StructureEventType.BearishBos or StructureEventType.BearishChoch;

    private static bool MatchesFvgDirection(FvgDirection fvg, SetupDirection setup) =>
        (fvg == FvgDirection.Bullish && setup == SetupDirection.Long) || (fvg == FvgDirection.Bearish && setup == SetupDirection.Short);

    private static bool MatchesOrderBlockDirection(OrderBlockDirection ob, SetupDirection setup) =>
        (ob == OrderBlockDirection.Bullish && setup == SetupDirection.Long) || (ob == OrderBlockDirection.Bearish && setup == SetupDirection.Short);

    private static RequirementCheck EvaluateLocation(DealingRange? range, decimal price, SetupDirection direction)
    {
        const string desc = "Pullback into discount (longs) or premium (shorts)";
        if (range is null) return new RequirementCheck(desc, RequirementStatus.NotEvaluated);
        var zone = PremiumDiscount.Classify(range, price);
        var met = direction == SetupDirection.Long ? zone == PriceZone.Discount : zone == PriceZone.Premium;
        return new RequirementCheck(desc, met ? RequirementStatus.Met : RequirementStatus.NotMet);
    }

    private static RequirementCheck EvaluateZoneInteraction(
        decimal price, IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<FairValueGap> fvgs,
        IReadOnlyList<RealtouchWillisZone> willisZones, SetupDirection direction)
    {
        const string desc = "Interaction with fresh demand/supply, order block, FVG, or Willis Zone";
        var inWillis = willisZones.Any(z => MatchesOrderBlockDirection(z.Direction, direction) &&
            price >= Math.Min(z.ProximalBoundary, z.DistalBoundary) && price <= Math.Max(z.ProximalBoundary, z.DistalBoundary));
        var inOb = orderBlocks.Any(o => OrderBlockDetector.IsValid(o) && MatchesOrderBlockDirection(o.Direction, direction) &&
            price >= Math.Min(o.ProximalBoundary, o.DistalBoundary) && price <= Math.Max(o.ProximalBoundary, o.DistalBoundary));
        var inFvg = fvgs.Any(f => f.Status != MitigationStatus.Invalidated && MatchesFvgDirection(f.Direction, direction) &&
            price >= f.Lower && price <= f.Upper);

        return new RequirementCheck(desc, (inWillis || inOb || inFvg) ? RequirementStatus.Met : RequirementStatus.NotMet);
    }

    private static bool NearAnyZone(decimal price, IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<FairValueGap> fvgs, SetupDirection direction, decimal tolerance)
    {
        var obNear = orderBlocks.Any(o => MatchesOrderBlockDirection(o.Direction, direction) &&
            price >= Math.Min(o.ProximalBoundary, o.DistalBoundary) - tolerance && price <= Math.Max(o.ProximalBoundary, o.DistalBoundary) + tolerance);
        var fvgNear = fvgs.Any(f => MatchesFvgDirection(f.Direction, direction) &&
            price >= f.Lower - tolerance && price <= f.Upper + tolerance);
        return obNear || fvgNear;
    }
}
