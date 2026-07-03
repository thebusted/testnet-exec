using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TestnetExec;

// Common surface both venue feeds expose so the paper trader can treat Binance and Bybit uniformly.
public interface IFeed
{
    string Venue { get; }
    Task StartAsync();
    (int? bidTick, int? askTick) Best();
    (long bid, long ask) DepthSum(int radiusTicks);
}

// Bybit V5 spot orderbook feed (mainnet, public — no key). Protocol differs from Binance:
//   subscribe {"op":"subscribe","args":["orderbook.200.BTCUSDT"]} on wss://stream.bybit.com/v5/public/spot,
//   then a "snapshot" (full book, must RESET local) followed by "delta" messages (size "0" = remove level).
//   A fresh snapshot can arrive any time and means "rebuild from scratch". Ping every 20s to stay connected.
public sealed class BybitFeed : IFeed, IAsyncDisposable
{
    private readonly HybridBook _book;
    private readonly string _symbol;
    private readonly object _sync = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public string Venue => "bybit";

    public BybitFeed(HybridBook book, string symbol) { _book = book; _symbol = symbol; }

    private static int Tick(string p) => (int)Math.Round(decimal.Parse(p, Inv) * 100m);
    private static long Qty(string s) => (long)Math.Round(decimal.Parse(s, Inv) * 100_000_000m);

    private void ApplySide(JsonElement rows, bool isBid)
    {
        foreach (var r in rows.EnumerateArray())
            _book.SetLevel(isBid, Tick(r[0].GetString()!), Qty(r[1].GetString()!));
    }

    public async Task StartAsync()
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri("wss://stream.bybit.com/v5/public/spot"), default);
        var sub = JsonSerializer.Serialize(new { op = "subscribe", args = new[] { $"orderbook.200.{_symbol}" } });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, default);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
        _ = Task.Run(() => PingLoop(_cts.Token));
    }

    private async Task PingLoop(CancellationToken ct)
    {
        var ping = Encoding.UTF8.GetBytes("{\"op\":\"ping\"}");
        try { while (!ct.IsCancellationRequested) { await Task.Delay(20000, ct); if (_ws!.State == WebSocketState.Open) await _ws.SendAsync(ping, WebSocketMessageType.Text, true, ct); } }
        catch (OperationCanceledException) { }
    }

    private async Task Loop(CancellationToken ct)
    {
        var buf = new byte[256 * 1024];
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
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data) || !root.TryGetProperty("type", out var typeEl)) continue;
                lock (_sync)
                {
                    if (typeEl.GetString() == "snapshot") _book.Clear();   // full book -> rebuild
                    if (data.TryGetProperty("b", out var b)) ApplySide(b, true);
                    if (data.TryGetProperty("a", out var a)) ApplySide(a, false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { }
    }

    public (int? bidTick, int? askTick) Best() { lock (_sync) return (_book.BestBidTick(), _book.BestAskTick()); }
    public (long bid, long ask) DepthSum(int radiusTicks) { lock (_sync) return _book.DepthSum(radiusTicks); }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default); } catch { }
        _ws?.Dispose();
    }
}
