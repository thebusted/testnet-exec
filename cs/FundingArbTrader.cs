using System.Globalization;
using System.Text.Json;

namespace TestnetExec;

// Cross-venue PERP funding-rate carry — the "make money from both venues" trade that actually has a
// mechanism behind it, unlike tick-level book arb (which we measured and rejected: spot spread ~1-2 bps
// vs a ~12 bps fee hurdle — see CrossVenueTrader's verdict).
//
// Mechanism: BTCUSDT perpetuals on Binance and Bybit each pay funding every 8h. Longs pay shorts when
// the rate is positive (and vice versa). The two venues' rates are set independently by their own
// premium indices, so they DIFFER. Hold LONG the perp on the lower/more-negative-funding venue and
// SHORT the perp on the higher-funding venue: price risk cancels (both legs are the same underlying),
// and every settlement you net  notional × (rate_shortVenue − rate_longVenue)  in cash. That is the
// whole edge — a delta-neutral carry, not a prediction.
//
// Entry economics, priced UP FRONT like the cross trader does: the round trip costs 4 taker fills
// (open+close × 2 venues). At Binance USDⓈ-M VIP0 taker 5.0 bps and Bybit linear taker 5.5 bps that is
// 2·(5.0+5.5) = 21 bps of notional, plus 4 crossings of the touch (slip). The differential pays
// |d| bps per 8h settlement, so break-even needs costFrac/|d| settlements. We only enter when the
// CURRENT differential would repay hurdleMult × cost within horizonSettles settlements (default: 1.3×
// cost within 21 settlements = 7 days), i.e. |d| ≥ 1.3·21.8/21 ≈ 1.35 bps/8h ≈ 14.8% annualized.
// The typical live BTC differential is ~0-1 bps/8h, so expect this trader to sit FLAT most of the
// time — that is the honest answer, printed rather than hidden (see verdict at the bottom).
//
// Honest-marking rules:
//   · funding is credited ONLY at each venue's own settlement boundary (nextFundingTime from the API),
//     never accrued per tick — you must HOLD THROUGH the boundary to be paid, which the sim enforces.
//   · the rate/mark used at a boundary is the LAST one observed BEFORE it (previous sample) — the new
//     interval's rate is never peeked at. No look-ahead.
//   · fills mark adversely at mark ± slip (long lifts, short hits), fees on all 4 fills' notional.
//   · funding already settled is REALIZED cash (that's how perps pay it); Unrealized holds only what
//     closing right now would net: basis drift at adverse prices minus the exit fees. A fresh entry
//     therefore shows NEGATIVE uPnL (the exit fees you now owe), not a flattering zero.
public enum FundingPos { Flat, LongBnShortBy, LongByShortBn }

// One observation of both venues' funding state. NowMs = observation time (unix ms);
// rates are per-8h-period fractions (e.g. 0.0001 = 1 bp); NextMs = that venue's next settlement.
public sealed record FundingSample(
    long NowMs,
    double BnMark, double BnRate, long BnNextMs,
    double ByMark, double ByRate, long ByNextMs);

public sealed class FundingArbTrader(
    double qty = 0.01,            // BTC per leg — same size as every other paper trader here
    double bnFee = 0.0005,        // Binance USDⓈ-M taker VIP0 (0.05%) — taker, stated
    double byFee = 0.00055,       // Bybit linear taker VIP0 (0.055%) — taker, stated
    double slip = 0.00002,        // 0.2 bp per fill: touch-crossing + mark-vs-executable gap allowance
    int horizonSettles = 21,      // carry must repay costs within 21 settlements (7 days at 3/day)
    double hurdleMult = 1.3,      // ... with 30% margin — differential persistence is NOT guaranteed
    double exitBps = 0.0,         // exit when the position's signed differential (bps/8h) ≤ this
    int confirmSamples = 4,       // entry needs N consecutive clearing samples (~2 min at 30s polls)
    long staleMs = 300_000)       // sample gap > 5 min → poller presumed dead, block new entries
{
    public string Venue => "funding";
    public FundingPos Position { get; private set; } = FundingPos.Flat;
    public double EntryDiffBps { get; private set; }       // differential (bps/8h) at entry
    public double LastDiffBps { get; private set; }        // byRate − bnRate, in bps per 8h period
    public double AnnualizedCarryPct { get; private set; } // gross carry %/yr: flat → |d|, held → signed for OUR side
    public double RealizedPnl { get; private set; }        // USDT: settled funding + closed basis − all fees
    public double Unrealized { get; private set; }         // what closing NOW nets: basis drift − exit fees
    public double FeesPaid { get; private set; }
    public int Trades { get; private set; }
    public int Wins { get; private set; }
    public int LegSettles { get; private set; }            // per-leg funding boundaries collected while held
    public double WinRate => Trades > 0 ? (double)Wins / Trades : 0;
    public double EquityPnl => RealizedPnl + Unrealized;
    // full round trip as a fraction of one leg's notional: 4 fee fills + 4 touch crossings
    public double CostFrac => 2 * (bnFee + byFee) + 4 * slip;
    public double EntryThresholdBps => hurdleMult * CostFrac / horizonSettles * 1e4;

    private FundingSample? _prev;
    private long _bnNext, _byNext;          // boundaries the OPEN position must hold through to be paid
    private double _bnEntryPx, _byEntryPx;
    private double _tradeNet;               // running net of the open trade — decides win/loss at close
    private int _confirm;

    public void OnSample(FundingSample s)
    {
        double d = s.ByRate - s.BnRate;     // d > 0: long binance / short bybit RECEIVES net funding
        LastDiffBps = d * 1e4;
        double signedD = Position switch
        {
            FundingPos.LongBnShortBy => d,
            FundingPos.LongByShortBn => -d,
            _ => Math.Abs(d),
        };
        // ponytail: annualization assumes 8h intervals forever (1095 settles/yr); Bybit can switch a
        // symbol to 4h/1h funding in extremes — we read nextFundingTime so accrual stays correct, but
        // this display number and the entry threshold don't re-derive the interval.
        AnnualizedCarryPct = signedD * 1095 * 100;

        bool stale = _prev is not null && s.NowMs - _prev.NowMs > staleMs;

        // ── funding settlements — cash at each venue's own boundary, nothing in between ──
        // Rate/mark come from the PREVIOUS sample (last observed before the boundary): Binance fixes the
        // realized rate AT the boundary, so the last pre-boundary estimate (≤30s old at our poll cadence)
        // is what a no-look-ahead observer actually knew. Using the crossing sample's values would leak
        // the NEXT interval's rate into this settlement.
        // ponytail: real funding = size × mark AT the boundary instant × fixed rate; ours is ≤1 poll
        // stale on both. Also a poller outage spanning k boundaries settles only once at the last-seen
        // rate — undercounts, never invents. Fix = pull each venue's fundingRate history REST endpoint.
        if (Position != FundingPos.Flat && _prev is not null)
        {
            if (s.NowMs >= _bnNext)
            {
                double pay = qty * _prev.BnMark * _prev.BnRate;                       // longs pay this (receive if negative)
                double credit = Position == FundingPos.LongBnShortBy ? -pay : +pay;   // we're long bn → pay; short bn → receive
                RealizedPnl += credit; _tradeNet += credit; LegSettles++;
                _bnNext = s.BnNextMs > s.NowMs ? s.BnNextMs : _bnNext + 28_800_000;
            }
            if (s.NowMs >= _byNext)
            {
                double pay = qty * _prev.ByMark * _prev.ByRate;
                double credit = Position == FundingPos.LongByShortBn ? -pay : +pay;
                RealizedPnl += credit; _tradeNet += credit; LegSettles++;
                _byNext = s.ByNextMs > s.NowMs ? s.ByNextMs : _byNext + 28_800_000;
            }
        }

        switch (Position)
        {
            case FundingPos.Flat:
                // Entry gate: the differential must repay hurdleMult × full round-trip cost within
                // horizonSettles settlements — same "price the costs first" discipline as the cross
                // trader — and must clear it confirmSamples polls in a row (one glitched poll or a
                // momentary estimate spike is not a carry regime).
                // ponytail: persistence over the horizon is ASSUMED, not forecast — funding estimates
                // mean-revert across venues; a real desk models the differential's half-life.
                bool clears = Math.Abs(LastDiffBps) >= EntryThresholdBps;
                _confirm = clears && !stale ? _confirm + 1 : 0;
                if (_confirm >= confirmSamples)
                    Enter(d > 0 ? FundingPos.LongBnShortBy : FundingPos.LongByShortBn, s);
                break;
            default:
                // Exit when forward carry stops being positive (differential compressed/flipped).
                // Exit fees are owed whenever we leave, so the marginal decision is only "is the NEXT
                // settlement still expected to pay?" — no sunk-cost anchoring on the entry threshold.
                // ponytail: single-sample exit; an intra-interval estimate wobble can shake us out
                // (entry has a confirm gate, exit deliberately doesn't — staying wrong-way costs cash).
                if (signedD * 1e4 <= exitBps) Exit(s);
                break;
        }

        if (Position != FundingPos.Flat)
        {
            var (bnPx, byPx) = ExitPrices(s);
            double basis = BasisPnl(bnPx, byPx);
            Unrealized = basis - qty * (bnPx * bnFee + byPx * byFee);
        }
        else Unrealized = 0;

        _prev = s;
    }

    private void Enter(FundingPos p, FundingSample s)
    {
        Position = p; _confirm = 0; _tradeNet = 0;
        bool longBn = p == FundingPos.LongBnShortBy;
        // 2 taker fills, marked adversely: the long leg lifts the offer (mark+slip), the short hits the
        // bid (mark−slip). ponytail: mark price stands in for the touch — these endpoints carry no perp
        // order book; slip is a stated allowance, not a measured half-spread. No depth/size check either.
        _bnEntryPx = s.BnMark * (longBn ? 1 + slip : 1 - slip);
        _byEntryPx = s.ByMark * (longBn ? 1 - slip : 1 + slip);
        double fees = qty * (_bnEntryPx * bnFee + _byEntryPx * byFee);
        RealizedPnl -= fees; FeesPaid += fees; _tradeNet -= fees;
        EntryDiffBps = LastDiffBps;
        // only boundaries we actually hold through pay funding — no credit for the interval we entered
        // mid-way (that's the real mechanic: whoever holds AT the boundary gets the full payment).
        // ponytail: no funding-time skew games — entering seconds before a boundary to snipe the payment
        // is real (and gets arbed via mark-price convergence around settlement, which we don't model).
        _bnNext = s.BnNextMs; _byNext = s.ByNextMs;
    }

    private void Exit(FundingSample s)
    {
        var (bnPx, byPx) = ExitPrices(s);
        double basis = BasisPnl(bnPx, byPx);   // the two marks drift apart while held — that's real P&L
        double fees = qty * (bnPx * bnFee + byPx * byFee);
        RealizedPnl += basis - fees; FeesPaid += fees; _tradeNet += basis - fees;
        Trades++;
        if (_tradeNet > 0) Wins++;             // whole trade: funding collected + basis − all 4 fills
        Position = FundingPos.Flat; EntryDiffBps = 0; Unrealized = 0;
        _bnEntryPx = _byEntryPx = 0;
    }

    // closing crosses the touch again: sell the long at mark−slip, buy back the short at mark+slip
    private (double bn, double by) ExitPrices(FundingSample s)
    {
        bool longBn = Position == FundingPos.LongBnShortBy;
        return (s.BnMark * (longBn ? 1 - slip : 1 + slip),
                s.ByMark * (longBn ? 1 + slip : 1 - slip));
    }

    private double BasisPnl(double bnPx, double byPx) => Position == FundingPos.LongBnShortBy
        ? qty * ((bnPx - _bnEntryPx) - (byPx - _byEntryPx))
        : qty * ((byPx - _byEntryPx) - (bnPx - _bnEntryPx));

    // MODE=fundingcheck — scripted funding sequences + settlement boundaries vs hand-computed cash.
    // Framework-free like SelfCheck/MakerCheck: throws on failure, prints on success.
    public static int FundingCheck()
    {
        static void Check(bool ok, string what)
        { if (!ok) throw new Exception("FUNDINGCHECK FAIL: " + what); Console.WriteLine($"  ✓ {what}"); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        const long H8 = 28_800_000;
        // slip=0, hurdle=1.0 → costFrac = 2·(0.0005+0.00055) = 0.0021 exactly, threshold = 0.0021/21
        // = 1.00 bp/8h exactly — every scenario below is hand-computable from these round numbers.
        static FundingArbTrader Fresh(double bnFee = 0.0005, double byFee = 0.00055, double slip = 0,
                                      double hurdleMult = 1.0, int confirm = 1, long staleMs = long.MaxValue / 4) =>
            new(qty: 0.01, bnFee: bnFee, byFee: byFee, slip: slip, horizonSettles: 21,
                hurdleMult: hurdleMult, exitBps: 0, confirmSamples: confirm, staleMs: staleMs);
        static FundingSample S(long now, double bnRate, double byRate, double bnMark = 60000, double byMark = 60000,
                               long bnNext = H8, long byNext = H8) =>
            new(now, bnMark, bnRate, bnNext, byMark, byRate, byNext);

        Console.WriteLine("funding-arb self-check — scripted settlements vs hand-computed cash\n");

        // 1 — cost gate: differential below the priced hurdle is refused, however "free" it looks.
        var t1 = Fresh();
        Check(Near(t1.EntryThresholdBps, 1.0), "threshold: 1.0×(2·(5.0+5.5)bps)/21 settles = 1.00 bp/8h exactly");
        t1.OnSample(S(0, 0.00001, 0.00010));   // d = 0.9 bp < 1.0 bp
        Check(t1.Position == FundingPos.Flat && t1.Trades == 0, "cost gate: 0.9 bp/8h differential REFUSED — can't repay 21 bps of fills in 21 settles");

        // 2 — full cycle at d = +1.5 bp/8h (by richer): long bn / short by, 2 settlements, exit on flip.
        // entry fees = 0.01·60000·(0.0005+0.00055) = $0.63
        // per settlement: short by receives 0.01·60000·0.0002 = +0.12; long bn pays 0.01·60000·0.00005 = −0.03 → net +0.09
        var t2 = Fresh();
        t2.OnSample(S(0, 0.00005, 0.00020));
        Check(t2.Position == FundingPos.LongBnShortBy, "direction: bybit funding richer → LONG binance (pays less) / SHORT bybit (receives more)");
        Check(Near(t2.RealizedPnl, -0.63) && Near(t2.FeesPaid, 0.63), "entry books the 2 taker fills immediately: realized = −$0.63");
        Check(Near(t2.Unrealized, -0.63), "fresh entry uPnL = −(exit fees owed) = −$0.63, not a flattering zero");
        t2.OnSample(S(H8, 0.00005, 0.00020, bnNext: 2 * H8, byNext: 2 * H8));       // 1st boundary held
        Check(Near(t2.RealizedPnl, -0.54) && t2.LegSettles == 2, "1st settlement: +0.12 (by short) − 0.03 (bn long) = +$0.09 cash → −$0.54");
        t2.OnSample(S(2 * H8, 0.00005, 0.00020, bnNext: 3 * H8, byNext: 3 * H8));   // 2nd boundary held
        Check(Near(t2.RealizedPnl, -0.45), "2nd settlement: another +$0.09 → −$0.45 — funding pays at boundaries only, never per tick");
        t2.OnSample(S(2 * H8 + 60_000, 0.00020, 0.00005, bnNext: 3 * H8, byNext: 3 * H8));  // differential flips
        Check(t2.Position == FundingPos.Flat && t2.Trades == 1 && t2.Wins == 0, "flip → exit; trade complete");
        Check(Near(t2.RealizedPnl, -1.08) && Near(t2.FeesPaid, 1.26),
            "HONEST: 1.5 bp/8h held 2 settles = $0.18 carry vs $1.26 fees → net −$1.08 (a loss, reported as one)");

        // 3 — no look-ahead: the sample that CROSSES a boundary carries the NEW interval's rate; the
        // settlement must use the pre-boundary rate. Wrong (current-rate) math would give 0 here.
        var t3 = Fresh();
        t3.OnSample(S(0, 0.00005, 0.00020));
        t3.OnSample(S(H8, 0.00500, 0.00500, bnNext: 2 * H8, byNext: 2 * H8));  // d=0 → also exits after settling
        Check(Near(t3.RealizedPnl, -0.63 + 0.09 - 0.63), "settlement at a boundary uses the LAST pre-boundary rate (+$0.09), not the crossing sample's");

        // 4 — a differential big enough to win: d = 10 bp/8h (bn funding NEGATIVE −2bp, by +8bp).
        // per settlement: bn long receives 0.12, by short receives 0.48 → +0.60; 3 settles = +1.80 vs $1.26 fees.
        var t4 = Fresh();
        t4.OnSample(S(0, -0.00020, 0.00080));
        for (int k = 1; k <= 3; k++) t4.OnSample(S(k * H8, -0.00020, 0.00080, bnNext: (k + 1) * H8, byNext: (k + 1) * H8));
        t4.OnSample(S(3 * H8 + 60_000, 0.00080, -0.00020, bnNext: 4 * H8, byNext: 4 * H8));
        Check(t4.Trades == 1 && t4.Wins == 1 && t4.LegSettles == 6, "3 settlements collected on both legs, then exit");
        Check(Near(t4.RealizedPnl, 0.54), "profitable carry: −0.63 + 3·0.60 − 0.63 = +$0.54 — wins only when funding outruns fills");

        // 5 — venues settle at DIFFERENT times: each leg pays at its own boundary.
        var t5 = Fresh();
        t5.OnSample(S(0, 0.00005, 0.00020, bnNext: H8, byNext: H8 + 4 * 3_600_000));       // by settles 4h later
        t5.OnSample(S(H8 + 3_600_000, 0.00005, 0.00020, bnNext: 2 * H8, byNext: H8 + 4 * 3_600_000));
        Check(Near(t5.RealizedPnl, -0.66) && t5.LegSettles == 1, "bn boundary only: long bn PAYS 0.03 → −$0.66; bybit leg not yet due");
        t5.OnSample(S(H8 + 5 * 3_600_000, 0.00005, 0.00020, bnNext: 2 * H8, byNext: 2 * H8 + 4 * 3_600_000));
        Check(Near(t5.RealizedPnl, -0.54) && t5.LegSettles == 2, "bybit boundary 4h later: short by RECEIVES 0.12 → −$0.54");

        // 6 — basis drift is real P&L: marks diverge $30 against us while held (bn +50, by +80).
        // basis = 0.01·(50−80) = −$0.30; exit fees = 0.01·(60050·0.0005 + 60080·0.00055) = $0.63069
        var t6 = Fresh();
        t6.OnSample(S(0, 0.00005, 0.00020));
        t6.OnSample(S(60_000, 0.00020, 0.00005, bnMark: 60050, byMark: 60080));
        Check(Near(t6.RealizedPnl, -0.63 - 0.30 - 0.63069), "basis drift: −0.63 entry − 0.30 basis − 0.63069 exit = −$1.56069 — hedged ≠ riskless");

        // 7 — slip accounting isolated (fees zeroed): 4 crossings × 1 bp × $60000 × 0.01 BTC = $0.24
        var t7 = Fresh(bnFee: 0, byFee: 0, slip: 0.0001);
        t7.OnSample(S(0, 0.00005, 0.00020));
        t7.OnSample(S(60_000, 0.00020, 0.00005));
        Check(Near(t7.RealizedPnl, -0.24), "slip: 4 adverse touch-crossings cost exactly 4·slip·notional = −$0.24");

        // 8 — stale poller blocks NEW entries (a frozen feed must not manufacture a carry signal).
        var t8 = Fresh(staleMs: 60_000);
        t8.OnSample(S(0, 0.00001, 0.00010));                 // below threshold — no entry
        t8.OnSample(S(600_000, 0.00005, 0.00020));           // clears, but 10 min since last sample → stale
        Check(t8.Position == FundingPos.Flat, "staleness guard: clearing differential after a 10-min poll gap is NOT entered");
        t8.OnSample(S(630_000, 0.00005, 0.00020));           // fresh again (30s gap) → entry allowed
        Check(t8.Position == FundingPos.LongBnShortBy, "guard releases once polling resumes");

        // 9 — confirm gate: entry needs N consecutive clearing samples, not one lucky poll.
        var t9 = Fresh(confirm: 3);
        t9.OnSample(S(0, 0.00005, 0.00020));
        t9.OnSample(S(30_000, 0.00005, 0.00020));
        Check(t9.Position == FundingPos.Flat, "confirm gate: 2 of 3 required clearing samples → still flat");
        t9.OnSample(S(60_000, 0.00005, 0.00020));
        Check(t9.Position == FundingPos.LongBnShortBy, "3rd consecutive clearing sample → entry");

        Console.WriteLine("\n✓ funding-check OK — settlement cash, fees, basis, slip all equal hand math; no look-ahead, no per-tick accrual");
        return 0;
    }
}

// Public REST poller for both venues' perp funding state — no keys, no order endpoints. Funding moves
// on 8h cycles so 30s polling is generous; the paper loop's 1s cadence reads the latest snapshot.
// All trader mutation stays on the paper loop's thread (TryTake pattern) — the poller only swaps an
// immutable record under a lock, matching how the book feeds keep strategies single-threaded.
public sealed class FundingFeed(string symbol, int intervalMs = 30_000)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly object _sync = new();
    private FundingSample? _latest;
    private bool _fresh;
    public long Polls { get; private set; }
    public long Errors { get; private set; }

    public void Start() => _ = Task.Run(Loop);

    private async Task Loop()
    {
        while (true)
        {
            try
            {
                var bnT = Http.GetStringAsync($"https://fapi.binance.com/fapi/v1/premiumIndex?symbol={symbol}");
                var byT = Http.GetStringAsync($"https://api.bybit.com/v5/market/tickers?category=linear&symbol={symbol}");
                using var bn = JsonDocument.Parse(await bnT);
                using var by = JsonDocument.Parse(await byT);
                var b = bn.RootElement;
                var y = by.RootElement.GetProperty("result").GetProperty("list")[0];
                var s = new FundingSample(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    double.Parse(b.GetProperty("markPrice").GetString()!, Inv),
                    double.Parse(b.GetProperty("lastFundingRate").GetString()!, Inv),   // upcoming settlement's rate (live estimate)
                    b.GetProperty("nextFundingTime").GetInt64(),
                    double.Parse(y.GetProperty("markPrice").GetString()!, Inv),
                    double.Parse(y.GetProperty("fundingRate").GetString()!, Inv),
                    long.Parse(y.GetProperty("nextFundingTime").GetString()!, Inv));
                lock (_sync) { _latest = s; _fresh = true; Polls++; }
            }
            // a failed/garbled poll keeps the previous snapshot; the trader's staleMs guard turns a
            // DEAD poller into "no new entries" rather than trading on frozen funding data.
            catch { lock (_sync) Errors++; }
            await Task.Delay(intervalMs);
        }
    }

    // hands each snapshot to the caller exactly once, on the caller's thread
    public bool TryTake(out FundingSample s)
    {
        lock (_sync)
        {
            if (_fresh && _latest is not null) { s = _latest; _fresh = false; return true; }
        }
        s = null!;
        return false;
    }
}

// ── HONEST VERDICT (from the arithmetic, before any live run — not tuned to fit one) ───────────────
// The mechanism is real: funding differentials are actual cash flows, unlike the phantom edges in the
// tick-level book-arb experiments. But for a RETAIL taker on BTC the numbers rarely line up:
//   · typical Binance-vs-Bybit BTCUSDT differential: ~0-1 bp per 8h (both anchor near the same index
//     and the +1bp/8h default), i.e. ~0-11%/yr gross before costs;
//   · the 4-fill taker round trip costs ~21 bps — three WEEKS of a 1 bp/8h differential, during which
//     the differential must persist (it usually mean-reverts in hours) and basis drift can eat more;
//   · so the entry gate (≥1.35 bp/8h sustained) fires mainly in stress regimes — leverage flushes,
//     one-venue liquidation cascades, listing/delisting events — and BTC is the tightest pair there is.
// What would make it genuinely tradeable, in order of impact: (1) maker entries/exits at ~2 bps/fill
// cut the hurdle ~2.6×; (2) alt perps, where cross-venue funding differentials of 5-30 bp/8h are
// routine; (3) the spot-vs-perp cash-and-carry variant when funding itself (not the differential)
// spikes. ponytail: no margin/collateral model — both legs need posted margin, the short leg can be
// liquidation-squeezed in a spike before funding ever pays, and idle collateral has a carry cost of
// its own; none of that is charged here, so live P&L is a CEILING on the real trade.
