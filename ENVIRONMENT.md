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
dotnet user-secrets set "AlphaVantage:ApiKeys" "key1,key2"
dotnet user-secrets set "Telegram:BotToken" "..."
dotnet user-secrets set "Telegram:ChatId" "..."
```

Secrets are stored outside the repo at:
```
%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json
```
(the `<UserSecretsId>` GUID is in `backend/RealtouchSmartTrade.Api.csproj`).

In production, use real environment variables with `__` nesting
(`TwelveData__ApiKeys=...`) or your host's secret manager instead.

## Required/optional keys

| Variable | Required for | Free tier notes |
|---|---|---|
| `TwelveData:ApiKey` / `TwelveData:ApiKeys` | FX/Metals live data | 800 credits/day/key, ~8 req/min/key. Multiple comma-separated keys rotate on failure. |
| (none) | Economic calendar | The calendar feed is keyless (FairEconomy weekly JSON). |
| `AlphaVantage:ApiKey` / `AlphaVantage:ApiKeys` | News + sentiment (adapter built, not wired to any endpoint yet) | 25 requests/day/key on the free tier. |
| `Fred:ApiKeys` | Not used by any code yet | Stored for future use only. |
| `Telegram:BotToken` / `Telegram:ChatId` | `POST /api/telegram/test` only | No automated alerts are sent - manual connectivity test only. |

Bybit (crypto) needs no key at all - its public market-data endpoint is free
and keyless.

## Running

```powershell
# Terminal 1
cd backend
dotnet run          # http://localhost:5266

# Terminal 2
cd frontend
npm start            # http://localhost:4200
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
- `api.bybit.com`: resolves and works fine on this network as of the last check.

If this is ever deployed to a different network/host, re-run the DNS/HTTP
checks documented in `DATA_SOURCES.md` before assuming any of the above still
holds - none of it is guaranteed by the providers, it was discovered
empirically on one specific network.
