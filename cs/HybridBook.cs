using System;
using System.Collections.Generic;

namespace TestnetExec;

// HYBRID order book — flat circular hot-window (near touch: O(1), no-alloc)
// + SortedDictionary cold-tail (far: complete). Ported from the VERIFIED
// reference (practice/ob_roll/Program.cs, class Hybrid), checked byte-for-byte
// against a SortedDictionary reference over 1,000,000 synthetic order events.
//
// DEVIATION FROM REFERENCE (the only intentional one): the reference tracks
// individual orders (Add(id,side,tick,qty) / Cancel(id)) via an `orders`
// dictionary so a cancel can find its original (side,tick,qty) to subtract.
// A level-based feed (Binance-style snapshot/diff depth) has no order ids —
// each update REPLACES the resting quantity at a price level outright. So the
// `orders` dictionary is removed entirely and Add/Cancel collapse into a
// single SetLevel(isBid, tick, qty) method:
//   - qty <= 0  -> remove the level (same effect as a Cancel that fully drains it)
//   - qty  > 0  -> replace (not accumulate) the level's resting quantity
// The Shift/MaybeShift/FullRepop/Rescan* mechanics are untouched — they only
// move quantities between the flat window and the far maps and never assumed
// per-order granularity to begin with (Bump() during a spill is safe as a
// plain assignment because a tick can only ever live in the window OR the far
// map, never both, so the far map never has a pre-existing entry to bump).
public sealed class HybridBook
{
    private readonly int N, margin;
    private readonly long[] bidQ, askQ;              // circular: slot(t) = ((t%N)+N)%N
    private int lo;
    private bool inited;                              // window = [lo, lo+N)

    private readonly SortedDictionary<int, long> farBid = new(Comparer<int>.Create((a, b) => b.CompareTo(a))); // desc: first = highest
    private readonly SortedDictionary<int, long> farAsk = new();                                                // asc:  first = lowest

    private int bbTick = int.MinValue, baTick = int.MaxValue; // WINDOW bests (abs tick); MIN/MAX = window side empty

    public long shifts = 0, spills = 0, updates = 0;

    public HybridBook(int windowTicks)
    {
        N = windowTicks;
        margin = N / 4;
        bidQ = new long[N];
        askQ = new long[N];
    }

    private int Slot(int t) { int s = t % N; return s < 0 ? s + N : s; }
    private bool InWin(int t) => inited && t >= lo && t < lo + N;
    private static int First(SortedDictionary<int, long> d) { foreach (var kv in d) return kv.Key; return int.MinValue; }
    private int FarBidHigh() => farBid.Count > 0 ? First(farBid) : int.MinValue;
    private int FarAskLow() => farAsk.Count > 0 ? First(farAsk) : int.MaxValue;
    // true best = better of window best and far best (far can hold a crossed level outside the window)
    private int TrueBB() { int f = FarBidHigh(); return bbTick >= f ? bbTick : f; }
    private int TrueBA() { int f = FarAskLow(); return baTick <= f ? baTick : f; }

    private static void Bump(SortedDictionary<int, long> d, int t, long q) { d.TryGetValue(t, out long c); d[t] = c + q; }

    // Level-based replacement of the reference's Add(id,side,tick,qty)/Cancel(id).
    // qty<=0 removes the level; qty>0 REPLACES (not adds to) the level's qty.
    public void SetLevel(bool isBid, int tick, long qty)
    {
        updates++;
        if (qty > 0)
        {
            if (!inited) { lo = tick - N / 2; inited = true; }
            if (InWin(tick))
            {
                int s = Slot(tick);
                if (isBid) { bidQ[s] = qty; if (tick > bbTick) bbTick = tick; }
                else { askQ[s] = qty; if (tick < baTick) baTick = tick; }
                MaybeShift();
            }
            else
            {
                if (isBid) farBid[tick] = qty; else farAsk[tick] = qty;
                // far add matters for centering only when that window side is empty (best would live in far)
                if ((isBid && bbTick == int.MinValue) || (!isBid && baTick == int.MaxValue)) MaybeShift();
            }
        }
        else
        {
            if (InWin(tick))
            {
                int s = Slot(tick);
                if (isBid) { bidQ[s] = 0; if (tick == bbTick) RescanBid(); }
                else { askQ[s] = 0; if (tick == baTick) RescanAsk(); }
                MaybeShift();
            }
            else
            {
                var bk = isBid ? farBid : farAsk;
                bk.Remove(tick);
            }
        }
    }

    // next window best after the current one empties (scan within window only)
    private void RescanBid() { int t = bbTick - 1; while (t >= lo) { if (bidQ[Slot(t)] > 0) { bbTick = t; return; } t--; } bbTick = int.MinValue; }
    private void RescanAsk() { int top = lo + N - 1, t = baTick + 1; while (t <= top) { if (askQ[Slot(t)] > 0) { baTick = t; return; } t++; } baTick = int.MaxValue; }
    // full window rescan (only used if a shift spilled the current best — degenerate)
    private void RescanBidWin() { bbTick = int.MinValue; for (int t = lo + N - 1; t >= lo; t--) { if (bidQ[Slot(t)] > 0) { bbTick = t; break; } } }
    private void RescanAskWin() { baTick = int.MaxValue; for (int t = lo; t < lo + N; t++) { if (askQ[Slot(t)] > 0) { baTick = t; break; } } }

    private void MaybeShift()
    {
        int bb = bbTick != int.MinValue ? bbTick : FarBidHigh();
        int ba = baTick != int.MaxValue ? baTick : FarAskLow();
        if (bb == int.MinValue && ba == int.MaxValue) return;                 // empty book
        bool near = false;
        if (bbTick != int.MinValue && (bbTick - lo < margin || lo + N - 1 - bbTick < margin)) near = true;
        if (baTick != int.MaxValue && (baTick - lo < margin || lo + N - 1 - baTick < margin)) near = true;
        if (bbTick == int.MinValue && FarBidHigh() != int.MinValue) near = true; // no hot bid but far has one -> pull in
        if (baTick == int.MaxValue && FarAskLow() != int.MaxValue) near = true;  // no hot ask but far has one -> pull in
        if (!near) return;
        int mid = bb == int.MinValue ? ba : (ba == int.MaxValue ? bb : (int)(((long)bb + ba) / 2));
        Shift(mid - N / 2);
    }

    private void Shift(int newLo)
    {
        if (newLo == lo) return;
        int d = newLo - lo;
        if (d >= N || d <= -N) { FullRepop(newLo); shifts++; return; }   // best teleported > window -> rare full rebuild
        bool needBid = false, needAsk = false;
        if (d > 0)
        { // window up: spill bottom band [lo,lo+d), pull top band [lo+N,lo+N+d) -- same slots, already cleared
            for (int t = lo; t < lo + d; t++)
            {
                int s = Slot(t);
                if (bidQ[s] > 0) { Bump(farBid, t, bidQ[s]); if (t == bbTick) needBid = true; bidQ[s] = 0; spills++; }
                if (askQ[s] > 0) { Bump(farAsk, t, askQ[s]); if (t == baTick) needAsk = true; askQ[s] = 0; spills++; }
            }
            for (int t = lo + N; t < lo + N + d; t++)
            {
                int s = Slot(t);
                if (farBid.TryGetValue(t, out long qb)) { bidQ[s] = qb; farBid.Remove(t); if (t > bbTick) bbTick = t; }
                if (farAsk.TryGetValue(t, out long qa)) { askQ[s] = qa; farAsk.Remove(t); if (t < baTick) baTick = t; }
            }
        }
        else
        {
            int dd = -d; // window down: spill top band, pull bottom band (mirror)
            for (int t = lo + N - dd; t < lo + N; t++)
            {
                int s = Slot(t);
                if (bidQ[s] > 0) { Bump(farBid, t, bidQ[s]); if (t == bbTick) needBid = true; bidQ[s] = 0; spills++; }
                if (askQ[s] > 0) { Bump(farAsk, t, askQ[s]); if (t == baTick) needAsk = true; askQ[s] = 0; spills++; }
            }
            for (int t = newLo; t < newLo + dd; t++)
            {
                int s = Slot(t);
                if (farBid.TryGetValue(t, out long qb)) { bidQ[s] = qb; farBid.Remove(t); if (t > bbTick) bbTick = t; }
                if (farAsk.TryGetValue(t, out long qa)) { askQ[s] = qa; farAsk.Remove(t); if (t < baTick) baTick = t; }
            }
        }
        lo = newLo; shifts++;
        if (needBid) RescanBidWin();       // best sat in the spilled edge (rare) -> rebuild from window
        if (needAsk) RescanAskWin();
    }

    private void FullRepop(int newLo)
    {
        for (int t = lo; t < lo + N; t++)
        {
            int s = Slot(t);
            if (bidQ[s] > 0) { Bump(farBid, t, bidQ[s]); bidQ[s] = 0; }
            if (askQ[s] > 0) { Bump(farAsk, t, askQ[s]); askQ[s] = 0; }
        }
        lo = newLo; bbTick = int.MinValue; baTick = int.MaxValue;
        PullRange(farBid, true); PullRange(farAsk, false);
    }

    private void PullRange(SortedDictionary<int, long> far, bool isBid)
    {
        var move = new List<int>();
        foreach (var kv in far) if (kv.Key >= lo && kv.Key < lo + N) move.Add(kv.Key);
        foreach (var t in move)
        {
            long q = far[t]; far.Remove(t); int s = Slot(t);
            if (isBid) { bidQ[s] = q; if (t > bbTick) bbTick = t; } else { askQ[s] = q; if (t < baTick) baTick = t; }
        }
    }

    // Reset the whole book — needed when a venue (e.g. Bybit) re-sends a full snapshot and the
    // local book must be rebuilt from scratch rather than diffed.
    public void Clear()
    {
        Array.Clear(bidQ); Array.Clear(askQ);
        farBid.Clear(); farAsk.Clear();
        bbTick = int.MinValue; baTick = int.MaxValue;
        inited = false; lo = 0;
        shifts = spills = updates = 0;
    }

    public int BB() { int b = TrueBB(); return b == int.MinValue ? -1 : b; }
    public int BA() { int a = TrueBA(); return a == int.MaxValue ? -1 : a; }
    public int? BestBidTick() { int b = TrueBB(); return b == int.MinValue ? null : b; }
    public int? BestAskTick() { int a = TrueBA(); return a == int.MaxValue ? null : a; }

    // Resting market quantity at a price level — the queue an own order at that price sits BEHIND
    // (FIFO). This is the join that makes it one engine: overlay an own order on the live book and
    // read its queue-ahead straight out of the same structure that tracks the market.
    public long QtyAt(bool isBid, int tick)
    {
        if (InWin(tick)) return (isBid ? bidQ : askQ)[Slot(tick)];
        var far = isBid ? farBid : farAsk;
        return far.TryGetValue(tick, out var q) ? q : 0;
    }

    // Cumulative resting quantity within ±radiusTicks of mid — the depth-weighted imbalance input.
    // Sums the flat window AND the far map, so a wide radius (e.g. $6000) that spills past the hot
    // window still counts the cold-tail liquidity. Returns raw 1e-8 units per side.
    public (long bid, long ask) DepthSum(int radiusTicks)
    {
        int bb = TrueBB(), ba = TrueBA();
        if (bb == int.MinValue || ba == int.MaxValue) return (0, 0);
        long mid = ((long)bb + ba) / 2, bidLo = mid - radiusTicks, askHi = mid + radiusTicks;
        long bidSum = 0, askSum = 0;
        if (inited)
            for (int t = lo; t < lo + N; t++)
            {
                int s = Slot(t);
                if (t <= mid && t >= bidLo) bidSum += bidQ[s];
                if (t >= mid && t <= askHi) askSum += askQ[s];
            }
        foreach (var kv in farBid) if (kv.Key <= mid && kv.Key >= bidLo) bidSum += kv.Value;
        foreach (var kv in farAsk) if (kv.Key >= mid && kv.Key <= askHi) askSum += kv.Value;
        return (bidSum, askSum);
    }

    // Full-book signature over hot window + far map — used by ob_live_test to prove the
    // level-based port stays byte-for-byte equal to a naive SortedDictionary book at every
    // event (depth completeness, not just best-of-touch). Not on any hot path.
    public (long b, long a, int bl, int al) Sig()
    {
        long b = 0, a = 0; int bl = 0, al = 0;
        for (int t = lo; t < lo + N; t++) { int s = Slot(t); if (bidQ[s] > 0) { b += (long)t * 131 + bidQ[s]; bl++; } if (askQ[s] > 0) { a += (long)t * 131 + askQ[s]; al++; } }
        foreach (var kv in farBid) if (kv.Value > 0) { b += (long)kv.Key * 131 + kv.Value; bl++; }
        foreach (var kv in farAsk) if (kv.Value > 0) { a += (long)kv.Key * 131 + kv.Value; al++; }
        return (b, a, bl, al);
    }

    public int WindowLo => lo;
    public int WindowHi => lo + N;
    public int FarBidCount => farBid.Count;
    public int FarAskCount => farAsk.Count;

    // Memory footprint of the two structures, for the same "memory" story the offline
    // benchmark told (STATIC FLAT 32MB / ROLLING FLAT 1MB / HYBRID 1MB + far).
    // FlatBytes is exact: two long[N] arrays, 8 bytes/element, no per-slot overhead.
    // FarBytesApprox is an ESTIMATE, not exact -- .NET's SortedDictionary<int,long> is a
    // red-black tree; each node holds key(4B, padded)+value(8B)+3 refs(left/right/parent,
    // 8B each on 64-bit)+color/bookkeeping(~4B)+object header(16B) -- rounds to ~64B/entry
    // in practice. There's no cheap, honest way to measure this exactly from managed code
    // without a heap snapshot, so it's labeled "approx" everywhere it's surfaced rather than
    // presented as precise.
    private const int FAR_BYTES_PER_ENTRY_APPROX = 64;
    public long FlatBytes => (long)N * sizeof(long) * 2;
    public long FarBytesApprox => (long)(farBid.Count + farAsk.Count) * FAR_BYTES_PER_ENTRY_APPROX;

    // Depth ladder scanning OUTWARD from the window best, through the flat
    // window only (far map is intentionally excluded here -- it's the cold
    // tail, not part of the "near touch" ladder display). Stops early if it
    // walks off the window edge or hits `max` entries; never throws on a
    // thin book.
    public List<(double price, double qty)> BidLadder(int max)
    {
        var outp = new List<(double, double)>();
        if (bbTick == int.MinValue) return outp;
        for (int t = bbTick; t >= lo && outp.Count < max; t--)
        {
            long q = bidQ[Slot(t)];
            if (q > 0) outp.Add((t / 100.0, q / 1e8));
        }
        return outp;
    }

    public List<(double price, double qty)> AskLadder(int max)
    {
        var outp = new List<(double, double)>();
        if (baTick == int.MaxValue) return outp;
        int top = lo + N - 1;
        for (int t = baTick; t <= top && outp.Count < max; t++)
        {
            long q = askQ[Slot(t)];
            if (q > 0) outp.Add((t / 100.0, q / 1e8));
        }
        return outp;
    }

    // Order-book imbalance: (bidQty-askQty)/(bidQty+askQty) in [-1,+1], +1 = all bid, -1 = all ask.
    // Returns (L1, topK): L1 uses only the best level on each side (fragile -- one resting order
    // flips it); topK sums the first `k` levels on each side (steadier -- exists to demonstrate
    // that single-level imbalance is noise-prone vs. depth-weighted). Both computed from the flat
    // window only, same as the ladders -- this is resting-liquidity imbalance, NOT trade flow.
    public (double l1, double topK) Imbalance(int k)
    {
        var bids = BidLadder(k);
        var asks = AskLadder(k);
        double bidQ1 = bids.Count > 0 ? bids[0].qty : 0;
        double askQ1 = asks.Count > 0 ? asks[0].qty : 0;
        double bidSum = 0, askSum = 0;
        foreach (var b in bids) bidSum += b.qty;
        foreach (var a in asks) askSum += a.qty;
        double l1 = (bidQ1 + askQ1) > 0 ? (bidQ1 - askQ1) / (bidQ1 + askQ1) : 0;
        double topK = (bidSum + askSum) > 0 ? (bidSum - askSum) / (bidSum + askSum) : 0;
        return (l1, topK);
    }
}
