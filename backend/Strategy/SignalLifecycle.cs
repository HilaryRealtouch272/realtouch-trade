namespace RealtouchSmartTrade.Api.Strategy;

public enum SignalState
{
    Scanning, Watchlist, Armed, Triggered, Active,
    Tp1Reached, Tp2Reached, Tp3Reached,
    Stopped, Invalidated, Expired, Cancelled
}

public record SignalTransition(
    SignalState From, SignalState To, DateTime TimestampUtc,
    decimal MarketPrice, string Reason, string DataSource
);

// Section 26: pure state-transition logic. No storage/persistence layer
// exists yet to hold open signals across restarts - that's a separate
// infrastructure decision (database/table choice) not made yet. These
// functions are ready to be called by whatever eventually owns that state;
// they never rewrite history, only ever append a new transition forward in
// time from the current state and the latest price.
public static class SignalLifecycle
{
    public static SignalState InitialState(int confluenceScore) =>
        confluenceScore >= 65 ? SignalState.Watchlist : SignalState.Scanning;

    public static SignalTransition? Advance(
        SignalState current, TradePlan plan, bool isQualified, decimal price, DateTime nowUtc, string dataSource)
    {
        var isLong = plan.Entry.PreferredEntry < plan.Targets.Tp1; // targets are above entry for a long

        switch (current)
        {
            case SignalState.Scanning:
                return null; // advancing to Watchlist/Armed happens via InitialState + the qualification check below

            case SignalState.Watchlist:
                if (isQualified)
                    return new(current, SignalState.Armed, nowUtc, price, "Setup reached qualification threshold (score >= 75, 5+ families, valid R:R)", dataSource);
                return null;

            case SignalState.Armed:
                if (nowUtc > plan.Entry.ExpiryUtc)
                    return new(current, SignalState.Expired, nowUtc, price, $"Price did not retrace into the entry zone within the expiry window ({plan.Entry.ExpiryUtc:u})", dataSource);
                if (BeyondInvalidation(plan, isLong, price))
                    return new(current, SignalState.Invalidated, nowUtc, price, "Price closed through the invalidation level before triggering", dataSource);
                if (plan.Entry.Triggered)
                    return new(current, SignalState.Triggered, nowUtc, price, "Entry trigger confirmed (price in zone + matching structure event)", dataSource);
                return null;

            case SignalState.Triggered:
                return new(current, SignalState.Active, nowUtc, price, "Position considered open at the triggered entry", dataSource);

            case SignalState.Active:
                if (HitStop(plan, isLong, price))
                    return new(current, SignalState.Stopped, nowUtc, price, $"Price reached the stop level {plan.Stop.Price} ({plan.Stop.Reason})", dataSource);
                if (ReachedTarget(plan.Targets.Tp1, isLong, price))
                    return new(current, SignalState.Tp1Reached, nowUtc, price, $"Price reached TP1 ({plan.Targets.Tp1Basis})", dataSource);
                return null;

            case SignalState.Tp1Reached:
                if (HitStop(plan, isLong, price)) // stop should already be at/near breakeven by policy once TP1 hits
                    return new(current, SignalState.Stopped, nowUtc, price, "Price reached the (breakeven-adjusted) stop after TP1", dataSource);
                if (ReachedTarget(plan.Targets.Tp2, isLong, price))
                    return new(current, SignalState.Tp2Reached, nowUtc, price, $"Price reached TP2 ({plan.Targets.Tp2Basis})", dataSource);
                return null;

            case SignalState.Tp2Reached:
                if (ReachedTarget(plan.Targets.Tp3, isLong, price))
                    return new(current, SignalState.Tp3Reached, nowUtc, price, $"Price reached TP3 ({plan.Targets.Tp3Basis})", dataSource);
                return null;

            default:
                return null; // Tp3Reached, Stopped, Invalidated, Expired, Cancelled are terminal
        }
    }

    private static bool BeyondInvalidation(TradePlan plan, bool isLong, decimal price) =>
        isLong ? price < plan.Entry.InvalidationPrice : price > plan.Entry.InvalidationPrice;

    private static bool HitStop(TradePlan plan, bool isLong, decimal price) =>
        isLong ? price <= plan.Stop.Price : price >= plan.Stop.Price;

    private static bool ReachedTarget(decimal target, bool isLong, decimal price) =>
        isLong ? price >= target : price <= target;

    public static bool IsTerminal(SignalState state) => state is
        SignalState.Tp3Reached or SignalState.Stopped or SignalState.Invalidated or
        SignalState.Expired or SignalState.Cancelled;
}
