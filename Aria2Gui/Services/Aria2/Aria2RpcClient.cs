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

    /// <summary>Fields requested from tell* — keeps poll payloads small.</summary>
    private static readonly string[] TellKeys =
    [
        "gid", "status", "totalLength", "completedLength", "uploadLength",
        "downloadSpeed", "uploadSpeed", "connections", "numSeeders", "seeder",
        "errorCode", "errorMessage", "dir", "bittorrent", "files", "infoHash",
        "followedBy", "following",
    ];

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
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
        await CloseSocketAsync().ConfigureAwait(false);

        _tokenParam = "token:" + secret;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/jsonrpc"), ct).ConfigureAwait(false);

        _socket = socket;
        _receiveCts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, _receiveCts.Token), CancellationToken.None);
    }

    // ---------------------------------------------------------------- typed API

    public async Task<string> AddUriAsync(IReadOnlyList<string> uris, IReadOnlyDictionary<string, string>? options = null, CancellationToken ct = default)
    {
        var result = await InvokeAsync("aria2.addUri", w =>
        {
            w.WriteStartArray();
            foreach (var uri in uris)
                w.WriteStringValue(uri);
            w.WriteEndArray();
            WriteOptions(w, options);
        }, ct).ConfigureAwait(false);
        return result.GetString() ?? "";
    }

    public async Task<string> AddTorrentAsync(byte[] torrent, IReadOnlyDictionary<string, string>? options = null, CancellationToken ct = default)
    {
        var result = await InvokeAsync("aria2.addTorrent", w =>
        {
            w.WriteBase64StringValue(torrent);
            w.WriteStartArray(); // web-seed URIs (none)
            w.WriteEndArray();
            WriteOptions(w, options);
        }, ct).ConfigureAwait(false);
        return result.GetString() ?? "";
    }

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

    public async Task<Aria2VersionInfo> GetVersionAsync(CancellationToken ct = default)
    {
        var result = await InvokeAsync("aria2.getVersion", null, ct).ConfigureAwait(false);
        return result.Deserialize(Aria2JsonContext.Default.Aria2VersionInfo) ?? new Aria2VersionInfo();
    }

    public Task ShutdownAsync(bool force = false, CancellationToken ct = default) =>
        InvokeAsync(force ? "aria2.forceShutdown" : "aria2.shutdown", null, ct);

    /// <summary>
    /// Fetches active + waiting + stopped downloads and global transfer stats in a
    /// single system.multicall round-trip — one WebSocket message per poll tick.
    /// </summary>
    public async Task<Aria2Snapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var result = await InvokeAsync("system.multicall", w =>
        {
            w.WriteStartArray();
            WriteMulticallEntry(w, "aria2.tellActive", static w2 => WriteKeys(w2));
            WriteMulticallEntry(w, "aria2.tellWaiting", static w2 =>
            {
                w2.WriteNumberValue(0);
                w2.WriteNumberValue(1000);
                WriteKeys(w2);
            });
            WriteMulticallEntry(w, "aria2.tellStopped", static w2 =>
            {
                w2.WriteNumberValue(0);
                w2.WriteNumberValue(1000);
                WriteKeys(w2);
            });
            WriteMulticallEntry(w, "aria2.getGlobalStat", null);
            w.WriteEndArray();
        }, ct).ConfigureAwait(false);

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

    private async Task<JsonElement> InvokeAsync(string method, Action<Utf8JsonWriter>? writeArgs, CancellationToken ct)
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

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);
            await using (timeoutCts.Token.Register(static state => ((TaskCompletionSource<JsonElement>)state!).TrySetCanceled(), tcs).ConfigureAwait(false))
            {
                return await tcs.Task.ConfigureAwait(false);
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
                catch (JsonException)
                {
                    // Malformed frame — ignore; the matching request will time out.
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
        if (!_pending.TryRemove(id, out var tcs))
            return;

        if (root.TryGetProperty("error", out var error))
        {
            int code = error.TryGetProperty("code", out var codeProp) && codeProp.TryGetInt32(out var c) ? c : 0;
            string msg = error.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "RPC error" : "RPC error";
            tcs.TrySetException(new Aria2RpcException(code, msg));
        }
        else if (root.TryGetProperty("result", out var resultProp))
        {
            // Clone so the element outlives the pooled JsonDocument.
            tcs.TrySetResult(resultProp.Clone());
        }
        else
        {
            tcs.TrySetException(new Aria2RpcException(0, "Malformed RPC response."));
        }
    }

    private void FailAllPending()
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
        }
    }

    private async Task CloseSocketAsync()
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
        await CloseSocketAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
