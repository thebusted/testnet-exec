namespace TestnetExec;

// Cross-venue spread mean-reversion, MAKER execution — the honest test of the taker version's
// verdict (bottom of CrossVenueTrader.cs): "the signal is real but fee-limited; lever #1 is perp
// maker-rebate tiers cutting the 4-fill cost ~10×". This class models that lever WITHOUT the
// classic maker-backtest lie ("I rest at the touch and always get filled at my price"). Three
// rules carry all the honesty:
//
//   1. TRADE-THROUGH FILLS ONLY. A resting SELL at P fills only when the best bid moves STRICTLY
//      above P; a resting BUY at P only when the best ask moves strictly below P. A touch never
//      fills — with no queue model, the honest assumption is back-of-queue, and at the back you
//      don't trade when price kisses your level and bounces. This forfeits every real-world touch
//      fill (conservative on fill COUNT) and bakes adverse selection into every fill we do take:
//      by construction we are filled only when the market has already moved through us.
//   2. POST-FILL ADVERSE MARK. Fills book at the rest price, but the position marks at the
//      CURRENT (post-through) touch — never at its own fill price. A fresh fill therefore shows
//      an adverse ex-fee mark, not a flattering zero.
//   3. LEG RISK IS REAL. Both entry legs rest simultaneously; if only one fills within
//      fillWindowTicks (or the signal dies first) the trade ABORTS: cancel the unfilled quote and
//      close the naked leg TAKER at the then-current adverse touch, realizing the loss.
//      ponytail: a real desk would often hedge-CHASE the missing leg instead of aborting; abort is
//      the conservative floor and slightly overstates leg-risk cost. Ceiling = a chase policy with
//      its own slippage model.
//
// Fee assumption — deliberately the OPTIMISTIC end so a negative verdict is definitive:
//   makerFee = −0.00005 (a 0.5 bp REBATE per fill). USDT-perp maker fees run ~+0.02% at base tier
//   on both venues, tapering to ~0.00% and small rebates at the top VIP/Pro tiers (Bybit Pro tiers
//   quote maker rebates in the −0.005%..−0.01% range). If the edge dies at a −0.5 bp maker rebate
//   it dies at every realistic tier; if it lives, it lives ONLY at tiers with this rebate.
//   takerFee = +0.0003 (3 bp — mid-VIP perp taker) on every fallback/abort/stop fill.
//   FeesPaid is SIGNED: maker rebates push it negative and it is reported truthfully, not clamped.
// ponytail: perp fee schedule modeled on SPOT books — no basis, no funding P&L (funding alone can
// dominate a position held across a funding timestamp), and perp books are deeper than spot.
// Ceiling = running the same model on a real perp feed pair.
public sealed class CrossMakerTrader(
    int window = 600,          // rolling spread stats — 1s samples → 10 min
    int minSamples = 120,      // 2 min warmup (taker cross uses 5) so a short live demo run can act;
                               // σ of a 1s-sampled spread series is stable well before 120 samples
    double zEntry = 2.0,       // lower than the taker cross's 2.5 — the maker cost floor is ~30× smaller,
                               // so smaller dislocations are worth quoting (leg risk prices the rest)
    double zExit = 0.25,
    double qty = 0.01,         // BTC per leg — same size as every other trader here
    double makerFee = -0.00005,// 0.5 bp REBATE per passive fill (top-tier perp assumption, see header)
    double takerFee = 0.0003,  // 3 bp per aggressive fill (aborts, stops, exit fallbacks)
    double hurdleMult = 1.5,
    double vetoTH = 0.3,       // same imbalance-agreement veto as the taker cross
    double stopMult = 2.5,
    int maxHoldTicks = 1800,   // 30 min time stop
    int staleTicks = 10,       // frozen top-of-book → feed presumed dead, block entries
    int fillWindowTicks = 10,  // quotes rest 10s; then unfilled → cancel, one-legged → abort
    bool verbose = false)
{
    private enum St { Flat, Quoting, Holding, Exiting }
    private struct Leg { public bool Active, IsBuy, Filled; public double Px; }

    public string Venue => "cross-maker";
    public string State => _st switch
    {
        St.Flat => "Flat",
        St.Quoting => _dir == Pos.Short ? "QuoteShort" : "QuoteLong",
        St.Holding => _dir.ToString(),
        _ => _dir == Pos.Short ? "ExitShort" : "ExitLong",
    };
    public double EntrySpread { get; private set; }   // rest/fill entry spread ($) — legs fill AT their rest px
    public double RealizedPnl { get; private set; }   // USDT, net of signed fees
    public double FeesPaid { get; private set; }      // SIGNED — rebates make this negative, shown truthfully
    public double Unrealized { get; private set; }
    public int Trades { get; private set; }
    public int Wins { get; private set; }
    public double LastSpread { get; private set; }    // binanceMid − bybitMid ($)
    public double LastZ { get; private set; }
    public double WinRate => Trades > 0 ? (double)Wins / Trades : 0;
    public double EquityPnl => RealizedPnl + Unrealized;

    // Execution-quality instrumentation — these counters ARE the experiment's deliverable:
    // fill rate = PairFills/Entries · one-leg rate = OneLegAborts/Entries · taker leakage = TakerFills.
    public int Entries { get; private set; }            // entry quote pairs placed
    public int PairFills { get; private set; }          // both entry legs filled → real spread position
    public int OneLegAborts { get; private set; }       // one leg filled, other missed → naked taker close
    public int ExpiredQuotes { get; private set; }      // neither leg filled → free cancel
    public int TakerFallbackExits { get; private set; } // exits forced to cross the spread (incl. stops)
    public int MakerFills { get; private set; }
    public int TakerFills { get; private set; }

    private St _st = St.Flat;
    private Pos _dir;
    private Leg _bn, _by;
    private int _quoteAge, _held;
    private double _entryDiv;
    private double _cash, _bnBtc, _byBtc;   // per-trade cash (signed fees folded in) + BTC inventory per venue

    private readonly double[] _win = new double[window];
    private int _count, _head;
    private double _sum, _sumSq;
    private long _pushes;
    private (int b, int a) _bnPrev, _byPrev;
    private int _bnSame, _bySame;

    public void OnTick(int? bnBidT, int? bnAskT, int? byBidT, int? byAskT, double bnImb, double byImb)
    {
        if (bnBidT is not int bbT || bnAskT is not int baT || byBidT is not int ybT || byAskT is not int yaT) return;
        double bnBid = bbT / 100.0, bnAsk = baT / 100.0, byBid = ybT / 100.0, byAsk = yaT / 100.0;
        double bnMid = (bnBid + bnAsk) / 2, byMid = (byBid + byAsk) / 2;
        double s = bnMid - byMid;
        LastSpread = s;

        // staleness guard — same counter proxy as the taker cross (see its ponytail note there).
        _bnSame = (bbT, baT) == _bnPrev ? _bnSame + 1 : 0; _bnPrev = (bbT, baT);
        _bySame = (ybT, yaT) == _byPrev ? _bySame + 1 : 0; _byPrev = (ybT, yaT);
        bool stale = _bnSame >= staleTicks || _bySame >= staleTicks;

        // rolling stats from PRIOR ticks only — decide first, push the current sample after.
        double mean = 0, sd = 0;
        if (_count > 0) { mean = _sum / _count; double v = _sumSq / _count - mean * mean; sd = v > 0 ? Math.Sqrt(v) : 0; }
        LastZ = sd > 1e-9 ? (s - mean) / sd : 0;
        double divergence = s - mean;

        if (_st is St.Holding or St.Exiting) _held++;

        // Fill detection FIRST, against the book that just arrived: the new best bid strictly above a
        // resting sell (or ask strictly below a resting buy) means the market traded through the level
        // since the last sample — fill at the rest price, then rule 2 marks it at this adverse book.
        // ponytail: 1s top-of-book sampling sees no trade prints — a through-and-back move inside one
        // second is missed entirely, so this under-counts fills relative to reality (both directions
        // conservative: fewer entries, and more exit quotes falling back to taker). Ceiling = trade
        // prints (aggTrade / publicTrade streams) matched against the resting level.
        if (_st is St.Quoting or St.Exiting)
        {
            _quoteAge++;
            TryFill(ref _bn, isBn: true, bnBid, bnAsk);
            TryFill(ref _by, isBn: false, byBid, byAsk);
        }

        switch (_st)
        {
            case St.Flat:
            {
                if (_count < minSamples || stale) break;
                if (Math.Abs(LastZ) < zEntry) break;
                // Maker round-trip cost floor per 1 BTC: 4 maker fills — CLAMPED at 0 (a rebate is
                // earned in the fills, never banked in the gate) — plus both venues' spreads as a
                // safety margin for the taker fallback paths this model actually takes when fills
                // miss. At a rebate tier this is just the two spreads (~$1–3 live) vs the taker
                // cross's ~$75 — THE reason this variant trades where the taker sits flat.
                double costPerBtc = Math.Max(0, 2 * (bnMid + byMid) * makerFee) + (bnAsk - bnBid) + (byAsk - byBid);
                if (Math.Abs(divergence) < hurdleMult * costPerBtc) break;
                if (Math.Sign(bnImb) == Math.Sign(byImb) && Math.Min(Math.Abs(bnImb), Math.Abs(byImb)) >= vetoTH) break;

                _dir = divergence > 0 ? Pos.Short : Pos.Long;
                // Join the passive touch on each leg: short spread = rest SELL bn at its ask + rest
                // BUY by at its bid (long = the mirror). Note what through-fill then implies: BOTH
                // entry legs fill only if the dislocation first WIDENS through us — we are filled
                // exactly when the signal moved against us. That is adverse selection, modeled.
                // ponytail: quotes are PINNED at placement — no re-quoting, no pegging, no improving
                // inside the spread. Pinned stale quotes get picked off harder than a live re-quoting
                // MM, so fills here skew worse than a real passive desk's. Ceiling = re-quote logic
                // with queue-position tracking (see HybridBook queue demo in MODE=book).
                if (_dir == Pos.Short) { _bn = new Leg { Active = true, IsBuy = false, Px = bnAsk }; _by = new Leg { Active = true, IsBuy = true, Px = byBid }; }
                else { _bn = new Leg { Active = true, IsBuy = true, Px = bnBid }; _by = new Leg { Active = true, IsBuy = false, Px = byAsk }; }
                EntrySpread = _bn.Px - _by.Px;
                _entryDiv = Math.Abs(divergence); _quoteAge = 0; Entries++;
                _st = St.Quoting;
                Log($"quote {(_dir == Pos.Short ? "SHORT" : "LONG")} spread · bn {(_bn.IsBuy ? "BUY" : "SELL")}@{_bn.Px:0.00} + by {(_by.IsBuy ? "BUY" : "SELL")}@{_by.Px:0.00} · S={s:0.00} z={LastZ:0.00} div={divergence:0.00}");
                break;
            }
            case St.Quoting:
            {
                bool bnF = _bn.Filled, byF = _by.Filled;
                if (bnF && byF)
                {
                    PairFills++; _held = 0; _st = St.Holding;
                    Log($"pair FILLED — {_dir} spread open @ {EntrySpread:0.00}");
                    break;
                }
                bool signalGone = _dir == Pos.Short ? LastZ <= zExit : LastZ >= -zExit;
                if (_quoteAge >= fillWindowTicks || signalGone)
                {
                    if (bnF ^ byF)
                    {
                        // LEG RISK realized: one leg is naked. Cancel the unfilled quote and flatten
                        // the filled leg TAKER at the current touch — which by construction has moved
                        // through (and usually beyond) our fill. This is the price of legging.
                        CloseInventoryTaker(bnBid, bnAsk, byBid, byAsk);
                        OneLegAborts++;
                        Realize($"one-leg ABORT ({(signalGone ? "signal gone" : "window expired")})");
                    }
                    else
                    {
                        ExpiredQuotes++;
                        Log($"quotes canceled unfilled ({(signalGone ? "signal gone" : "window expired")}) — no cost");
                        ResetTrade();
                    }
                    _st = St.Flat;
                }
                break;
            }
            case St.Holding:
            {
                bool reverted = _dir == Pos.Short ? LastZ <= zExit : LastZ >= -zExit;
                bool stopped = (_dir == Pos.Short ? divergence : -divergence) >= stopMult * _entryDiv;
                if (stopped || _held >= maxHoldTicks)
                {
                    // Stops cross the spread NOW — resting a stop and hoping is how convergence books
                    // die. Taker fee + full spread paid, honestly.
                    CloseInventoryTaker(bnBid, bnAsk, byBid, byAsk);
                    TakerFallbackExits++;
                    Realize(stopped ? "STOP — taker close" : "time stop — taker close");
                    _st = St.Flat;
                }
                else if (reverted)
                {
                    // Passive exit: rest the closing legs at the touch, same through-fill law.
                    if (_dir == Pos.Short) { _bn = new Leg { Active = true, IsBuy = true, Px = bnBid }; _by = new Leg { Active = true, IsBuy = false, Px = byAsk }; }
                    else { _bn = new Leg { Active = true, IsBuy = false, Px = bnAsk }; _by = new Leg { Active = true, IsBuy = true, Px = byBid }; }
                    _quoteAge = 0; _st = St.Exiting;
                    Log($"exit quote · bn {(_bn.IsBuy ? "BUY" : "SELL")}@{_bn.Px:0.00} + by {(_by.IsBuy ? "BUY" : "SELL")}@{_by.Px:0.00} · z={LastZ:0.00}");
                }
                break;
            }
            case St.Exiting:
            {
                if (_bn.Filled && _by.Filled) { Realize("passive exit — all 4 fills maker"); _st = St.Flat; break; }
                bool stopped = (_dir == Pos.Short ? divergence : -divergence) >= stopMult * _entryDiv;
                if (_quoteAge >= fillWindowTicks || stopped || _held >= maxHoldTicks)
                {
                    // Whatever hasn't filled passively gets closed at the taker touch. No exceptions —
                    // an exit that "waits a bit longer" is a stop that never fires.
                    CloseInventoryTaker(bnBid, bnAsk, byBid, byAsk);
                    TakerFallbackExits++;
                    Realize(stopped ? "stop during exit — taker close" : "exit window expired — taker close");
                    _st = St.Flat;
                }
                break;
            }
        }

        // Mark rule 2: banked trade cash (signed fees included) + inventory at the CURRENT executable
        // touch — a fresh through-fill marks against the post-move book, never at its own fill price.
        // ponytail: the liquidation mark skips the taker fee it would cost to actually flatten (~$0.18
        // at this size) — same convention as the other traders so uPnLs compare apples-to-apples.
        Unrealized = _st == St.Flat ? 0
            : _cash + _bnBtc * (_bnBtc >= 0 ? bnBid : bnAsk) + _byBtc * (_byBtc >= 0 ? byBid : byAsk);

        Push(s);
    }

    private void TryFill(ref Leg leg, bool isBn, double bid, double ask)
    {
        if (!leg.Active || leg.Filled) return;
        if (leg.IsBuy ? ask >= leg.Px : bid <= leg.Px) return;   // touch (==) is NOT a fill — rule 1
        leg.Filled = true;
        ApplyFill(isBn, leg.IsBuy, leg.Px, makerFee); MakerFills++;
        Log($"{(isBn ? "bn" : "by")} {(leg.IsBuy ? "BUY" : "SELL")} filled MAKER @{leg.Px:0.00} (book now {bid:0.00}/{ask:0.00} — traded through)");
    }

    private void ApplyFill(bool isBn, bool isBuy, double px, double feeRate)
    {
        double fee = qty * px * feeRate;                 // negative when feeRate is a rebate
        _cash += (isBuy ? -qty * px : qty * px) - fee;
        FeesPaid += fee;
        if (isBn) _bnBtc += isBuy ? qty : -qty; else _byBtc += isBuy ? qty : -qty;
    }

    private void CloseInventoryTaker(double bnBid, double bnAsk, double byBid, double byAsk)
    {
        if (Math.Abs(_bnBtc) > 1e-12)
        {
            bool buy = _bnBtc < 0; double px = buy ? bnAsk : bnBid;
            ApplyFill(true, buy, px, takerFee); TakerFills++;
            Log($"bn {(buy ? "BUY" : "SELL")} TAKER @{px:0.00} (flatten)");
        }
        if (Math.Abs(_byBtc) > 1e-12)
        {
            bool buy = _byBtc < 0; double px = buy ? byAsk : byBid;
            ApplyFill(false, buy, px, takerFee); TakerFills++;
            Log($"by {(buy ? "BUY" : "SELL")} TAKER @{px:0.00} (flatten)");
        }
    }

    private void Realize(string why)
    {
        double net = _cash;   // inventory is flat here; cash (fees folded in) IS the trade P&L
        RealizedPnl += net; Trades++; if (net > 0) Wins++;
        Log($"{why} → net {net:+0.0000;-0.0000} · realized {RealizedPnl:0.0000} · fees {FeesPaid:0.0000}");
        ResetTrade();
    }

    private void ResetTrade()
    {
        _cash = 0; _bnBtc = 0; _byBtc = 0; _bn = default; _by = default;
        EntrySpread = 0; _entryDiv = 0; _held = 0; _quoteAge = 0;
    }

    private void Push(double s)
    {
        if (_count == _win.Length) { double old = _win[_head]; _sum -= old; _sumSq -= old * old; }
        else _count++;
        _win[_head] = s; _sum += s; _sumSq += s * s;
        _head = (_head + 1) % _win.Length;
        if (++_pushes % 86_400 == 0)   // daily fp-error rebase, same as the taker cross
        {
            _sum = 0; _sumSq = 0;
            for (int i = 0; i < _count; i++) { _sum += _win[i]; _sumSq += _win[i] * _win[i]; }
        }
    }

    private void Log(string m) { if (verbose) Console.WriteLine($"[cross-maker {DateTime.UtcNow:HH:mm:ss}] {m}"); }

    // MODE=makercheck — proves the maker fill/P&L model against hand-computed numbers, fully offline.
    // Every scenario below was computed by hand BEFORE running (values in the messages); the code
    // must reproduce them exactly. Covers: cost gate, touch≠fill, clean 4-maker-fill round trip
    // earning the rebate, one-leg abort going naked, free expiry, taker-fallback exit, veto, staleness.
    public static int MakerCheck()
    {
        static void Check(bool ok, string what)
        { if (!ok) throw new Exception("MAKERCHECK FAIL: " + what); Console.WriteLine($"  ✓ {what}"); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;   // ~$600 notionals in doubles
        static (int bid, int ask) Q(double mid) => ((int)Math.Round((mid - 0.5) * 100), (int)Math.Round((mid + 0.5) * 100));
        static CrossMakerTrader Fresh(int staleTicks = 1_000) =>
            new(window: 8, minSamples: 8, zEntry: 2.0, zExit: 0.25, qty: 0.01, makerFee: -0.00005,
                takerFee: 0.0003, hurdleMult: 1.5, vetoTH: 0.3, stopMult: 100, maxHoldTicks: 1_000_000,
                staleTicks: staleTicks, fillWindowTicks: 3);
        static void Tick(CrossMakerTrader t, double bnMid, double byMid, double bnImb = 0, double byImb = 0)
        { var bn = Q(bnMid); var by = Q(byMid); t.OnTick(bn.bid, bn.ask, by.bid, by.ask, bnImb, byImb); }
        // 8 warmup ticks: spread alternates +1/−1 → mean 0, population σ exactly 1
        static void Warm(CrossMakerTrader t) { for (int k = 1; k <= 8; k++) Tick(t, 60000 + (k % 2 == 1 ? 1 : -1), 60000); }

        Console.WriteLine("cross-maker self-check — through-fill maker model vs hand-computed P&L\n");
        // fixture books: each venue quoted mid ± $0.50 → maker hurdle = 1.5 × ($1 + $1 spreads) = $3
        // (the rebate term clamps to 0 in the gate); fill window 3 ticks.

        var t1 = Fresh(); Warm(t1);
        Tick(t1, 60002.5, 60000);   // z = 2.5 passes the σ gate, but $2.50 < $3 hurdle
        Check(t1.State == "Flat" && t1.Entries == 0, "cost gate: 2.5σ dislocation REFUSED — $2.50 < $3 maker hurdle (vs $75 taker hurdle — the lever this class tests)");

        var t2 = Fresh(); Warm(t2);
        Tick(t2, 60100, 60000);
        Check(t2.State == "QuoteShort" && t2.Entries == 1 && t2.Trades == 0, "entry: $100 dislocation → rest SELL bn@60100.50 + BUY by@59999.50 (join touch, nothing filled)");
        Tick(t2, 60101, 60000);     // bn bid reaches exactly 60100.50
        Check(t2.MakerFills == 0 && t2.State == "QuoteShort", "touch is NOT a fill: bn bid == 60100.50 exactly → still resting (back-of-queue assumption)");
        Tick(t2, 60102, 59998);     // bn bid 60101.50 > 60100.50 AND by ask 59998.50 < 59999.50
        Check(t2.State == "Short" && t2.PairFills == 1 && t2.MakerFills == 2, "trade-through fills BOTH legs — note the dislocation had to WIDEN through us first (adverse selection, modeled)");
        Check(Near(t2.FeesPaid, -0.06005), $"2 maker fills → fees = −$0.06005, a REBATE, negative (got {t2.FeesPaid:0.00000000})");
        Check(Near(t2.Unrealized, 0.02005), $"post-fill mark at the post-through book: −$0.04 ex-fee adverse mark + $0.06005 banked rebate = +$0.02005 (got {t2.Unrealized:0.00000000})");
        Tick(t2, 60000, 60000);     // spread reverts → passive exit quotes
        Check(t2.State == "ExitShort", "reversion → passive exit: rest BUY bn@59999.50 + SELL by@60000.50");
        Tick(t2, 59998, 60002);     // bn ask 59998.50 < 59999.50 AND by bid 60001.50 > 60000.50
        Check(t2.State == "Flat" && t2.Trades == 1 && t2.Wins == 1, "exit trade-through fills both legs → clean 4-maker-fill round trip");
        Check(Near(t2.FeesPaid, -0.12005), $"4 maker fills, all rebates: fees = −$0.12005 (got {t2.FeesPaid:0.00000000})");
        Check(Near(t2.RealizedPnl, 1.14005), $"realized = $1.02 gross + $0.12005 rebate = $1.14005 (got {t2.RealizedPnl:0.00000000})");

        var t3 = Fresh(); Warm(t3);
        Tick(t3, 60100, 60000);
        Tick(t3, 60102, 60000);     // bn trades through; bybit never moves
        Check(t3.State == "QuoteShort" && t3.MakerFills == 1, "leg risk setup: bn leg fills, by leg never trades through — naked short 0.01 BTC on binance");
        Tick(t3, 60103, 60000);
        Tick(t3, 60104, 60000);     // window (3) expires → abort
        Check(t3.State == "Flat" && t3.OneLegAborts == 1 && t3.Trades == 1 && t3.Wins == 0, "one-leg ABORT: cancel by quote, buy back the naked bn short TAKER @60104.50 — the adverse move that filled us kept going");
        Check(Near(t3.RealizedPnl, -0.19026325), $"naked-leg loss: sold 60100.50 maker / bought 60104.50 taker = −$0.04 adverse + $0.15026325 net fees = −$0.19026325 (got {t3.RealizedPnl:0.00000000})");
        Check(Near(t3.FeesPaid, 0.15026325), $"fees = −$0.03005025 rebate + $0.18031350 taker = +$0.15026325 (got {t3.FeesPaid:0.00000000})");

        var t4 = Fresh(); Warm(t4);
        Tick(t4, 60100, 60000);
        Tick(t4, 60100, 60000); Tick(t4, 60100, 60000); Tick(t4, 60100, 60000);   // book never trades through
        Check(t4.State == "Flat" && t4.ExpiredQuotes == 1 && t4.Trades == 0 && Near(t4.FeesPaid, 0) && Near(t4.RealizedPnl, 0), "no fills at all: window expires → free cancel — no trade, no fees, no P&L");

        var t5 = Fresh(); Warm(t5);
        Tick(t5, 60100, 60000); Tick(t5, 60102, 59998);                            // pair fills (same as t2)
        Tick(t5, 60000, 60000);                                                    // reversion → exit quotes
        Tick(t5, 60000, 60000); Tick(t5, 60000, 60000); Tick(t5, 60000, 60000);    // exit never trades through
        Check(t5.State == "Flat" && t5.TakerFallbackExits == 1 && t5.Trades == 1 && t5.Wins == 1, "exit window expires unfilled → close BOTH legs TAKER (crossing both spreads)");
        Check(Near(t5.RealizedPnl, 0.70005), $"taker fallback drags the SAME trade from $1.14005 (t2) to $0.70005 (got {t5.RealizedPnl:0.00000000})");
        Check(Near(t5.FeesPaid, 0.29995), $"fees flip positive: −$0.06005 entry rebate + $0.36 exit taker = +$0.29995 (got {t5.FeesPaid:0.00000000})");

        var t6 = Fresh(); Warm(t6);
        Tick(t6, 60100, 60000, bnImb: 0.5, byImb: 0.5);
        Check(t6.Entries == 0, "imbalance-agreement veto: both books bid-heavy → dislocation NOT quoted (lead/lag momentum, not noise)");
        Tick(t6, 60100, 60000);
        Check(t6.Entries == 1, "veto releases: quiet dislocation is quoted");

        var t7 = Fresh(staleTicks: 3); Warm(t7);
        Tick(t7, 60100, 60000);
        Check(t7.Entries == 0, "staleness guard: frozen bybit book blocks the phantom-divergence quote");

        Console.WriteLine("\n✓ maker check OK — through-fills only, touch≠fill, post-fill adverse marks, leg risk realized, fees signed");
        return 0;
    }
}

// ── HONEST VERDICT (unlike the taker file's, this one is EMPIRICAL — written AFTER a 9.4-min live
//    run on 2026-07-03, 566 ticks, BTCUSDT ≈ $61.4k, live spread S oscillating ≈ −$5..+$9) ────────
//
//   entries=3 · pairFills=0 · oneLegAborts=3 (100%) · expiredQuotes=0 · realized −$0.85 (0.01 BTC/leg)
//   vs taker `cross` baseline: 0 trades, $0.00 — the flat baseline WON.
//
// All three entries died identically: short-the-spread quote (binance rich) → the BYBIT buy leg
// filled within 1s (bybit falling through our bid — the very move that created the dislocation)
// → the BINANCE sell leg NEVER filled (binance never rose through our offer) → 10s window expired
// → naked bybit long sold taker into a still-falling book (−$0.20, −$0.44, −$0.21). The rebate
// earned per maker fill (~$0.03 at this size) was 7-15× smaller than each naked-leg loss.
//
// The failure is STRUCTURAL, not a parameter choice: the two mids are cointegrated — they move
// TOGETHER, and a $5-9 dislocation is lead/lag (one venue moves first, the other follows the SAME
// way). But a passive two-legged entry needs the venues to move through OPPOSITE sides (bn up
// through our ask AND by down through our bid) inside one window — i.e. the dislocation must widen
// beyond both spreads combined, which cointegration makes a rare tail. So the through-fill model
// systematically fills ONLY the toxic leg: the leading venue picks off our quote, the lagging venue
// walks away from it. Waiting longer makes it worse (the laggard converges toward the leader, away
// from our quote); hedge-chasing the missing leg at these dislocation sizes ($5-9) doesn't clear
// the taker fee it costs. Sample is small (n=3, one session, all bybit-led) but the mechanism binds
// regardless of sample: pair completion is the precondition for the rebate math, and cointegration
// is precisely the force that denies it.
//
// ANSWER to "is the edge capturable with realistic maker execution?": NO at this design point —
// symmetric passive quoting at 1s granularity converts maker rebates into adverse-selection losses;
// the taker variant's discipline (sit flat) remains the best P&L of the family. What the green
// would actually require, in order of leverage:
//   1. asymmetric execution — quote passively ONLY on the lagging venue and hedge the leading venue
//      taker at fill instant (classic cross-venue MM), which needs sub-second event-driven feeds
//      and a real queue model, not 1s polls;
//   2. dislocations ≳ both spreads + taker fee (~$20+ at these prices) so hedge-chasing clears cost
//      — rare outside vol events (the taker trader's $75 hurdle was the same lesson, softened 4×);
//   3. genuine top-tier rebates AND pair-completion — the makercheck t2 scenario shows a completed
//      pair on a $100 dislocation nets +$1.14, so the arithmetic works IF both legs fill; live,
//      pair fill rate was 0/3. The rebate is real; the fills are the fiction.
// ponytail: verdict rests on 1s-sampled tops-of-book and a 10s fill window — an event-driven rerun
// with trade prints could move the one-leg rate some, but not the direction of the cointegration
// argument. Ceiling = weeks-long run + per-event dislocation taxonomy (lead/lag vs true two-sided).
