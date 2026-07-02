# testnet-exec

Execution-side experiments against the Binance Spot **testnet** (fake funds, no real money). Two tracks: a from-scratch C# WebSocket order-entry engine, and a quick ccxt plumbing check in Bun/TS.

Write-up: **https://trading.ggez.work/exec/**

## `cs/` — WS order-entry execution engine (C#, .NET 10)

A from-scratch execution engine over Binance's WebSocket order-entry API — no ccxt, no SDK. It hand-rolls the parts a wrapper hides: request signing, the order lifecycle, and queue position.

| File | What |
|------|------|
| `WsOrderClient` | Raw ws-api client — HMAC signing over **alphabetically-sorted** params (the WS API sorts, REST doesn't), id↔response correlation, self-healing clock skew |
| `OwnOrderBook` | Idempotent own-order state machine keyed by clientOrderId; illegal transitions rejected, not applied |
| `HybridBook` | Delta-applied market book — flat circular hot-window (O(1) near touch) + SortedDictionary far-map |
| `MarketFeed` | Snapshot + diff-stream bootstrap into the book |
| `EventReactor` | Async single-consumer event reactor + a sample imbalance `Strategy` base class |
| `Program` | Three modes |

**Modes**

```bash
cd cs
dotnet run                 # full loop: resting place→cancel ×5 + a real fill (marketable IOC → flatten)
MODE=book  dotnet run      # overlay an own order on the live book, read FIFO queue position
MODE=react dotnet run      # async reactor: a Strategy reacts to the live book stream
```

Demonstrates: own protocol signing · idempotent order submit · own-order state machine + own-trade handling · FIFO queue position · production error paths (`-1021` clock-skew self-heal, `-2011` cancel-already-gone, rate-limit backoff) · async event-driven reactor.

## `binance-plumbing.ts` — ccxt plumbing check (Bun/TS)

```bash
bun run binance-plumbing.ts
```

The fast-to-ship counterpart: place/cancel resting limits over ccxt (unified across venues), latency percentiles, error classification. ccxt is a high-level wrapper — fine for a plumbing check, not for the protocol-level depth the C# engine shows.

## Run

Get testnet keys at [testnet.binance.vision](https://testnet.binance.vision/) and export them (no keys are stored in this repo):

```bash
export BINANCE_TESTNET_KEY=...
export BINANCE_TESTNET_SECRET=...
```

Requires .NET 10 SDK (for `cs/`) and [Bun](https://bun.sh) (for the ccxt check).
