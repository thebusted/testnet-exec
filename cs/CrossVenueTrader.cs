namespace TestnetExec;

// Cross-venue spread mean-reversion — the "read BOTH books" strategy.
//
// Signal: S = binanceMid − bybitMid for the same BTCUSDT. Same asset on two venues, so S is
// cointegrated by construction (arbitrageurs pin it); it oscillates around a slowly-moving level.
// Track a rolling mean/stdev of S; when S dislocates hard from its mean, take the convergence:
// binance rich (S ≫ μ) → sell binance / buy bybit ("Short" the spread), binance cheap → the reverse
// ("Long"). Exit when S reverts to the mean. Same convergence logic as prediction-market cross-book
// arbitrage (Polymarket/Betfair), applied to two spot books.
//
// WHY THE BASELINE LOSES AND THIS ONE REFUSES TO: the per-venue imbalance traders bleed (−0.45 and
// −4.26 live) because they trade often on a weak signal and pay fees every round trip. This strategy
// inverts that: it prices its FULL round-trip cost up front — 4 fee fills (entry+exit on both venues)
// plus crossing both venues' bid/ask spreads — and refuses any trade whose expected capture (full
// reversion to the mean) doesn't clear that hurdle with margin. Computed, not hoped: at the
// PaperTrader-matched fee of 2 bps/fill the fee leg alone is 2·(bnMid+byMid)·fee ≈ $49/BTC at $61.5k,
// so with hurdleMult=1.5 the spread must dislocate ≈ $75 ≈ 12 bps before an entry is allowed. The
// live Binance–Bybit spot spread runs ~1–2 bps ($5–12). Expect this trader to sit FLAT nearly all
// the time and fire only on genuine dislocations (vol bursts, single-venue liquidation cascades).
// Sitting at 0.00 already beats a baseline bleeding −4.26 — but see the honest verdict at the bottom
// of this file: the constraint is the FEE LINE, not the signal.
//
// Honest-marking rules (mirrors PaperTrader's conventions so the comparison is apples-to-apples):
//   · fills mark at the ADVERSE touch — short-spread entry sells binance at its BID and buys bybit
//     at its ASK; both venues' spreads are paid in full, entry and exit. No mid-price fills.
//   · fee = 0.0002/fill (the baseline's maker assumption), charged on all 4 fills' notional.
//   · the decision at tick t uses rolling stats of ticks < t only (sample pushed AFTER deciding) —
//     no look-ahead, and an entry spike can't contaminate its own trigger stats.
//   · open positions mark at what closing RIGHT NOW would fetch, so a fresh entry shows a small
//     NEGATIVE uPnL (both crossed spreads) instead of a flattering zero.
public sealed class CrossVenueTrader(
    int window = 600,          // rolling stats window — 1s samples → 10 min
    int minSamples = 300,      // no trading until stats have 5 min of history
    double zEntry = 2.5,       // dislocation must be a ≥2.5σ anomaly ...
    double zExit = 0.25,       // ... and is closed once S is back within 0.25σ of the mean
    double qty = 0.01,         // BTC per leg (same size as the single-venue baseline)
    double fee = 0.0002,       // per-fill fee on notional — matched to PaperTrader for honest comparison
    double hurdleMult = 1.5,   // expected capture must be ≥ 1.5× the full round-trip cost
    double vetoTH = 0.3,       // both books agreeing this hard on direction → don't fade the move
    double stopMult = 2.5,     // divergence grows to 2.5× entry divergence → structural break, cut it
    int maxHoldTicks = 1800,   // 30 min time stop — a "reversion" that old is a regime change
    int staleTicks = 10)       // top-of-book frozen this many ticks → feed presumed dead, block entries
{
    public string Venue => "cross";
    public Pos Position { get; private set; } = Pos.Flat;  // Long/Short THE SPREAD (Long = long bn / short by)
    public double EntrySpread { get; private set; }        // executable spread at entry ($) — not a price
    public double RealizedPnl { get; private set; }        // USDT, net of all 4 fee fills
    public double FeesPaid { get; private set; }
    public double Unrealized { get; private set; }         // marked at CURRENT executable exit prices
    public int Trades { get; private set; }
    public int Wins { get; private set; }
    public double LastSpread { get; private set; }         // binanceMid − bybitMid ($)
    public double LastZ { get; private set; }              // z-score of LastSpread vs the rolling window
    public double WinRate => Trades > 0 ? (double)Wins / Trades : 0;
    public double EquityPnl => RealizedPnl + Unrealized;

    private readonly double[] _win = new double[window];
    private int _count, _head;
    private double _sum, _sumSq;
    private long _pushes;
    private double _bnEntryPx, _byEntryPx, _entryDiv;
    private int _held;
    private (int b, int a) _bnPrev, _byPrev;
    private int _bnSame, _bySame;

    public void OnTick(int? bnBidT, int? bnAskT, int? byBidT, int? byAskT, double bnImb, double byImb)
    {
        if (bnBidT is not int bbT || bnAskT is not int baT || byBidT is not int ybT || byAskT is not int yaT) return;
        double bnBid = bbT / 100.0, bnAsk = baT / 100.0, byBid = ybT / 100.0, byAsk = yaT / 100.0;
        double bnMid = (bnBid + bnAsk) / 2, byMid = (byBid + byAsk) / 2;
        double s = bnMid - byMid;
        LastSpread = s;

        // Staleness guard: neither feed reconnects, so a silently dead WS freezes its book and the
        // other venue's real moves then masquerade as "divergence" — the worst fake-profit failure
        // mode of a cross-venue paper sim. Top-of-book frozen for staleTicks samples → no NEW entries.
        // ponytail: counter proxy only — a held position still marks against the frozen book until the
        // time stop; the real fix is a last-update timestamp on IFeed + reconnect logic in both feeds.
        _bnSame = (bbT, baT) == _bnPrev ? _bnSame + 1 : 0; _bnPrev = (bbT, baT);
        _bySame = (ybT, yaT) == _byPrev ? _bySame + 1 : 0; _byPrev = (ybT, yaT);
        bool stale = _bnSame >= staleTicks || _bySame >= staleTicks;

        // Rolling stats from PRIOR ticks only — decide first, push the current sample after.
        double mean = 0, sd = 0;
        if (_count > 0) { mean = _sum / _count; double v = _sumSq / _count - mean * mean; sd = v > 0 ? Math.Sqrt(v) : 0; }
        LastZ = sd > 1e-9 ? (s - mean) / sd : 0;   // σ≈0 (lockstep mids) → no signal, never div-by-zero
        double divergence = s - mean;

        // Full round-trip cost per 1 BTC, in spread-$: 4 fee fills on notional (entry+exit × 2 venues)
        // + both venues' bid/ask spreads (each is crossed once at entry and once at exit).
        double costPerBtc = 2 * (bnMid + byMid) * fee + (bnAsk - bnBid) + (byAsk - byBid);

        if (Position != Pos.Flat) _held++;
        switch (Position)
        {
            case Pos.Flat:
                if (_count < minSamples || stale) break;
                if (Math.Abs(LastZ) < zEntry) break;                        // not a statistical anomaly
                if (Math.Abs(divergence) < hurdleMult * costPerBtc) break;  // full reversion couldn't pay the costs
                // Cross-venue imbalance AGREEMENT veto: both books leaning hard the same way means the
                // dislocation is likely lead/lag momentum (one venue front-running a real move), not
                // noise — fading that is how convergence trades die. Only fade QUIET dislocations.
                // ponytail: near-touch ($50-radius) book imbalance as the direction proxy; upgrade path
                // is venue lead/lag estimation or a liquidation-print feed as the confirmation signal.
                if (Math.Sign(bnImb) == Math.Sign(byImb) && Math.Min(Math.Abs(bnImb), Math.Abs(byImb)) >= vetoTH) break;
                if (divergence > 0) Enter(Pos.Short, bnBid, byAsk, divergence);  // bn rich: sell bn bid / buy by ask
                else Enter(Pos.Long, bnAsk, byBid, divergence);                  // bn cheap: buy bn ask / sell by bid
                break;
            case Pos.Short:  // exit trade available NOW: buy back binance at its ASK, sell bybit at its BID
                if (LastZ <= zExit || divergence >= stopMult * _entryDiv || _held >= maxHoldTicks)
                    Exit(bnAsk, byBid);
                break;
            case Pos.Long:
                if (LastZ >= -zExit || -divergence >= stopMult * _entryDiv || _held >= maxHoldTicks)
                    Exit(bnBid, byAsk);
                break;
        }

        Unrealized = Position switch
        {
            Pos.Short => qty * (EntrySpread - (bnAsk - byBid)),
            Pos.Long => qty * ((bnBid - byAsk) - EntrySpread),
            _ => 0,
        };

        Push(s);
    }

    private void Enter(Pos p, double bnPx, double byPx, double divergence)
    {
        Position = p; _bnEntryPx = bnPx; _byEntryPx = byPx;
        EntrySpread = bnPx - byPx; _entryDiv = Math.Abs(divergence); _held = 0;
    }

    private void Exit(double bnPx, double byPx)
    {
        double exitSpread = bnPx - byPx;
        double gross = Position == Pos.Short ? qty * (EntrySpread - exitSpread) : qty * (exitSpread - EntrySpread);
        double fees = qty * (_bnEntryPx + _byEntryPx + bnPx + byPx) * fee;  // all 4 fills, both venues, both legs
        double net = gross - fees;
        RealizedPnl += net; FeesPaid += fees; Trades++;
        if (net > 0) Wins++;
        Position = Pos.Flat; EntrySpread = 0; _bnEntryPx = _byEntryPx = _entryDiv = 0;
    }

    private void Push(double s)
    {
        if (_count == _win.Length) { double old = _win[_head]; _sum -= old; _sumSq -= old * old; }
        else _count++;
        _win[_head] = s; _sum += s; _sumSq += s * s;
        _head = (_head + 1) % _win.Length;
        // running add/subtract sums accumulate fp error; rebase from scratch once a day (at 1s ticks)
        if (++_pushes % 86_400 == 0)
        {
            _sum = 0; _sumSq = 0;
            for (int i = 0; i < _count; i++) { _sum += _win[i]; _sumSq += _win[i] * _win[i]; }
        }
    }

    // MODE=selfcheck — proves the cross P&L arithmetic against hand-computed numbers, fully offline.
    // Framework-free on purpose: each check throws on failure, prints on success.
    public static int SelfCheck()
    {
        static void Check(bool ok, string what)
        { if (!ok) throw new Exception("SELFCHECK FAIL: " + what); Console.WriteLine($"  ✓ {what}"); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        // bid/ask ticks (price·100) for a venue quoted mid ± $0.50
        static (int bid, int ask) Q(double mid) => ((int)Math.Round((mid - 0.5) * 100), (int)Math.Round((mid + 0.5) * 100));
        // tiny window/warmup so scenarios are hand-computable; staleTicks huge by default because the
        // fixtures hold bybit intentionally static, which would otherwise trip the live staleness guard
        static CrossVenueTrader Fresh(int staleTicks = 1_000) =>
            new(window: 8, minSamples: 8, zEntry: 2.0, zExit: 0.25, qty: 0.01, fee: 0.0002,
                hurdleMult: 1.5, vetoTH: 0.3, stopMult: 100, maxHoldTicks: 1_000_000, staleTicks: staleTicks);
        static void Tick(CrossVenueTrader t, double bnMid, double byMid, double bnImb = 0, double byImb = 0)
        { var bn = Q(bnMid); var by = Q(byMid); t.OnTick(bn.bid, bn.ask, by.bid, by.ask, bnImb, byImb); }
        // 8 warmup ticks: spread alternates +1/−1 → mean 0, population σ exactly 1
        static void Warm(CrossVenueTrader t) { for (int k = 1; k <= 8; k++) Tick(t, 60000 + (k % 2 == 1 ? 1 : -1), 60000); }

        Console.WriteLine("cross-venue self-check — hand-computed spread cycle vs trader P&L\n");
        // cost hurdle at these prices: 1.5 × (2·(bnMid+byMid)·0.0002 + $1 + $1 crossed spreads) ≈ $75.06

        var t1 = Fresh(); Warm(t1);
        Tick(t1, 60010, 60000);  // z = 10 (passes the σ gate) but divergence $10 < $75 hurdle
        Check(t1.Position == Pos.Flat && t1.Trades == 0, "cost gate: 10σ dislocation REFUSED — $10 move can't pay ~$75 round-trip hurdle");

        var t2 = Fresh(); Warm(t2);
        Tick(t2, 60100, 60000);  // z = 100, divergence $100 > hurdle → enter short spread
        Check(t2.Position == Pos.Short, "entry: $100 dislocation → short binance / long bybit");
        Check(Near(t2.EntrySpread, 99.0), "entry marks at ADVERSE touch: bnBid − byAsk = 60099.5 − 60000.5 = $99");
        Check(Near(t2.Unrealized, -0.02), "fresh entry uPnL = −qty·(both crossed spreads) = −$0.02, not a flattering zero");
        Tick(t2, 60000, 60000);  // spread reverts → exit at bnAsk − byBid = 60000.5 − 59999.5 = $1
        // hand math: gross = 0.01·(99−1) = 0.98 ; fees = 0.01·(60099.5+60000.5+60000.5+59999.5)·0.0002 = 0.4802
        Check(t2.Position == Pos.Flat && t2.Trades == 1 && t2.Wins == 1, "exit on reversion → 1 trade, 1 win");
        Check(Near(t2.FeesPaid, 0.4802), $"fees = 4 fills on notional = $0.4802 (got {t2.FeesPaid:0.000000})");
        Check(Near(t2.RealizedPnl, 0.4998), $"realized = 0.98 gross − 0.4802 fees = $0.4998 (got {t2.RealizedPnl:0.000000})");

        var t3 = Fresh(); Warm(t3);
        Tick(t3, 60100, 60000, bnImb: +0.5, byImb: +0.5);  // same dislocation but both books scream BUY
        Check(t3.Position == Pos.Flat, "imbalance-agreement veto: both books bid-heavy → dislocation NOT faded");
        Tick(t3, 60100, 60000);                            // pressure gone → same dislocation now tradeable
        Check(t3.Position == Pos.Short, "veto releases: quiet dislocation is entered");

        var t4 = Fresh(staleTicks: 3); Warm(t4);
        Tick(t4, 60100, 60000);  // bybit top-of-book unchanged 8 ticks ≥ 3 → presumed dead feed
        Check(t4.Position == Pos.Flat, "staleness guard: frozen bybit book blocks the phantom-divergence entry");

        Console.WriteLine("\n✓ self-check OK — cross P&L equals hand math, net of 4 fee fills and both crossed spreads");
        return 0;
    }
}

// ── HONEST VERDICT (written from arithmetic BEFORE seeing live results — not tuned to fit) ─────────
// The signal is real (the two mids are cointegrated and the spread mean-reverts) but at 2 bps/fill
// the strategy's edge is fee-limited, not signal-limited: the entry hurdle (~$75 ≈ 12 bps) exceeds
// the typical live dislocation (~1–2 bps) by an order of magnitude, so on most days this trader does
// exactly the right thing — nothing. It "beats" the bleeding single-venue baseline by refusing
// negative-EV trades, and only fires on genuine dislocations, which are rare. To turn the signal
// into steady P&L the lever is the COST line, in order of impact:
//   1. cheaper venue/tier — perps at maker-rebate tiers cut the 4-fill cost ~10×, hurdle → ~$8;
//   2. passive execution — resting both entry legs earns the half-spreads instead of paying them
//      (needs a queue/fill model, which this paper sim honestly does not have);
//   3. event-driven ticks — 1s polling undersamples; most dislocations live and die between samples.
// ponytail: 1s sampled mids + assumed-instant paired fills (no legging risk — real cross-venue arb
// can get one leg filled and chase the other); upgrade = event-driven feed callbacks + a two-leg
// fill state machine with hedge-chase logic.
