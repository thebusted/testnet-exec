using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace TestnetExec;

// Raw Binance Spot WebSocket order-entry client — no ccxt, no Binance SDK. The request signing,
// the sorted-params payload, and the id↔response correlation are all hand-rolled, because being
// able to say "I implemented the WS order-entry protocol myself" is the whole point of this piece.
//
// Protocol (verified against binance-spot-api-docs/web-socket-api.md):
//   • one JSON request per frame: { "id": <uuid>, "method": "order.place", "params": { ... } }
//   • SIGNED methods: params must include apiKey + timestamp, and a `signature` =
//     HMAC-SHA256( secret , params-sorted-alphabetically-as-"k=v&k=v" ) hex-encoded.
//     NOTE the sort — REST does not sort params, the WS API DOES. Getting this wrong = -1022.
//   • response: { "id", "status": 200, "result": {...} } or { "id", "status": 4xx, "error": {code,msg} }
public sealed class WsOrderClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly string _apiKey, _secret;
    private readonly Uri _uri;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private CancellationTokenSource? _rxCts;
    private long _serverOffsetMs;

    public WsOrderClient(string uri, string apiKey, string secret)
    {
        _uri = new Uri(uri);
        _apiKey = apiKey;
        _secret = secret;
    }

    // recvWindow protects against a stale request; timestamp must track server time or you get -1021.
    public void SetServerOffset(long offsetMs) => _serverOffsetMs = offsetMs;
    private long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _serverOffsetMs;

    private Func<Task<long>>? _resync;
    public int ClockResyncs { get; private set; }
    // Give the client a way to refresh the clock offset so it can self-heal a -1021 and retry once.
    public void SetResync(Func<Task<long>> resync) => _resync = resync;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _ws.ConnectAsync(_uri, ct);
        _rxCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoop(_rxCts.Token));
    }

    private string Sign(SortedDictionary<string, string> p)
    {
        var payload = string.Join("&", p.Select(kv => $"{kv.Key}={kv.Value}"));
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    // Send a SIGNED request; self-heal a clock-skew (-1021) once by resyncing the offset and retrying.
    public async Task<JsonElement> SendSignedAsync(string method, SortedDictionary<string, string> p)
    {
        try { return await SendOnceAsync(method, p); }
        catch (BinanceWsError e) when (e.Code == -1021 && _resync is not null)
        {
            SetServerOffset(await _resync());   // fresh clock -> retry with a new timestamp + signature
            ClockResyncs++;
            return await SendOnceAsync(method, p);
        }
    }

    // One signed round trip, awaiting the id-correlated response. Throws BinanceWsError on non-200.
    private async Task<JsonElement> SendOnceAsync(string method, SortedDictionary<string, string> p)
    {
        p["apiKey"] = _apiKey;
        p["recvWindow"] = "10000";
        p["timestamp"] = Now().ToString();
        p.Remove("signature");      // re-signable: on a retry, a stale signature must not pollute the payload
        p["signature"] = Sign(p);   // sign covers everything above; signature itself is added after

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var req = JsonSerializer.Serialize(new { id, method, @params = p });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (timeout.Token.Register(() => tcs.TrySetException(new TimeoutException($"{method} timed out"))))
            return await tcs.Task;
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(buf, ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                } while (!res.EndOfMessage);

                Dispatch(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { foreach (var p in _pending.Values) p.TrySetException(e); }
    }

    private void Dispatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl)) return;             // stream events (no id) — not used here
        if (!_pending.TryRemove(idEl.GetString()!, out var tcs)) return;

        var status = root.GetProperty("status").GetInt32();
        if (status == 200)
            tcs.TrySetResult(root.GetProperty("result").Clone());
        else
        {
            var err = root.GetProperty("error");
            tcs.TrySetException(new BinanceWsError(err.GetProperty("code").GetInt32(), err.GetProperty("msg").GetString() ?? ""));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _rxCts?.Cancel();
        if (_ws.State == WebSocketState.Open)
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        _ws.Dispose();
    }
}

public sealed class BinanceWsError(int code, string msg) : Exception($"{code}: {msg}")
{
    public int Code { get; } = code;
}
