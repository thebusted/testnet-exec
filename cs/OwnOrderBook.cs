using System.Globalization;
using System.Text.Json;

namespace TestnetExec;

// Own-order lifecycle, keyed by clientOrderId. State is driven by the exchange's order-entry
// responses (status + fills) — the exchange's truth, applied through one Apply() path.
//   • Idempotent submit: a duplicate clientOrderId returns the existing order, never a second send.
//   • Transitions are idempotent on same-state but reject genuinely illegal ones (ACK after CANCEL),
//     so a duplicate/out-of-order update can't corrupt state.
// ponytail: state comes from request/response here. The push upgrade (unsolicited fills on resting
// orders) is the WS-API user-data stream — needs an Ed25519 session + listenToken; noted, not built.
public enum OrderState { New, Sent, Acked, PartiallyFilled, Filled, Canceled, Rejected }

public sealed class OwnOrder(string clientOrderId)
{
    public string ClientOrderId { get; } = clientOrderId;
    public OrderState State { get; private set; } = OrderState.New;
    public long? ExchangeId { get; set; }
    public decimal CumFilled { get; set; }
    public decimal AvgFillPrice { get; set; }
    public string? LastError { get; set; }

    private static readonly Dictionary<OrderState, OrderState[]> Allowed = new()
    {
        [OrderState.New] = [OrderState.Sent, OrderState.Rejected],
        [OrderState.Sent] = [OrderState.Acked, OrderState.Rejected, OrderState.Filled, OrderState.PartiallyFilled, OrderState.Canceled],
        [OrderState.Acked] = [OrderState.PartiallyFilled, OrderState.Filled, OrderState.Canceled, OrderState.Rejected],
        [OrderState.PartiallyFilled] = [OrderState.PartiallyFilled, OrderState.Filled, OrderState.Canceled],
        [OrderState.Filled] = [],
        [OrderState.Canceled] = [],
        [OrderState.Rejected] = [],
    };

    // false (not throw) on an illegal transition; true (no-op) on same-state re-delivery.
    public bool TryTransition(OrderState to)
    {
        if (to == State) return true;
        if (!Allowed[State].Contains(to)) return false;
        State = to;
        return true;
    }
}

public sealed class OwnOrderBook
{
    private readonly Dictionary<string, OwnOrder> _orders = new();
    private readonly object _lock = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public int IllegalTransitions { get; private set; }

    // Idempotent create; `created` is false if the clientOrderId was already live.
    public OwnOrder Submit(string clientOrderId, out bool created)
    {
        lock (_lock)
        {
            if (_orders.TryGetValue(clientOrderId, out var existing)) { created = false; return existing; }
            var o = new OwnOrder(clientOrderId);
            _orders[clientOrderId] = o;
            created = true;
            return o;
        }
    }

    // Fold an order.place / order.cancel response into the order's state + fills.
    public void Apply(OwnOrder o, JsonElement res)
    {
        lock (_lock)
        {
            if (res.TryGetProperty("executedQty", out var eq))
            {
                decimal exec = decimal.Parse(eq.GetString()!, Inv);
                if (exec > 0)
                {
                    o.CumFilled = exec;
                    decimal quote = decimal.Parse(res.GetProperty("cummulativeQuoteQty").GetString()!, Inv);
                    o.AvgFillPrice = quote / exec;
                }
            }
            var next = res.GetProperty("status").GetString() switch
            {
                "NEW" => OrderState.Acked,
                "PARTIALLY_FILLED" => OrderState.PartiallyFilled,
                "FILLED" => OrderState.Filled,
                "CANCELED" => OrderState.Canceled,
                "REJECTED" => OrderState.Rejected,
                "EXPIRED" => o.CumFilled > 0 ? OrderState.PartiallyFilled : OrderState.Rejected, // IOC leftover
                _ => (OrderState?)null,
            };
            if (next is { } s && !o.TryTransition(s)) IllegalTransitions++;
        }
    }

    public IReadOnlyList<OwnOrder> All { get { lock (_lock) return _orders.Values.ToList(); } }

    public Dictionary<OrderState, int> StateCounts()
    {
        lock (_lock)
        {
            var d = new Dictionary<OrderState, int>();
            foreach (var o in _orders.Values) d[o.State] = d.GetValueOrDefault(o.State) + 1;
            return d;
        }
    }
}
