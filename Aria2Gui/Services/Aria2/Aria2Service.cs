using System.Globalization;

namespace Aria2Gui.Services.Aria2;

public enum Aria2ServiceState
{
    Stopped,
    Starting,
    Running,
    Restarting,
    Failed,
}

/// <summary>
/// Facade that ties the aria2c child process and the RPC client together:
/// startup with connect-retry, auto-restart when aria2c crashes, reconnect when
/// the socket drops, and graceful save-session shutdown on app exit.
/// </summary>
public sealed class Aria2Service
{
    public static Aria2Service Instance { get; } = new();

    private readonly Aria2ProcessManager _process = new();
    private readonly Aria2RpcClient _rpc = new();
    private readonly SemaphoreSlim _recoveryLock = new(1, 1);
    private volatile bool _shuttingDown;
    private int _restartAttempts;

    public Aria2RpcClient Rpc => _rpc;

    public AppSettings Settings { get; private set; } = new();

    public Aria2ServiceState State { get; private set; } = Aria2ServiceState.Stopped;

    public string? LastError { get; private set; }

    /// <summary>Forwarded aria2 push events: (method, gid). Raised on a worker thread.</summary>
    public event Action<string, string>? DownloadNotification;

    /// <summary>Raised on a worker thread whenever <see cref="State"/> changes.</summary>
    public event Action<Aria2ServiceState>? StateChanged;

    private Aria2Service()
    {
        _rpc.DownloadNotification += (method, gid) => DownloadNotification?.Invoke(method, gid);
        _rpc.ConnectionClosed += () => _ = RecoverAsync("RPC connection lost");
        _process.Exited += () => _ = RecoverAsync("aria2c exited unexpectedly");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        Settings = SettingsService.Load();
        SetState(Aria2ServiceState.Starting);
        // The recovery lock keeps RecoverAsync (fired by a crash mid-startup) from
        // racing this path with a concurrent process start.
        await _recoveryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartProcessAndConnectAsync(ct).ConfigureAwait(false);
            _restartAttempts = 0;
            SetState(Aria2ServiceState.Running);
        }
        catch (Exception ex)
        {
            LastError = BuildStartupError(ex);
            _process.Stop(); // never leave an unsupervised aria2c behind a Failed state
            SetState(Aria2ServiceState.Failed);
            throw;
        }
        finally
        {
            _recoveryLock.Release();
        }
    }

    /// <summary>Applies the runtime-changeable subset to aria2 live, then persists.
    /// Apply-first means a rejected value (or RPC failure) leaves the settings file
    /// untouched — the UI error message stays truthful.</summary>
    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (_rpc.IsConnected)
            await _rpc.ChangeGlobalOptionAsync(BuildRuntimeOptions(settings), ct).ConfigureAwait(false);
        Settings = settings;
        SettingsService.Save(settings);
    }

    /// <summary>
    /// Graceful engine restart for settings that only exist as command-line flags
    /// (BT port, DHT/PEX, extra options): saves the session, relaunches aria2c with
    /// the new flags and reconnects. Unfinished downloads resume from the session.
    /// </summary>
    public async Task RestartEngineAsync()
    {
        await _recoveryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            SetState(Aria2ServiceState.Restarting);
            try
            {
                if (_rpc.IsConnected)
                    await _rpc.ShutdownAsync(force: true).ConfigureAwait(false);
            }
            catch
            {
                // Best effort — WaitForExitOrKill below covers a wedged engine.
            }
            await Task.Run(() => _process.WaitForExitOrKill(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
            try
            {
                await StartProcessAndConnectAsync(CancellationToken.None).ConfigureAwait(false);
                _restartAttempts = 0;
                SetState(Aria2ServiceState.Running);
            }
            catch (Exception ex)
            {
                LastError = BuildStartupError(ex);
                _process.Stop();
                SetState(Aria2ServiceState.Failed);
                throw;
            }
        }
        finally
        {
            _recoveryLock.Release();
        }
    }

    /// <summary>
    /// Fast synchronous shutdown for app exit: persist the session explicitly
    /// (aria2.saveSession returns as soon as it's on disk), then kill the process
    /// right away instead of waiting out aria2's slow ~4s graceful exit. The Job
    /// Object is the final safety net. Total UI-thread block is well under a second.
    /// </summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        // Don't tear down mid-recovery: a concurrent restart could spawn a process
        // after we've killed the current one. Bounded wait keeps exit snappy.
        bool lockTaken = _recoveryLock.Wait(TimeSpan.FromSeconds(1));
        try
        {
            try
            {
                if (_rpc.IsConnected)
                {
                    // Session on disk first — then we don't need a graceful exit.
                    _rpc.SaveSessionAsync().Wait(TimeSpan.FromSeconds(1.5));
                    _ = _rpc.ShutdownAsync(force: true); // fire-and-forget; Kill backs it up
                }
            }
            catch
            {
                // Best effort — Stop() below force-kills regardless.
            }
            _process.Stop();
            _process.Dispose();
            _ = _rpc.DisposeAsync();
            SetState(Aria2ServiceState.Stopped);
        }
        finally
        {
            if (lockTaken)
                _recoveryLock.Release();
        }
    }

    private async Task StartProcessAndConnectAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Settings.DownloadDirectory);

        // Two attempts: a fresh port on the second try covers the (rare) case of
        // the free-port probe racing another process for the same port.
        for (int attempt = 1; ; attempt++)
        {
            _process.Start(Settings.DownloadDirectory, AppPaths.SessionFile, BuildStartupOptions(Settings));
            try
            {
                await ConnectWithRetryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch when (attempt == 1 && !ct.IsCancellationRequested)
            {
                _process.Stop();
            }
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        Exception? lastError = null;
        for (int i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!_process.IsRunning)
                throw new InvalidOperationException("aria2c terminated during startup. " + _process.StderrTail, lastError);
            try
            {
                await _rpc.ConnectAsync(_process.RpcPort, _process.Secret, ct).ConfigureAwait(false);
                await _rpc.GetVersionAsync(ct).ConfigureAwait(false); // validates port + secret
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
        throw new TimeoutException("aria2c did not accept the RPC connection in time.", lastError);
    }

    private async Task RecoverAsync(string reason)
    {
        if (_shuttingDown)
            return;
        await _recoveryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shuttingDown || State is Aria2ServiceState.Running && _rpc.IsConnected && _process.IsRunning)
                return; // a concurrent recovery already fixed things

            if (++_restartAttempts > 3)
            {
                LastError = $"{reason}; giving up after {_restartAttempts - 1} restart attempts. {_process.StderrTail}";
                SetState(Aria2ServiceState.Failed);
                return;
            }

            SetState(Aria2ServiceState.Restarting);
            try
            {
                if (_process.IsRunning && !_rpc.IsConnected)
                {
                    // Process alive, socket dropped — try a plain reconnect first,
                    // and fall back to a full restart if the process is unreachable.
                    try
                    {
                        await ConnectWithRetryAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        _process.Stop();
                        await StartProcessAndConnectAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                else
                {
                    _process.Stop();
                    await StartProcessAndConnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                _restartAttempts = 0;
                SetState(Aria2ServiceState.Running);
            }
            catch (Exception ex)
            {
                LastError = BuildStartupError(ex);
                _process.Stop(); // never leave an unsupervised aria2c behind a Failed state
                SetState(Aria2ServiceState.Failed);
            }
        }
        finally
        {
            _recoveryLock.Release();
        }
    }

    /// <summary>Options passed on the aria2c command line at startup.</summary>
    private static Dictionary<string, string> BuildStartupOptions(AppSettings s)
    {
        var options = new Dictionary<string, string>
        {
            ["max-overall-download-limit"] = s.MaxDownloadLimit,
            ["max-overall-upload-limit"] = s.MaxUploadLimit,
            ["max-concurrent-downloads"] = s.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture),
            ["max-connection-per-server"] = s.MaxConnectionsPerServer.ToString(CultureInfo.InvariantCulture),
            ["split"] = s.MaxConnectionsPerServer.ToString(CultureInfo.InvariantCulture),
            ["bt-max-peers"] = s.BtMaxPeers.ToString(CultureInfo.InvariantCulture),
            ["enable-dht"] = s.EnableDht ? "true" : "false",
            ["enable-peer-exchange"] = s.EnablePex ? "true" : "false",
            ["bt-enable-lpd"] = s.EnableLpd ? "true" : "false",
            ["bt-require-crypto"] = s.RequireCrypto ? "true" : "false",
        };
        if (s.ListenPort > 0)
        {
            options["listen-port"] = s.ListenPort.ToString(CultureInfo.InvariantCulture);
            options["dht-listen-port"] = s.ListenPort.ToString(CultureInfo.InvariantCulture);
        }
        string trackers = NormalizeTrackers(s.ExtraTrackers);
        if (trackers.Length > 0)
            options["bt-tracker"] = trackers;
        AddCommonOptions(options, s);
        AddSeedOptions(options, s);
        ApplyExtraOptions(options, s.ExtraAria2Options);
        return options;
    }

    /// <summary>Connection/file/BT options shared between startup and runtime.</summary>
    private static void AddCommonOptions(Dictionary<string, string> options, AppSettings s)
    {
        string inv(int v) => v.ToString(CultureInfo.InvariantCulture);
        options["bt-max-open-files"] = inv(s.BtMaxOpenFiles);
        options["timeout"] = inv(s.Timeout);
        options["connect-timeout"] = inv(s.ConnectTimeout);
        options["max-tries"] = inv(s.MaxTries);
        options["retry-wait"] = inv(s.RetryWait);
        options["check-certificate"] = s.CheckCertificate ? "true" : "false";
        options["min-split-size"] = s.MinSplitSize;
        options["allow-overwrite"] = s.AllowOverwrite ? "true" : "false";
        options["auto-file-renaming"] = s.AutoFileRenaming ? "true" : "false";
        if (!string.IsNullOrWhiteSpace(s.UserAgent))
            options["user-agent"] = s.UserAgent.Trim();
        if (!string.IsNullOrWhiteSpace(s.AllProxy))
            options["all-proxy"] = s.AllProxy.Trim();
        // "auto" is resolved per download dir in the process manager; pass concrete others.
        if (s.FileAllocation is not "auto")
            options["file-allocation"] = s.FileAllocation;
    }

    /// <summary>Lines/commas/spaces → aria2's comma-separated tracker list.</summary>
    private static string NormalizeTrackers(string raw) =>
        string.Join(',', raw.Split(['\r', '\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Free-form "key=value" lines giving access to any aria2 flag. Applied last so
    /// they override our defaults — except keys the app's own plumbing depends on.
    /// </summary>
    private static void ApplyExtraOptions(Dictionary<string, string> options, string raw)
    {
        // These keys are owned by the app: overriding them breaks RPC/session wiring.
        string[] blocked =
        [
            "enable-rpc", "rpc-listen-port", "rpc-secret", "rpc-listen-all",
            "stop-with-process", "save-session", "input-file", "no-conf", "conf-path", "dir",
        ];
        foreach (var rawLine in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string line = rawLine.StartsWith("--", StringComparison.Ordinal) ? rawLine[2..] : rawLine;
            if (line.StartsWith('#'))
                continue;
            int eq = line.IndexOf('=');
            string key = (eq < 0 ? line : line[..eq]).Trim().ToLowerInvariant();
            string value = eq < 0 ? "true" : line[(eq + 1)..].Trim();
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                continue;
            if (blocked.Contains(key))
                continue;
            options[key] = value;
        }
    }

    /// <summary>Subset of options aria2.changeGlobalOption accepts at runtime.</summary>
    private static Dictionary<string, string> BuildRuntimeOptions(AppSettings s)
    {
        var options = new Dictionary<string, string>
        {
            ["dir"] = s.DownloadDirectory,
            ["max-overall-download-limit"] = s.MaxDownloadLimit,
            ["max-overall-upload-limit"] = s.MaxUploadLimit,
            ["max-concurrent-downloads"] = s.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture),
            ["max-connection-per-server"] = s.MaxConnectionsPerServer.ToString(CultureInfo.InvariantCulture),
            ["split"] = s.MaxConnectionsPerServer.ToString(CultureInfo.InvariantCulture),
            ["bt-max-peers"] = s.BtMaxPeers.ToString(CultureInfo.InvariantCulture),
        };
        AddCommonOptions(options, s);
        AddSeedOptions(options, s);
        return options;
    }

    /// <summary>
    /// In aria2 --seed-ratio=0 means "seed forever". "Don't seed" is expressed with
    /// --seed-time=0 instead, so map our SeedRatio==0 to that. When a ratio is set,
    /// seed-time gets an effectively-infinite value so a previous runtime "0" cannot
    /// linger and cut seeding short (there is no "unset" in changeGlobalOption).
    /// </summary>
    private static void AddSeedOptions(Dictionary<string, string> options, AppSettings s)
    {
        if (s.SeedRatio <= 0)
        {
            options["seed-time"] = "0";
        }
        else
        {
            options["seed-ratio"] = s.SeedRatio.ToString("0.0##", CultureInfo.InvariantCulture);
            options["seed-time"] = "525600"; // a year, in minutes
        }
    }

    private string BuildStartupError(Exception ex)
    {
        string stderr = _process.StderrTail;
        return string.IsNullOrWhiteSpace(stderr) ? ex.Message : $"{ex.Message}\n{stderr}";
    }

    private void SetState(Aria2ServiceState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
