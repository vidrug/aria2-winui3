using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Aria2Gui.Services.Aria2;

/// <summary>Error returned by the aria2 JSON-RPC endpoint.</summary>
public sealed class Aria2RpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Allocation-conscious JSON-RPC 2.0 client over WebSocket for a local aria2c.
/// Requests are built with <see cref="Utf8JsonWriter"/> and responses parsed with
/// <see cref="JsonDocument"/> + source-generated models — no reflection anywhere.
/// </summary>
public sealed class Aria2RpcClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Window size for tellWaiting/tellStopped. aria2 caps its stopped history at
    /// --max-download-result (default 1000), and queues beyond this size are far
    /// outside this GUI's use case; the snapshot consumer additionally guards
    /// against truncation before pruning rows.
    /// </summary>
    private const int TellWindow = 10_000;

    /// <summary>Fields requested from tell* — keeps poll payloads small.</summary>
    private static readonly string[] TellKeys =
    [
        "gid", "status", "totalLength", "completedLength", "uploadLength",
        "downloadSpeed", "uploadSpeed", "connections", "numSeeders", "seeder",
        "errorCode", "errorMessage", "dir", "bittorrent", "files", "infoHash",
        "followedBy", "following",
    ];

    private readonly ConcurrentDictionary<long, IPendingCall> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // Serializes ConnectAsync / CloseSocketAsync / DisposeAsync so a reconnect can
    // never race teardown (double-disposed CTS, orphaned receive loop).
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private string _tokenParam = "";
    private long _nextId;
    private volatile bool _disposed;

    /// <summary>Raised from a worker thread for aria2.onDownload* push events: (method, gid).</summary>
    public event Action<string, string>? DownloadNotification;

    /// <summary>Raised from a worker thread when the socket drops (not on explicit dispose).</summary>
    public event Action? ConnectionClosed;

    public bool IsConnected => _socket is { State: WebSocketState.Open };

    public async Task ConnectAsync(int port, string secret, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await CloseSocketCoreAsync().ConfigureAwait(false);

            _tokenParam = "token:" + secret;
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/jsonrpc"), ct).ConfigureAwait(false);

            _socket = socket;
            _receiveCts = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, _receiveCts.Token), CancellationToken.None);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // ---------------------------------------------------------------- typed API

    public Task<string> AddUriAsync(IReadOnlyList<string> uris, IReadOnlyDictionary<string, string>? options = null, CancellationToken ct = default) =>
        InvokeAsync("aria2.addUri", w =>
        {
            w.WriteStartArray();
            foreach (var uri in uris)
                w.WriteStringValue(uri);
            w.WriteEndArray();
            WriteOptions(w, options);
        }, static r => r.GetString() ?? "", ct);

    public Task<string> AddTorrentAsync(byte[] torrent, IReadOnlyDictionary<string, string>? options = null, CancellationToken ct = default) =>
        InvokeAsync("aria2.addTorrent", w =>
        {
            w.WriteBase64StringValue(torrent);
            w.WriteStartArray(); // web-seed URIs (none)
            w.WriteEndArray();
            WriteOptions(w, options);
        }, static r => r.GetString() ?? "", ct);

    public Task PauseAsync(string gid, bool force = false, CancellationToken ct = default) =>
        InvokeAsync(force ? "aria2.forcePause" : "aria2.pause", w => w.WriteStringValue(gid), ct);

    public Task UnpauseAsync(string gid, CancellationToken ct = default) =>
        InvokeAsync("aria2.unpause", w => w.WriteStringValue(gid), ct);

    public Task PauseAllAsync(CancellationToken ct = default) =>
        InvokeAsync("aria2.pauseAll", null, ct);

    public Task UnpauseAllAsync(CancellationToken ct = default) =>
        InvokeAsync("aria2.unpauseAll", null, ct);

    /// <summary>Removes an active/waiting download (moves it to stopped with status "removed").</summary>
    public Task RemoveAsync(string gid, bool force = false, CancellationToken ct = default) =>
        InvokeAsync(force ? "aria2.forceRemove" : "aria2.remove", w => w.WriteStringValue(gid), ct);

    /// <summary>Removes a completed/errored/removed entry from the stopped list.</summary>
    public Task RemoveDownloadResultAsync(string gid, CancellationToken ct = default) =>
        InvokeAsync("aria2.removeDownloadResult", w => w.WriteStringValue(gid), ct);

    public Task PurgeDownloadResultAsync(CancellationToken ct = default) =>
        InvokeAsync("aria2.purgeDownloadResult", null, ct);

    public Task ChangeGlobalOptionAsync(IReadOnlyDictionary<string, string> options, CancellationToken ct = default) =>
        InvokeAsync("aria2.changeGlobalOption", w => WriteOptions(w, options), ct);

    /// <summary>Changes a single download's options at runtime (e.g. select-file).</summary>
    public Task ChangeOptionAsync(string gid, IReadOnlyDictionary<string, string> options, CancellationToken ct = default) =>
        InvokeAsync("aria2.changeOption", w =>
        {
            w.WriteStringValue(gid);
            WriteOptions(w, options);
        }, ct);

    /// <summary>Reads all of one download's current options (aria2.getOption) as a string map.
    /// Used to carry per-download options across a recheck re-add.</summary>
    public Task<Dictionary<string, string>> GetOptionAsync(string gid, CancellationToken ct = default) =>
        InvokeAsync("aria2.getOption", w => w.WriteStringValue(gid), static result =>
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (result.ValueKind == JsonValueKind.Object)
                foreach (var prop in result.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } s)
                        map[prop.Name] = s;
            return map;
        }, ct);

    /// <summary>Reads one download's current per-download speed limits as aria2 speed strings
    /// ("0" = no limit). Used to pre-fill the per-download speed-limit flyout.</summary>
    public Task<(string down, string up)> GetSpeedLimitsAsync(string gid, CancellationToken ct = default) =>
        InvokeAsync("aria2.getOption", w => w.WriteStringValue(gid), static result =>
        {
            string Read(string key) =>
                result.ValueKind == JsonValueKind.Object && result.TryGetProperty(key, out var v)
                    ? v.GetString() ?? "0"
                    : "0";
            return (Read("max-download-limit"), Read("max-upload-limit"));
        }, ct);

    public Task<Aria2VersionInfo> GetVersionAsync(CancellationToken ct = default) =>
        InvokeAsync("aria2.getVersion", null,
            static r => r.Deserialize(Aria2JsonContext.Default.Aria2VersionInfo) ?? new Aria2VersionInfo(), ct);

    public Task ShutdownAsync(bool force = false, CancellationToken ct = default) =>
        InvokeAsync(force ? "aria2.forceShutdown" : "aria2.shutdown", null, ct);

    /// <summary>Writes the session file to disk immediately — lets us kill aria2c
    /// fast on exit without waiting for its (slow) graceful shutdown to persist it.</summary>
    public Task SaveSessionAsync(CancellationToken ct = default) =>
        InvokeAsync("aria2.saveSession", null, ct);

    /// <summary>Connected peers of a BitTorrent download (errors for non-BT gids).</summary>
    public Task<List<Aria2Peer>> GetPeersAsync(string gid, CancellationToken ct = default) =>
        InvokeAsync("aria2.getPeers", w => w.WriteStringValue(gid),
            static r => r.Deserialize(Aria2JsonContext.Default.ListAria2Peer) ?? [], ct);

    /// <summary>
    /// Fetches active + waiting + stopped downloads and global transfer stats in a
    /// single system.multicall round-trip — one WebSocket message per poll tick.
    /// </summary>
    public Task<Aria2Snapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        InvokeAsync("system.multicall", w =>
        {
            w.WriteStartArray();
            WriteMulticallEntry(w, "aria2.tellActive", static w2 => WriteKeys(w2));
            WriteMulticallEntry(w, "aria2.tellWaiting", static w2 =>
            {
                w2.WriteNumberValue(0);
                w2.WriteNumberValue(TellWindow);
                WriteKeys(w2);
            });
            WriteMulticallEntry(w, "aria2.tellStopped", static w2 =>
            {
                w2.WriteNumberValue(0);
                w2.WriteNumberValue(TellWindow);
                WriteKeys(w2);
            });
            WriteMulticallEntry(w, "aria2.getGlobalStat", null);
            w.WriteEndArray();
        }, ParseSnapshot, ct);

    // Parsed on the receive loop directly off the live multicall result (no Clone — O1).
    private static Aria2Snapshot ParseSnapshot(JsonElement result)
    {
        var downloads = new List<Aria2Download>();
        var stat = new Aria2GlobalStat();

        if (result.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var slot in result.EnumerateArray())
            {
                // On success each slot is a one-element array; on failure it is a fault object.
                if (slot.ValueKind == JsonValueKind.Array && slot.GetArrayLength() > 0)
                {
                    var value = slot[0];
                    if (index < 3)
                        downloads.AddRange(value.Deserialize(Aria2JsonContext.Default.ListAria2Download) ?? []);
                    else
                        stat = value.Deserialize(Aria2JsonContext.Default.Aria2GlobalStat) ?? stat;
                }
                index++;
            }
        }

        return new Aria2Snapshot(downloads, stat);
    }

    // ---------------------------------------------------------------- JSON-RPC plumbing

    /// <summary>A response handler that parses the LIVE result element inside the receive loop
    /// (before the pooled JsonDocument is disposed), so we never deep-clone the whole subtree (O1).</summary>
    private interface IPendingCall
    {
        void Complete(JsonElement result);
        void Fail(Exception ex);
        void Cancel();
    }

    private sealed class PendingCall<T>(Func<JsonElement, T> parse) : IPendingCall
    {
        public TaskCompletionSource<T> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(JsonElement result)
        {
            try { Tcs.TrySetResult(parse(result)); }
            catch (Exception ex) { Tcs.TrySetException(ex); }
        }
        public void Fail(Exception ex) => Tcs.TrySetException(ex);
        public void Cancel() => Tcs.TrySetCanceled();
    }

    /// <summary>Fire-and-forget call whose result is ignored (pause, remove, changeOption, …).</summary>
    private Task InvokeAsync(string method, Action<Utf8JsonWriter>? writeArgs, CancellationToken ct) =>
        InvokeAsync(method, writeArgs, static _ => true, ct);

    private async Task<T> InvokeAsync<T>(string method, Action<Utf8JsonWriter>? writeArgs, Func<JsonElement, T> parse, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var socket = _socket ?? throw new InvalidOperationException("RPC client is not connected.");

        long id = Interlocked.Increment(ref _nextId);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writer.WriteStartArray("params");
            // The secret token is the first param of aria2.* calls; system.* calls
            // carry it inside each nested method instead.
            if (method.StartsWith("aria2.", StringComparison.Ordinal))
                writer.WriteStringValue(_tokenParam);
            writeArgs?.Invoke(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var pending = new PendingCall<T>(parse);
        _pending[id] = pending;
        try
        {
            // The timeout covers the whole round-trip, including waiting for the send
            // lock — a wedged aria2c must not block every caller forever.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);
            try
            {
                await _sendLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                try
                {
                    await socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, timeoutCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }

                await using (timeoutCts.Token.Register(static state => ((IPendingCall)state!).Cancel(), pending).ConfigureAwait(false))
                {
                    return await pending.Tcs.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout, not caller cancellation — surface a typed, catchable error.
                throw new TimeoutException($"aria2 RPC request timed out: {method}");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void WriteMulticallEntry(Utf8JsonWriter w, string method, Action<Utf8JsonWriter>? writeArgs)
    {
        w.WriteStartObject();
        w.WriteString("methodName", method);
        w.WriteStartArray("params");
        w.WriteStringValue(_tokenParam);
        writeArgs?.Invoke(w);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteKeys(Utf8JsonWriter w)
    {
        w.WriteStartArray();
        foreach (var key in TellKeys)
            w.WriteStringValue(key);
        w.WriteEndArray();
    }

    private static void WriteOptions(Utf8JsonWriter w, IReadOnlyDictionary<string, string>? options)
    {
        if (options is null || options.Count == 0)
            return;
        w.WriteStartObject();
        foreach (var (key, value) in options)
            w.WriteString(key, value);
        w.WriteEndObject();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var message = new ArrayBufferWriter<byte>(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.Clear();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(rented.AsMemory(), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(rented.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                try
                {
                    HandleMessage(message.WrittenMemory);
                }
                catch (Exception)
                {
                    // Malformed frame or a throwing notification subscriber must not
                    // tear down the connection; the matching request will time out.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            FailAllPending();
            if (!_disposed && !ct.IsCancellationRequested)
                ConnectionClosed?.Invoke();
        }
    }

    private void HandleMessage(ReadOnlyMemory<byte> payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return;

        // Push notification: {"method":"aria2.onDownloadComplete","params":[{"gid":"..."}]}
        if (root.TryGetProperty("method", out var methodProp))
        {
            string method = methodProp.GetString() ?? "";
            if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in paramsProp.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("gid", out var gidProp))
                        DownloadNotification?.Invoke(method, gidProp.GetString() ?? "");
                }
            }
            return;
        }

        // Response: {"id":N,"result":...} or {"id":N,"error":{"code":...,"message":"..."}}
        if (!root.TryGetProperty("id", out var idProp))
            return;
        long id = idProp.ValueKind switch
        {
            JsonValueKind.Number => idProp.GetInt64(),
            JsonValueKind.String when long.TryParse(idProp.GetString(), out var parsed) => parsed,
            _ => -1,
        };
        if (!_pending.TryRemove(id, out var pending))
            return;

        if (root.TryGetProperty("error", out var error))
        {
            int code = error.TryGetProperty("code", out var codeProp) && codeProp.TryGetInt32(out var c) ? c : 0;
            string msg = error.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "RPC error" : "RPC error";
            pending.Fail(new Aria2RpcException(code, msg));
        }
        else if (root.TryGetProperty("result", out var resultProp))
        {
            // Parse the live element here (still inside the using) instead of deep-cloning the
            // whole result subtree just to deserialize it on the awaiting thread (O1).
            pending.Complete(resultProp);
        }
        else
        {
            pending.Fail(new Aria2RpcException(0, "Malformed RPC response."));
        }
    }

    private void FailAllPending()
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var call))
                call.Fail(new Aria2RpcException(-1, Aria2Gui.Helpers.L.Get("RpcConnectionLost")));
        }
    }

    /// <summary>Must be called while holding <see cref="_connectLock"/>.</summary>
    private async Task CloseSocketCoreAsync()
    {
        var cts = _receiveCts;
        var socket = _socket;
        var receiveTask = _receiveTask;
        _receiveCts = null;
        _socket = null;
        _receiveTask = null;

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }
        if (receiveTask is not null)
        {
            try { await receiveTask.ConfigureAwait(false); }
            catch { /* receive loop swallows its own errors */ }
        }
        socket?.Dispose();
        FailAllPending();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSocketCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
        // _sendLock/_connectLock are intentionally not disposed: SemaphoreSlim holds no
        // unmanaged resources without AvailableWaitHandle, and disposing while async
        // waiters are queued would strand them forever.
    }
}
