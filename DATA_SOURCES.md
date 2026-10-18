# Data Sources

All fetches happen server-side in the .NET backend. No API key is ever sent
to the frontend or committed to the repository.

| Provider | Used for | Auth | Status |
|---|---|---|---|
| Coinbase Exchange public API (`api.exchange.coinbase.com`) | Crypto: BTC/USDT, ETH/USDT, CAKE/USDT, SOL/USDT (all quoted against the exchange's real `-USD` product) | None (free, keyless) | Live. Also the source for the backtester's historical candles and for the 1-minute settlement/reconciliation of open crypto trades. |
| Twelve Data (`api.twelvedata.com`) | FX: EUR/USD, GBP/USD, GBP/JPY, USD/JPY, AUD/USD, USD/CAD, USD/CHF, NZD/USD. Metals: XAU/USD. | Free API key(s) (`TwelveData:ApiKey` / `TwelveData:ApiKeys`, comma-separated, rotated round-robin and skipped once confirmed out of daily credits until the next UTC day) | Live. **XAG/USD and WTI/USD are confirmed (via real error responses) to require a paid Grow/Venture plan; BRENT/USD is confirmed to be an invalid symbol string on Twelve Data.** All three are skipped entirely in `TwelveDataMarketDataProvider.KnownUnavailableSymbols` - no network call is made for them. Requests are paced and timezone-forced to UTC. |
| FairEconomy weekly calendar (`nfs.faireconomy.media/ff_calendar_thisweek.json`) | Economic calendar, and the hard high-impact-event veto | None (keyless) | Live. Unofficial mirror of the ForexFactory calendar: current week only, forecast/previous but no actual, no uptime promise. Failure is reported as Unavailable and never blocks a signal - only a real event inside its veto window does. Replaced Finnhub. |
| Marketaux (`api.marketaux.com`) | News headlines, primary source | Free API key(s) (`Marketaux:ApiKey` / `Marketaux:ApiKeys`) | Live. Only scores articles tied to a stock ticker, so for FX, metals and most crypto it typically returns headlines with no sentiment - those are kept as context but never move the news state (see Alpha Vantage below). |
| Alpha Vantage News & Sentiment (`alphavantage.co`) | News + per-asset sentiment/relevance, asked whenever Marketaux returned no scored item | Free API key(s) (`AlphaVantage:ApiKey` / `AlphaVantage:ApiKeys`, rotated and skipped once confirmed out of its daily limit) | Live, as the scored fallback inside `CompositeNewsProvider`. |
| GDELT (`api.gdeltproject.org`) | Supplementary geopolitical/macro headlines, always added | None (keyless) | Live, but unscored - GDELT items carry no sentiment and can never move the news state on their own; they're shown as extra context only. |
| FRED (St. Louis Fed) | Slower macro context | Free API key(s) (`Fred:ApiKeys` stored) | Not built - no adapter exists. |
| Bybit v5 public REST (`api.bybit.com`) | Nothing currently - registered as an `IMarketDataProvider` but no catalog instrument routes to it | None (free, keyless) | Dead code, kept from before the engine moved to Coinbase for crypto. Candidate for removal. |
| TradingView embeds | Visual chart reference only | N/A | Used in the UI for display; never read as a strategy data source (no scraping, no pixel-reading). |

News is combined by `Providers/CompositeNewsProvider.cs`: Marketaux is tried
first, Alpha Vantage is asked whenever Marketaux produced no properly scored
item, and GDELT's headlines are always appended as unscored context. The news
state itself (`NewsCatalystEvaluator`) only counts headlines published within
the last 12 hours and needs at least 3 scored headlines before calling a
direction Aligned or Conflict (fewer is reported as "Thin"). **News never
decides whether a signal qualifies** - only a real calendar veto does.

## Normalized candle schema

Every market-data provider is converted to `Models/NormalizedCandle.cs`:

```
CanonicalSymbol, ProviderSymbol, Provider, Timeframe,
OpenTimeUtc, CloseTimeUtc, Open, High, Low, Close, Volume,
IsComplete, ReceivedAtUtc, Quality (Ok/Stale/Gap/Duplicate/OutOfOrder)
```

`IsComplete` is false only for a candle that genuinely hasn't closed yet in
real time (not merely "the last row returned") - the structure engine filters
these out before confirming any pivot, BOS, or CHoCH.

## Crypto provider history: Binance -> Kraken -> Bybit -> Coinbase

- **Binance** (`api.binance.com` and `api.binance.us`): both returned HTTP
  451 ("service unavailable from a restricted location") - an IP-based geo
  block from Binance's own servers. Authenticating with a real API key does
  not bypass this; it's unrelated to authentication.
- **Kraken** (`api.kraken.com`): the HTTP block wasn't an issue, but the
  user's ISP/router DNS resolver silently timed out on that domain
  specifically (confirmed by resolving fine against `8.8.8.8` while the
  default resolver hung) - DNS-level filtering of crypto-exchange domains.
- **Bybit** (`api.bybit.com`): worked at the time, free and keyless, but is
  no longer used by any instrument - see the table above.
- **Coinbase Exchange** (`api.exchange.coinbase.com`): free, keyless, real
  volume, a cheap 1-minute endpoint (used for settling open trades and for
  backtesting), and no geo-block observed. Currently in use for all crypto.

If this moves to a different network/host, re-run the same DNS/HTTP checks
before assuming any of the above is universally unblocked - every step above
was discovered empirically, not from documentation.
