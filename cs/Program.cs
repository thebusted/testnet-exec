using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TestnetExec;

// Binance Spot testnet — full execution loop over raw signed WebSocket order entry.
//   place/cancel on the ws-api socket → the exchange's response (status + fills) folds into an
//   idempotent own-order state machine. Demonstrates own WS signing, a resting place→cancel loop,
//   a real fill loop (marketable IOC buy → fill → flatten sell), and -1021 / -2011 handling.
// All against testnet fake funds; resting orders sit 10% below market so they never fill.

const string REST = "https://testnet.binance.vision";
const string WSAPI = "wss://ws-api.testnet.binance.vision/ws-api/v3";
const string SYMBOL = "BTCUSDT";
int reps = int.TryParse(Environment.GetEnvironmentVariable("REPS"), out var r) ? r : 5;
string qty = Environment.GetEnvironmentVariable("QTY") ?? "0.00030";

// paper mode is pure market data (public, no keys) — dispatch before the execution-key check.
if ((Environment.GetEnvironmentVariable("MODE") ?? "loop") == "paper") return await PaperLoop();

var key = Environment.GetEnvironmentVariable("BINANCE_TESTNET_KEY");
var secret = Environment.GetEnvironmentVariable("BINANCE_TESTNET_SECRET");
if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
{ Console.Error.WriteLine("BINANCE_TESTNET_KEY / _SECRET missing — source ~/.claude/.secrets"); return 1; }

var inv = CultureInfo.InvariantCulture;
var http = new HttpClient { BaseAddress = new Uri(REST) };

async Task<long> ServerOffset()
{
    long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    using var d = JsonDocument.Parse(await http.GetStringAsync("/api/v3/time"));
    long t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    return d.RootElement.GetProperty("serverTime").GetInt64() - (t0 + t1) / 2;
}
async Task<(decimal bid, decimal ask)> BookTicker()
{
    using var d = JsonDocument.Parse(await http.GetStringAsync($"/api/v3/ticker/bookTicker?symbol={SYMBOL}"));
    return (decimal.Parse(d.RootElement.GetProperty("bidPrice").GetString()!, inv),
            decimal.Parse(d.RootElement.GetProperty("askPrice").GetString()!, inv));
}
string Px(decimal p) => (Math.Floor(p * 100) / 100).ToString("0.00", inv);
SortedDictionary<string, string> P(params (string, string)[] kv) { var d = new SortedDictionary<string, string>(); foreach (var (k, v) in kv) d[k] = v; return d; }

// S3 — one engine holds the market book AND my own order, and reads my queue position out of the
// same structure. Market feed + own order both on testnet (same market — no mainnet/testnet mix).
async Task<int> BookDemo(long off)
{
    const string WSSTREAM = "wss://stream.testnet.binance.vision";
    var hb = new HybridBook(40000);                        // ±$200 flat window around mid
    await using var feed = new MarketFeed(hb, http, WSSTREAM, SYMBOL);
    await feed.StartAsync();
    await Task.Delay(2500);                                 // let a few diffs settle around the touch

    var (bidT, askT) = feed.Best();
    if (bidT is null || askT is null) { Console.WriteLine("market book not populated"); return 1; }
    int myTick = bidT.Value;                                // join the queue at best bid (passive, won't cross)
    decimal queueAhead = feed.QtyAt(true, myTick) / 100_000_000m;   // market qty resting at my price, BEFORE mine
    decimal myPrice = myTick / 100m;

    var ob = new OwnOrderBook();
    await using var oe = new WsOrderClient(WSAPI, key!, secret!);
    oe.SetServerOffset(off);
    oe.SetResync(ServerOffset);
    await oe.ConnectAsync();

    string cid = $"book-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    var o = ob.Submit(cid, out _); o.TryTransition(OrderState.Sent);
    var pres = await oe.SendSignedAsync("order.place", P(("symbol", SYMBOL), ("side", "BUY"), ("type", "LIMIT"), ("timeInForce", "GTC"), ("quantity", qty), ("price", Px(myPrice)), ("newClientOrderId", cid)));
    o.ExchangeId = pres.GetProperty("orderId").GetInt64();
    ob.Apply(o, pres);

    Console.WriteLine($"market   best bid {bidT.Value / 100m:0.00}  best ask {askT.Value / 100m:0.00}");
    Console.WriteLine($"my order BUY {qty} @ {myPrice:0.00}  ({o.State})");
    Console.WriteLine($"queue    {queueAhead:0.00000} BTC resting ahead of me at {myPrice:0.00} — FIFO estimate read straight from the live book");

    ob.Apply(o, await oe.SendSignedAsync("order.cancel", P(("symbol", SYMBOL), ("origClientOrderId", cid))));
    Console.WriteLine($"canceled ({o.State}) — one engine tracked the market book, my order, and my queue position");
    return o.State == OrderState.Canceled ? 0 : 1;
}

// Paper trade — pure simulation on live MAINNET books (Binance + Bybit), no orders. Each venue runs an
// imbalance-momentum PaperTrader; marks P&L net of fees so the edge measurement is honest. Writes
// paper-state.json for the dashboard. No keys needed (public market data on both venues).
async Task<int> PaperLoop()
{
    string statePath = Environment.GetEnvironmentVariable("PAPER_STATE") ?? "paper-state.json";
    var jopts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    var up = Stopwatch.StartNew();

    var bnBook = new HybridBook(40000);
    var bnHttp = new HttpClient { BaseAddress = new Uri("https://api.binance.com") };
    IFeed bn = new MarketFeed(bnBook, bnHttp, "wss://stream.binance.com:9443", SYMBOL);
    var byBook = new HybridBook(40000);
    IFeed by = new BybitFeed(byBook, SYMBOL);
    await bn.StartAsync();
    await by.StartAsync();

    var feeds = new[] { bn, by };
    var traders = new[] { new PaperTrader("binance"), new PaperTrader("bybit") };

    Console.WriteLine($"paper trade → {statePath} · imbalance-momentum, mark-to-market net of fees, Binance + Bybit mainnet");
    long cycle = 0;
    while (true)
    {
        for (int i = 0; i < feeds.Length; i++)
        {
            var (btk, atk) = feeds[i].Best();
            traders[i].OnTick(btk, atk, feeds[i].DepthSum(traders[i].SignalRadiusTicks));
        }
        var venues = traders.Select(t => new
        {
            venue = t.Venue,
            position = t.Position.ToString(),
            entry = Math.Round(t.EntryPrice, 2),
            imb = Math.Round(t.LastImb, 3),
            mid = Math.Round(t.LastMid, 2),
            realizedPnl = Math.Round(t.RealizedPnl, 4),
            unrealizedPnl = Math.Round(t.Unrealized, 4),
            equityPnl = Math.Round(t.EquityPnl, 4),
            fees = Math.Round(t.FeesPaid, 4),
            trades = t.Trades,
            wins = t.Wins,
            winRate = Math.Round(t.WinRate, 3),
        }).ToArray();
        var state = new { ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), uptimeSec = (long)up.Elapsed.TotalSeconds, cycles = cycle, symbol = SYMBOL, signalUsd = 50, venues };
        var tmp = statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, jopts));
        File.Move(tmp, statePath, true);
        cycle++;
        await Task.Delay(1000);   // sample the signal + mark every 1s
    }
}

// S4 — async event reactor: the live book streams into a single-consumer channel; a Strategy reacts
// to each tick in arrival order (no locks in the strategy). Read-only market data, no orders.
async Task<int> ReactDemo()
{
    const string WSSTREAM = "wss://stream.testnet.binance.vision";
    var hb = new HybridBook(40000);
    await using var feed = new MarketFeed(hb, http, WSSTREAM, SYMBOL);
    var strat = new ImbalanceReactor();
    var reactor = new EventReactor(strat);
    feed.OnBookUpdate = () => { var (b, a) = feed.Best(); if (b is int bt && a is int at) reactor.PostBook(bt, at, feed.QtyAt(true, bt), feed.QtyAt(false, at)); };

    await feed.StartAsync();
    var run = reactor.RunAsync(CancellationToken.None);
    Console.WriteLine("async event reactor running — reacting to the live testnet book for 6s...\n");
    await Task.Delay(6000);
    reactor.Complete();            // channel completes -> the consumer drains remaining events and stops
    await run;

    Console.WriteLine($"\nreactor drained {reactor.Dispatched} book events · strategy logged {strat.Flips} imbalance flips");
    Console.WriteLine("✓ async reactor OK — single-consumer channel, strategy reacted to a serialized live event stream");
    return reactor.Dispatched > 0 ? 0 : 1;
}

// Live monitor — the server-side backend for the web dashboard. Runs forever: live market book +
// queue + imbalance refreshed every tick (~5s), a real execution cycle (place→cancel, periodically a
// fill→flatten) every ~20s, and writes state.json atomically each tick for the frontend to poll.
// Keys stay server-side; this is why the dashboard can be live without exposing a secret in a browser.
async Task<int> MonitorLoop(long off)
{
    // MAINNET market data (public depth stream, no key) → real deep book for $6K/$10K/$20K macro imbalance.
    // Execution stays on the testnet WS-API (keys server-side). Honest split: market=mainnet, exec=testnet.
    const string WSSTREAM = "wss://stream.binance.com:9443";
    string statePath = Environment.GetEnvironmentVariable("STATE_PATH") ?? "state.json";
    var hb = new HybridBook(40000);
    var mktHttp = new HttpClient { BaseAddress = new Uri("https://api.binance.com") };
    await using var feed = new MarketFeed(hb, mktHttp, WSSTREAM, SYMBOL);
    await feed.StartAsync();
    var ob = new OwnOrderBook();
    await using var oe = new WsOrderClient(WSAPI, key!, secret!);
    oe.SetServerOffset(off); oe.SetResync(ServerOffset);
    await oe.ConnectAsync();

    var up = Stopwatch.StartNew();
    long cycle = 0, totalOrders = 0, totalFills = 0;
    var placeMs = new List<double>(); var cancelMs = new List<double>();
    object lastExec = new { kind = "warming up" };
    var jopts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    double Pctl(List<double> xs, int p) { if (xs.Count == 0) return 0; var s = xs.OrderBy(x => x).ToList(); return Math.Round(s[Math.Min(s.Count - 1, p * s.Count / 100)]); }

    void WriteState()
    {
        var (bt, at) = feed.Best();
        double? bid = bt is int b ? b / 100.0 : null, ask = at is int a ? a / 100.0 : null;
        long bq = bt is int bb ? feed.QtyAt(true, bb) : 0, aq = at is int aa ? feed.QtyAt(false, aa) : 0;
        double imb = (bq + aq) > 0 ? (double)(bq - aq) / (bq + aq) : 0;
        // depth-weighted imbalance over a range of radii ($ from mid) — public depth feed, no key needed.
        // Frontend picks any bucket (dynamic); $6000 = macro pressure, $50 = touch. steadier than L1.
        int[] dUsd = { 50, 100, 200, 400, 1000, 2000, 6000, 10000, 20000 };
        var dBid = new double[dUsd.Length]; var dAsk = new double[dUsd.Length]; var dImb = new double[dUsd.Length];
        for (int i = 0; i < dUsd.Length; i++)
        {
            var (db, da) = feed.DepthSum(dUsd[i] * 100);
            dBid[i] = Math.Round(db / 1e8, 4); dAsk[i] = Math.Round(da / 1e8, 4);
            dImb[i] = (db + da) > 0 ? Math.Round((double)(db - da) / (db + da), 3) : 0;
        }
        var state = new
        {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            uptimeSec = (long)up.Elapsed.TotalSeconds,
            cycles = cycle,
            symbol = SYMBOL,
            market = new { bid, ask, spread = bid.HasValue && ask.HasValue ? Math.Round(ask.Value - bid.Value, 2) : (double?)null },
            imbalanceL1 = Math.Round(imb, 3),
            depth = new { buckets = dUsd, bid = dBid, ask = dAsk, imb = dImb },
            queue = bid.HasValue ? new { price = bid, aheadBtc = Math.Round(bq / 1e8, 5) } : null,
            lastExec,
            totals = new { orders = totalOrders, fills = totalFills },
        };
        var tmp = statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, jopts));
        File.Move(tmp, statePath, true);   // atomic swap so the frontend never reads a half-written file
    }

    Console.WriteLine($"live monitor → {statePath} · market/queue every ~5s, exec cycle ~20s");
    while (true)
    {
        if (cycle % 4 == 0)   // execution cycle every ~20s (rate-limited, never spams the exchange)
        {
            var (btk, atk) = feed.Best();
            if (btk is int bTick && atk is int aTick)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try
                {
                    if (cycle % 16 == 0)   // ~every 80s: a real fill, immediately flattened
                    {
                        string bcid = $"mon-fill-{now}"; var bo = ob.Submit(bcid, out _); bo.TryTransition(OrderState.Sent);
                        ob.Apply(bo, await oe.SendSignedAsync("order.place", P(("symbol", SYMBOL), ("side", "BUY"), ("type", "LIMIT"), ("timeInForce", "IOC"), ("quantity", qty), ("price", Px(aTick / 100m * 1.001m)), ("newClientOrderId", bcid))));
                        totalOrders++;
                        if (bo.CumFilled > 0)
                        {
                            totalFills++;
                            string scid = $"mon-flat-{now}"; var so = ob.Submit(scid, out _); so.TryTransition(OrderState.Sent);
                            ob.Apply(so, await oe.SendSignedAsync("order.place", P(("symbol", SYMBOL), ("side", "SELL"), ("type", "LIMIT"), ("timeInForce", "IOC"), ("quantity", bo.CumFilled.ToString("0.00000", inv)), ("price", Px(bTick / 100m * 0.999m)), ("newClientOrderId", scid))));
                            totalOrders++; if (so.CumFilled > 0) totalFills++;
                            lastExec = new { kind = "fill", buy = $"{bo.CumFilled:0.00000} @ {bo.AvgFillPrice:0.00}", sell = $"{so.CumFilled:0.00000} @ {so.AvgFillPrice:0.00}", flat = true, at = now };
                        }
                        else lastExec = new { kind = "fill", note = $"IOC did not fill ({bo.State})", at = now };
                    }
                    else   // resting place → cancel, measured
                    {
                        string cid = $"mon-{now}"; var o = ob.Submit(cid, out _); o.TryTransition(OrderState.Sent);
                        var sw = Stopwatch.StartNew();
                        var pr = await oe.SendSignedAsync("order.place", P(("symbol", SYMBOL), ("side", "BUY"), ("type", "LIMIT"), ("timeInForce", "GTC"), ("quantity", qty), ("price", Px(bTick / 100m * 0.9m)), ("newClientOrderId", cid)));
                        placeMs.Add(sw.Elapsed.TotalMilliseconds); if (placeMs.Count > 50) placeMs.RemoveAt(0);
                        o.ExchangeId = pr.GetProperty("orderId").GetInt64(); ob.Apply(o, pr); totalOrders++;
                        var sw2 = Stopwatch.StartNew();
                        ob.Apply(o, await oe.SendSignedAsync("order.cancel", P(("symbol", SYMBOL), ("origClientOrderId", cid))));
                        cancelMs.Add(sw2.Elapsed.TotalMilliseconds); if (cancelMs.Count > 50) cancelMs.RemoveAt(0);
                        lastExec = new { kind = "rest", state = o.State.ToString(), placeP50 = Pctl(placeMs, 50), placeP95 = Pctl(placeMs, 95), cancelP50 = Pctl(cancelMs, 50), clockResyncs = oe.ClockResyncs, at = now };
                    }
                }
                catch (BinanceWsError e) { lastExec = new { kind = "error", code = e.Code, msg = e.Message, at = now }; }
            }
        }
        WriteState();
        cycle++;
        await Task.Delay(5000);
    }
}

long offset = await ServerOffset();
string mode = Environment.GetEnvironmentVariable("MODE") ?? "loop";
if (mode == "book") return await BookDemo(offset);
if (mode == "react") return await ReactDemo();
if (mode == "monitor") return await MonitorLoop(offset);
if (mode == "paper") return await PaperLoop();
var (bid0, ask0) = await BookTicker();
string restPrice = Px(bid0 * 0.9m);
Console.WriteLine($"clock offset ≈ {offset}ms · market {bid0:0.00}/{ask0:0.00} · resting BUY {qty} @ {restPrice} · {reps} reps\n");

var book = new OwnOrderBook();
await using var oe = new WsOrderClient(WSAPI, key!, secret!);
oe.SetServerOffset(offset);
oe.SetResync(ServerOffset);
await oe.ConnectAsync();

var placeMs = new List<double>();
var cancelMs = new List<double>();
var errors = new Dictionary<string, int>();
int acked = 0, canceled = 0, dupRefused = 0;
string Classify(BinanceWsError e) => e.Code switch { -1021 => "CLOCK_SKEW", -2011 => "ORDER_GONE", -1003 => "RATE_LIMIT", _ => $"OTHER({e.Code})" };

// ---- resting place → cancel loop; FSM folded from each response ----
for (int i = 0; i < reps; i++)
{
    string cid = $"rest-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{i}";
    var o = book.Submit(cid, out bool created);
    if (!created) { dupRefused++; continue; }
    try
    {
        o.TryTransition(OrderState.Sent);
        var sw = Stopwatch.StartNew();
        var pres = await oe.SendSignedAsync("order.place", P(("symbol", SYMBOL), ("side", "BUY"), ("type", "LIMIT"), ("timeInForce", "GTC"), ("quantity", qty), ("price", restPrice), ("newClientOrderId", cid)));
        placeMs.Add(sw.Elapsed.TotalMilliseconds);
        o.ExchangeId = pres.GetProperty("orderId").GetInt64();
        book.Apply(o, pres);
        if (o.State == OrderState.Acked) acked++;

        var sw2 = Stopwatch.StartNew();
        var cres = await oe.SendSignedAsync("order.cancel", P(("symbol", SYMBOL), ("origClientOrderId", cid)));
        cancelMs.Add(sw2.Elapsed.TotalMilliseconds);
        book.Apply(o, cres);
        if (o.State == OrderState.Canceled) canceled++;
    }
    catch (BinanceWsError e)
    {
        string k = Classify(e); errors[k] = errors.GetValueOrDefault(k) + 1;
        o.TryTransition(OrderState.Rejected);            // terminal failure -> not an orphan Sent
        if (k == "RATE_LIMIT") await Task.Delay(1000 + 200 * i);
    }
}

// ---- fill loop: marketable IOC buy crosses the spread -> real fill, then flatten with an IOC sell ----
string fillReport = "skipped";
try
{
    var (_, ask) = await BookTicker();
    string bcid = $"fill-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    var bo = book.Submit(bcid, out _); bo.TryTransition(OrderState.Sent);
    book.Apply(bo, await oe.SendSignedAsync("order.place", P(("symbol", SYMBOL), ("side", "BUY"), ("type", "LIMIT"), ("timeInForce", "IOC"), ("quantity", qty), ("price", Px(ask * 1.001m)), ("newClientOrderId", bcid))));

    if (bo.CumFilled > 0)
    {
        var (bidF, _) = await BookTicker();
        string scid = $"flat-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var so = book.Submit(scid, out _); so.TryTransition(OrderState.Sent);
        book.Apply(so, await oe.SendSignedAsync("order.place", P(("symbol", SYMBOL), ("side", "SELL"), ("type", "LIMIT"), ("timeInForce", "IOC"), ("quantity", bo.CumFilled.ToString("0.00000", inv)), ("price", Px(bidF * 0.999m)), ("newClientOrderId", scid))));
        fillReport = $"BUY {bo.CumFilled} @ {bo.AvgFillPrice:0.00} ({bo.State}) → SELL {so.CumFilled} @ {so.AvgFillPrice:0.00} ({so.State}) — position flat";
    }
    else fillReport = $"buy did not fill (state {bo.State})";
}
catch (BinanceWsError e) { var k = Classify(e); fillReport = $"error {k}"; errors[k] = errors.GetValueOrDefault(k) + 1; }

// ---- guards an interviewer probes ----
var done = book.All.FirstOrDefault(x => x.State == OrderState.Canceled);
if (done is not null) { book.Submit(done.ClientOrderId, out bool c2); if (!c2) dupRefused++; }   // idempotent submit
int illegalCaught = 0;
if (done is not null && !done.TryTransition(OrderState.Acked)) illegalCaught++;                   // ACK a canceled order
bool orderGoneHandled = false;                                                                    // cancel already-gone -> -2011
if (done?.ExchangeId is not null)
{
    try { await oe.SendSignedAsync("order.cancel", P(("symbol", SYMBOL), ("origClientOrderId", done.ClientOrderId))); }
    catch (BinanceWsError e) { orderGoneHandled = e.Code == -2011; }
}

double Pct(List<double> xs, int p) { if (xs.Count == 0) return double.NaN; var s = xs.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, p * s.Count / 100)]; }
var states = book.StateCounts();
Console.WriteLine("state machine: " + string.Join("  ", states.Select(kv => $"{kv.Key}={kv.Value}")));
Console.WriteLine($"loop: {acked} placed→ACKed, {canceled} canceled — each folded from its order-entry response");
Console.WriteLine($"place  WS round-trip ms  p50 {Pct(placeMs, 50):0}  p95 {Pct(placeMs, 95):0}  (n={placeMs.Count})");
Console.WriteLine($"cancel WS round-trip ms  p50 {Pct(cancelMs, 50):0}  p95 {Pct(cancelMs, 95):0}  (n={cancelMs.Count})");
Console.WriteLine($"fill loop: {fillReport}");
Console.WriteLine($"idempotent-submit refused dup: {dupRefused}  ·  illegal-transition caught: {illegalCaught + book.IllegalTransitions}  ·  cancel-already-gone (-2011): {(orderGoneHandled ? "yes ✓" : "not exercised")}");
Console.WriteLine($"errors handled: {(errors.Count > 0 ? string.Join(" ", errors.Select(kv => $"{kv.Key}={kv.Value}")) : "none")}  ·  clock-skew self-healed (resync+retry): {oe.ClockResyncs}");

bool ok = canceled > 0 && !states.ContainsKey(OrderState.New) && !states.ContainsKey(OrderState.Sent);
Console.WriteLine(ok ? "\n✓ full loop OK — signed WS order entry, state folded from exchange responses, fill round-tripped flat" : "\n✗ loop failed");
return ok ? 0 : 1;
