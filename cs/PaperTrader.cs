namespace TestnetExec;

// Pure paper trade — no orders, marks against the live mainnet book. Momentum on depth-weighted
// imbalance: go long when near-touch imbalance is strongly bid-heavy, short when ask-heavy, flatten
// when it flips. P&L is realized net of a round-trip taker fee so the result is honest, not inflated.
// The whole point is to MEASURE whether the imbalance signal has edge after costs — it may not, and
// that's a real answer (would say: imbalance alone isn't enough, needs confirmation / rekt).
public enum Pos { Flat, Long, Short }

// Tuned to survive costs: stronger entry (0.25) so weak/noisy imbalance doesn't trigger, a min-hold so
// the position isn't whipsawed out on a 1-tick flip, and a maker fee (0.0002) on the assumption the
// entry rests at the touch rather than crossing the spread — the honest levers to beat the taker-fee bleed.
public sealed class PaperTrader(string venue, int signalRadiusTicks = 5000, double entryTH = 0.25, double exitTH = 0.10, double qty = 0.01, double fee = 0.0002, int minHoldTicks = 8)
{
    private int _held;
    public string Venue { get; } = venue;
    public Pos Position { get; private set; } = Pos.Flat;
    public double EntryPrice { get; private set; }
    public double RealizedPnl { get; private set; }   // USDT, net of fees
    public double FeesPaid { get; private set; }
    public int Trades { get; private set; }
    public int Wins { get; private set; }
    public double LastImb { get; private set; }
    public double LastMid { get; private set; }

    public double WinRate => Trades > 0 ? (double)Wins / Trades : 0;
    public double Unrealized => Position == Pos.Long ? qty * (LastMid - EntryPrice)
                              : Position == Pos.Short ? qty * (EntryPrice - LastMid) : 0;
    public double EquityPnl => RealizedPnl + Unrealized;

    public void OnTick(int? bidTick, int? askTick, (long bid, long ask) depth)
    {
        if (bidTick is not int bt || askTick is not int at) return;
        double bid = bt / 100.0, ask = at / 100.0;
        LastMid = (bid + ask) / 2;
        long tot = depth.bid + depth.ask;
        LastImb = tot > 0 ? (double)(depth.bid - depth.ask) / tot : 0;

        if (Position != Pos.Flat) _held++;
        switch (Position)
        {
            case Pos.Flat:
                if (LastImb > entryTH) Enter(Pos.Long, ask);          // rest-buy at the offer (maker assumption)
                else if (LastImb < -entryTH) Enter(Pos.Short, bid);   // rest-sell at the bid
                break;
            case Pos.Long:
                if (_held >= minHoldTicks && LastImb < -exitTH) Exit(bid);  // held long enough + flipped -> flatten
                break;
            case Pos.Short:
                if (_held >= minHoldTicks && LastImb > exitTH) Exit(ask);
                break;
        }
    }

    private void Enter(Pos p, double price) { Position = p; EntryPrice = price; _held = 0; }

    private void Exit(double price)
    {
        double gross = Position == Pos.Long ? qty * (price - EntryPrice) : qty * (EntryPrice - price);
        double roundTripFee = qty * (EntryPrice + price) * fee;
        double net = gross - roundTripFee;
        RealizedPnl += net; FeesPaid += roundTripFee; Trades++;
        if (net > 0) Wins++;
        Position = Pos.Flat; EntryPrice = 0;
    }

    public int SignalRadiusTicks => signalRadiusTicks;
}
