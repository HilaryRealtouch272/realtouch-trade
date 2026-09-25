# Data Sources

All fetches happen server-side in the .NET backend. No API key is ever sent
to the frontend or committed to the repository.

| Provider | Used for | Auth | Status |
|---|---|---|---|
| Bybit v5 public REST (`api.bybit.com`) | Crypto (BTC/USDT, ETH/USDT, spot + linear-futures) | None (free, keyless) | Live |
| Twelve Data (`api.twelvedata.com`) | FX (EUR/USD, GBP/USD, GBP/JPY), Metals (XAU/USD), Energy (none - see below) | Free API key(s) (`TwelveData:ApiKey` / `TwelveData:ApiKeys`, comma-separated, rotated round-robin on failure) | Live for FX + Gold on free tier. **XAG/USD and WTI/USD are confirmed (via real error responses) to require a paid Grow/Venture plan; BRENT/USD is confirmed to be an invalid symbol string on Twelve Data.** All three are skipped entirely in `MarketDataService.KnownUnavailableSymbols` - no network call is made for them, so they cost zero quota. Requests for the remaining 20 combos (4 instruments × 5 timeframes) are paced ~2s apart to stay under each key's ~8 req/min cap. |
| FairEconomy weekly calendar (`nfs.faireconomy.media/ff_calendar_thisweek.json`) | Economic calendar | None (keyless) | Live. Unofficial mirror of the ForexFactory calendar: current week only, forecast/previous but no actual, no uptime promise. Failure is reported as Unavailable and never blocks a signal. Replaced Finnhub. |
| Alpha Vantage News & Sentiment (`alphavantage.co`) | News + per-asset sentiment/relevance | Free API key(s) (`AlphaVantage:ApiKey` / `AlphaVantage:ApiKeys`) | Adapter built (`Providers/AlphaVantageNewsProvider.cs`), not yet wired into any endpoint. Same honest-unavailable behavior. |
| FRED (St. Louis Fed) | Slower macro context | Free API key(s) (`Fred:ApiKeys` stored, no adapter yet) | Not yet built. |
| TradingView embeds | Visual chart reference only | N/A | Used in the UI for display; never read as a strategy data source (no scraping, no pixel-reading). |

## Normalized candle schema

Every market-data provider is converted to `Models/NormalizedCandle.cs`:

```
CanonicalSymbol, ProviderSymbol, Provider, Timeframe,
OpenTimeUtc, CloseTimeUtc, Open, High, Low, Close, Volume,
IsComplete, ReceivedAtUtc, Quality (Ok/Stale/Gap/Duplicate/OutOfOrder)
```

`IsComplete` is false for the currently-forming candle on both Bybit and
Twelve Data (their last returned bar is provisional) — the structure engine
filters these out before confirming any pivot, BOS, or CHoCH.

## Crypto provider history: Binance → Kraken → Bybit

- **Binance** (`api.binance.com` and `api.binance.us`): both returned HTTP
  451 ("service unavailable from a restricted location") — an IP-based geo
  block from Binance's own servers. Authenticating with a real API key does
  not bypass this; it's unrelated to authentication.
- **Kraken** (`api.kraken.com`): the HTTP block wasn't an issue, but the
  user's ISP/router DNS resolver silently timed out on that domain
  specifically (confirmed by resolving fine against `8.8.8.8` while the
  default resolver hung) — DNS-level filtering of crypto-exchange domains,
  a different mechanism than Binance's block, fixable by switching the
  machine's DNS servers but not otherwise worked around in code.
- **Bybit** (`api.bybit.com`): resolves fine on the user's network via the
  default resolver, and its v5 public kline endpoint is free and keyless
  like Kraken's was, with interval codes that map onto all five required
  timeframes (`15`,`60`,`240`,`D`,`W`). Currently in use.

If this moves to a different network/host, re-run the same DNS/HTTP checks
before assuming Bybit is universally unblocked — every step above was
discovered empirically, not from documentation.
