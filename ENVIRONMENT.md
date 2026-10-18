# Environment

## Required to run locally

- .NET 10 SDK
- Node.js + npm (Angular CLI 18 used at build time; installed via `npx`, no global install needed)

## Setting secrets

.NET does not read `.env` files. Use one of these (see `backend/.env.example`
for the full variable list):

```powershell
cd backend
dotnet user-secrets set "TwelveData:ApiKeys" "key1,key2,key3"
dotnet user-secrets set "Marketaux:ApiKeys" "key1"
dotnet user-secrets set "AlphaVantage:ApiKeys" "key1,key2"
dotnet user-secrets set "Telegram:BotToken" "..."
dotnet user-secrets set "Telegram:ChatId" "..."
```

Secrets are stored outside the repo at:
```
%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json
```
(the `<UserSecretsId>` GUID is in `backend/RealtouchSmartTrade.Api.csproj`).

In production (and in the scheduled GitHub Action), the same values are set as
real environment variables with `__` nesting (`TwelveData__ApiKeys=...`) -
see "GitHub Actions secrets" below for the exact names this repo's workflow
reads.

## Required/optional keys

| Variable | Required for | Free tier notes |
|---|---|---|
| `TwelveData:ApiKey` / `TwelveData:ApiKeys` | FX/Metals live data | 800 credits/day/key. Multiple comma-separated keys are round-robin rotated, and a key confirmed out of daily credits is skipped until the next UTC day. |
| (none) | Economic calendar | The calendar feed is keyless (FairEconomy weekly JSON). |
| `Marketaux:ApiKey` / `Marketaux:ApiKeys` | News (primary, scored source) | Small free daily request limit; only scores articles tied to a stock ticker, so most FX/metals/crypto headlines come back unscored (kept as context only). |
| `AlphaVantage:ApiKey` / `AlphaVantage:ApiKeys` | News (scored fallback when Marketaux has no scored item) | 25 requests/day/key on the free tier; multiple keys rotate the same way as Twelve Data's. |
| `Fred:ApiKeys` | Not used by any code yet | Stored for future use only. |
| `Telegram:BotToken` / `Telegram:ChatId` | Real signal and outcome alerts, sent automatically by every scan | Required for the bot to actually notify you - without it, scans and the ledger still run, but nothing is sent. |

Crypto (Coinbase) and GDELT (news context) need no key at all - both are free
and keyless.

## GitHub Actions secrets (for the scheduled workflow)

`.github/workflows/scan-and-deploy.yml` reads these exact secret names (set
under Settings -> Secrets and variables -> Actions):

```
TWELVEDATA__APIKEYS
ALPHAVANTAGE__APIKEYS
MARKETAUX__APIKEYS
TELEGRAM__BOTTOKEN
TELEGRAM__CHATID
```

There is no `FINNHUB__APIKEYS` secret anymore - the calendar no longer uses
Finnhub (see `DATA_SOURCES.md`), so it can be deleted if it still exists.

## Running

```powershell
# Terminal 1
cd backend
dotnet run          # http://localhost:5266

# Terminal 2
cd frontend
npm start            # http://localhost:4200

# One-shot scan (what the scheduled GitHub Action actually runs)
dotnet run --project backend -- --scan-once

# Backtest a pair's live-engine rules over real history
dotnet run --project backend -- --backtest BTC/USDT 1H 60
dotnet run --project backend -- --backtest-shadow BTC/USDT 1H 60
```

## Running tests

```powershell
cd backend.Tests
dotnet test
```

## Network caveats specific to this deployment (see DATA_SOURCES.md)

- `api.binance.com` / `api.binance.us`: return HTTP 451 from this network -
  Binance's own IP-based geo-block, unrelated to authentication.
- `api.kraken.com`: silently DNS-timed-out on this network's default resolver
  (worked fine against `8.8.8.8`) - DNS-level filtering, not an outage.
- `api.exchange.coinbase.com`: resolves and works fine on this network as of
  the last check, and is what crypto actually runs on now.

If this is ever deployed to a different network/host, re-run the DNS/HTTP
checks documented in `DATA_SOURCES.md` before assuming any of the above still
holds - none of it is guaranteed by the providers, it was discovered
empirically on one specific network.
