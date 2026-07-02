using System.Threading.Channels;

namespace TestnetExec;

// S4 — the "async event-driven algorithm base class". A strategy overrides handlers; the reactor
// pumps events (market book ticks, own-order updates) through a single-consumer channel and dispatches
// them to the strategy IN ARRIVAL ORDER. Single reader = the strategy sees a serialized event stream
// and needs no locking of its own. Producers (the market feed thread, the order path) just Post.
public abstract class Strategy
{
    public abstract Task OnBookTick(int bidTick, int askTick, long bidQty, long askQty);
    public virtual Task OnOwnOrder(OwnOrder o) => Task.CompletedTask;
}

public sealed class EventReactor(Strategy strategy)
{
    private readonly Channel<Func<Task>> _ch = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    public long Dispatched { get; private set; }

    // Each event is captured as a closure over the strategy call — the reactor just awaits them in order.
    public void PostBook(int bidTick, int askTick, long bidQty, long askQty)
        => _ch.Writer.TryWrite(() => strategy.OnBookTick(bidTick, askTick, bidQty, askQty));
    public void PostOwnOrder(OwnOrder o) => _ch.Writer.TryWrite(() => strategy.OnOwnOrder(o));
    public void Complete() => _ch.Writer.TryComplete();

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var handler in _ch.Reader.ReadAllAsync(ct)) { await handler(); Dispatched++; }
        }
        catch (OperationCanceledException) { }
    }
}

// Sample strategy: reacts to the live book by tracking L1 (best-level) resting-liquidity imbalance and
// logging each time it flips bid-heavy <-> ask-heavy past a threshold. Shows the base class reacting to
// a live event stream without touching threads or locks itself.
public sealed class ImbalanceReactor : Strategy
{
    private int _lastSign;
    public int Flips { get; private set; }

    public override Task OnBookTick(int bidTick, int askTick, long bidQty, long askQty)
    {
        long tot = bidQty + askQty;
        double imb = tot > 0 ? (double)(bidQty - askQty) / tot : 0;
        int sign = imb > 0.2 ? 1 : imb < -0.2 ? -1 : 0;
        if (sign != 0 && sign != _lastSign)
        {
            Flips++;
            Console.WriteLine($"  [strategy] L1 imbalance {(sign > 0 ? "BID" : "ASK")}-heavy {imb:+0.00;-0.00} @ {bidTick / 100.0:0.00}/{askTick / 100.0:0.00}");
            _lastSign = sign;
        }
        return Task.CompletedTask;
    }

    public override Task OnOwnOrder(OwnOrder o)
    {
        Console.WriteLine($"  [strategy] own {o.ClientOrderId} -> {o.State}");
        return Task.CompletedTask;
    }
}
