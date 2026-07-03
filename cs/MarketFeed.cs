using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TestnetExec;

// Live market book: consumes the testnet depth-diff stream into a HybridBook, so the SAME structure
// that tracks the market can be queried for an own order's queue position. Bootstrap is the standard
// snapshot+diff reconciliation, simplified (no strict U/u straddle re-sync) since this is a queue demo,
// not a production feed — labelled approx where it matters.
public sealed class MarketFeed : IFeed, IAsyncDisposable
{
    private readonly HybridBook _book;
    private readonly HttpClient _http;
    private readonly string _wsBase, _symbol;
    private readonly object _sync = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public bool Ready { get; private set; }
    public string Venue => "binance";
    public Action? OnBookUpdate;   // fired after each applied diff — the reactor's producer hook

    public MarketFeed(HybridBook book, HttpClient http, string wsBase, string symbol)
    { _book = book; _http = http; _wsBase = wsBase; _symbol = symbol; }

    private static int Tick(string price) => (int)Math.Round(decimal.Parse(price, Inv) * 100m);
    private static long Qty(string qty) => (long)Math.Round(decimal.Parse(qty, Inv) * 100_000_000m);

    private void ApplySide(JsonElement rows, bool isBid)
    {
        foreach (var r in rows.EnumerateArray())
            _book.SetLevel(isBid, Tick(r[0].GetString()!), Qty(r[1].GetString()!));
    }

    public async Task StartAsync()
    {
        var buffer = new List<JsonElement>();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri($"{_wsBase}/ws/{_symbol.ToLower()}@depth@100ms"), default);
        _cts = new CancellationTokenSource();

        // 1) snapshot, 2) drop diffs older than it, 3) apply the rest, then stream live — all under one lock.
        long lastUpdateId;
        using (var snap = JsonDocument.Parse(await _http.GetStringAsync($"/api/v3/depth?symbol={_symbol}&limit=5000")))
        {
            lastUpdateId = snap.RootElement.GetProperty("lastUpdateId").GetInt64();
            lock (_sync)
            {
                ApplySide(snap.RootElement.GetProperty("bids"), true);
                ApplySide(snap.RootElement.GetProperty("asks"), false);
            }
        }
        Ready = true;
        _ = Task.Run(() => Loop(lastUpdateId, _cts.Token));
    }

    private async Task Loop(long lastUpdateId, CancellationToken ct)
    {
        var buf = new byte[128 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(buf, ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                } while (!res.EndOfMessage);

                using var doc = JsonDocument.Parse(sb.ToString());
                var m = doc.RootElement;
                if (m.GetProperty("u").GetInt64() <= lastUpdateId) continue;   // stale vs snapshot
                lock (_sync)
                {
                    ApplySide(m.GetProperty("b"), true);
                    ApplySide(m.GetProperty("a"), false);
                }
                lastUpdateId = m.GetProperty("u").GetInt64();
                OnBookUpdate?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { }
    }

    // Locked reads so the demo never queries a half-applied diff.
    public (int? bidTick, int? askTick) Best() { lock (_sync) return (_book.BestBidTick(), _book.BestAskTick()); }
    public long QtyAt(bool isBid, int tick) { lock (_sync) return _book.QtyAt(isBid, tick); }
    public (long bid, long ask) DepthSum(int radiusTicks) { lock (_sync) return _book.DepthSum(radiusTicks); }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default); } catch { }
        _ws?.Dispose();
    }
}
