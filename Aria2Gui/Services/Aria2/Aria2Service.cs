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
    // Serializes the Settings swap + settings.json write only. Deliberately NOT _recoveryLock:
    // a restart/recovery holds that for tens of seconds (kill-wait + spawn + connect retries),
    // and a settings blur must not stall behind it just to persist (N18).
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private volatile bool _shuttingDown;
    private int _restartAttempts;
    /// <summary>When the last crash recovery started — lets RecoverAsync distinguish a burst of
    /// failures (counts toward the give-up cap) from a crash after hours of stability (fresh cap).</summary>
    private DateTime _lastRecoveryUtc = DateTime.MinValue;

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

    /// <summary>Persists the settings, then pushes the runtime-changeable subset to aria2 live.
    /// Persist-first so a transient RPC failure (or one unrelated to the field the user just
    /// changed, e.g. the UI language) can never drop the choice; out-of-range runtime values are
    /// clamped by <see cref="SettingsService.Load"/> on the next read. The RPC error still
    /// propagates so the caller can surface it.</summary>
    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        // B10: serialize the Settings swap + persist against concurrent appliers. The restart
        // path snapshots Settings into a local before use, so a dedicated lightweight lock is
        // enough — waiting on the recovery lock here would stall every settings blur for the
        // full duration of an engine restart (N18).
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Settings = settings;
            SettingsService.Save(settings);
        }
        finally
        {
            _settingsLock.Release();
        }
        WatchFolderService.Instance.Reconfigure(settings);
        if (_rpc.IsConnected)
            await _rpc.ChangeGlobalOptionAsync(BuildRuntimeOptions(settings), ct).ConfigureAwait(false);
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
        // B9: a restart/recovery in flight when the app is exiting must not respawn aria2c after
        // Shutdown() has killed it. This is the single choke point for every spawn path.
        if (_shuttingDown)
            throw new OperationCanceledException("The engine is shutting down.");

        // Snapshot the settings reference once so a concurrent ApplySettingsAsync swap can't
        // give us a dir from one object and options from another.
        var settings = Settings;
        string dir = settings.DownloadDirectory;
        var startupOptions = BuildStartupOptions(settings);

        // B14: bounded retries with a fresh port each time cover the (rare) case of the free-port
        // probe racing another process for the same port; the last attempt's failure propagates.
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // O3: the directory creation + process spawn (DriveInfo/CreateDirectory/ADS probe)
            // hit the disk synchronously — keep them off the calling (UI) thread.
            await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                _process.Start(dir, AppPaths.SessionFile, startupOptions);
            }, ct).ConfigureAwait(false);
            try
            {
                await ConnectWithRetryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch when (attempt < maxAttempts && !ct.IsCancellationRequested)
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

            // N7: count attempts within a failure BURST. Resetting on every successful connect
            // made the cap unreachable for an engine that crashes shortly AFTER connecting
            // (restart-loop forever); a crash after a long stable run still gets a fresh cap.
            if (DateTime.UtcNow - _lastRecoveryUtc > TimeSpan.FromMinutes(2))
                _restartAttempts = 0;
            _lastRecoveryUtc = DateTime.UtcNow;

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
                // No counter reset here: a quick re-crash must keep accumulating toward the cap;
                // the time window above grants a fresh cap only after sustained stability.
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

    // Turtle mode: while AltSpeedEnabled, the alternative limits replace the main ones
    // everywhere the engine sees a limit (startup command line AND live changeGlobalOption).
    private static string EffectiveDownLimit(AppSettings s) => s.AltSpeedEnabled ? s.AltDownloadLimit : s.MaxDownloadLimit;
    private static string EffectiveUpLimit(AppSettings s) => s.AltSpeedEnabled ? s.AltUploadLimit : s.MaxUploadLimit;

    /// <summary>Options passed on the aria2c command line at startup.</summary>
    private static Dictionary<string, string> BuildStartupOptions(AppSettings s)
    {
        var options = new Dictionary<string, string>
        {
            ["max-overall-download-limit"] = EffectiveDownLimit(s),
            ["max-overall-upload-limit"] = EffectiveUpLimit(s),
            ["max-concurrent-downloads"] = s.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture),
            ["max-connection-per-server"] = s.MaxConnectionsPerServer.ToString(CultureInfo.InvariantCulture),
            ["split"] = s.MaxConnectionsPerServer.ToString(CultureInfo.InvariantCulture),
            ["bt-max-peers"] = s.BtMaxPeers.ToString(CultureInfo.InvariantCulture),
            ["enable-dht"] = s.EnableDht ? "true" : "false",
            ["enable-peer-exchange"] = s.EnablePex ? "true" : "false",
            ["bt-enable-lpd"] = s.EnableLpd ? "true" : "false",
            ["bt-require-crypto"] = s.RequireCrypto ? "true" : "false",
            ["bt-min-crypto-level"] = s.BtMinCryptoLevel,
            ["bt-force-encryption"] = s.BtForceEncryption ? "true" : "false",
        };
        if (s.ListenPort > 0)
        {
            options["listen-port"] = s.ListenPort.ToString(CultureInfo.InvariantCulture);
            options["dht-listen-port"] = s.ListenPort.ToString(CultureInfo.InvariantCulture);
        }
        string trackers = NormalizeTrackers(s.ExtraTrackers);
        if (trackers.Length > 0)
            options["bt-tracker"] = trackers;
        // Startup-only — changeGlobalOption rejects these, so they apply on the engine restart.
        options["disk-cache"] = s.DiskCache;
        options["min-tls-version"] = s.MinTlsVersion;
        options["disable-ipv6"] = s.DisableIpv6 ? "true" : "false";
        AddCommonOptions(options, s);
        AddSeedOptions(options, s);
        ApplyExtraOptions(options, s.ExtraAria2Options, s.PrivacyMode);
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
        options["lowest-speed-limit"] = s.LowestSpeedLimit;
        options["bt-request-peer-speed-limit"] = s.BtRequestPeerSpeedLimit;
        options["bt-detach-seed-only"] = s.BtDetachSeedOnly ? "true" : "false";
        options["bt-stop-timeout"] = inv(s.BtStopTimeout);
        if (!string.IsNullOrWhiteSpace(s.UserAgent))
            options["user-agent"] = s.UserAgent.Trim();
        if (!string.IsNullOrWhiteSpace(s.AllProxy))
            options["all-proxy"] = s.AllProxy.Trim();
        if (!string.IsNullOrWhiteSpace(s.HttpUser))
            options["http-user"] = s.HttpUser.Trim();
        if (!string.IsNullOrWhiteSpace(s.HttpPasswd))
            options["http-passwd"] = s.HttpPasswd;
        if (!string.IsNullOrWhiteSpace(s.AllProxyUser))
            options["all-proxy-user"] = s.AllProxyUser.Trim();
        if (!string.IsNullOrWhiteSpace(s.AllProxyPasswd))
            options["all-proxy-passwd"] = s.AllProxyPasswd;
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
    private static void ApplyExtraOptions(Dictionary<string, string> options, string raw, bool privacyOn)
    {
        // These keys are owned by the app: overriding them breaks RPC/session wiring.
        string[] blocked =
        [
            "enable-rpc", "rpc-listen-port", "rpc-secret", "rpc-listen-all",
            "stop-with-process", "save-session", "input-file", "no-conf", "conf-path", "dir",
            // force-save keeps completed downloads in the session; overriding it would bring
            // back the "list empties once everything finished seeding" data loss.
            "force-save",
        ];
        // B11: while privacy mode is on, an extra line like "enable-dht=true" must not silently
        // undo the privacy preset. These keys are off-limits until privacy mode is turned off.
        string[] privacyKeys =
        [
            "enable-dht", "enable-peer-exchange", "bt-enable-lpd",
            "bt-require-crypto", "bt-min-crypto-level", "bt-force-encryption",
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
            if (privacyOn && privacyKeys.Contains(key))
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
            ["max-overall-download-limit"] = EffectiveDownLimit(s),
            ["max-overall-upload-limit"] = EffectiveUpLimit(s),
            ["max-concurrent-downloads"] = s.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture),
            ["max-connection-per-server"] = s.MaxConnectionsPerServer.ToString(CultureInfo.InvariantCulture),
            ["split"] = s.MaxConnectionsPerServer.ToString(CultureInfo.InvariantCulture),
            ["bt-max-peers"] = s.BtMaxPeers.ToString(CultureInfo.InvariantCulture),
        };
        AddCommonOptions(options, s);
        AddSeedOptions(options, s);
        // AddCommonOptions omits these when blank — right for the command line, but
        // changeGlobalOption can't unset an omitted key, so CLEARING the proxy/credentials/
        // user-agent would otherwise never reach the running engine (N6). An empty value is
        // aria2's documented way to clear these at runtime.
        foreach (var key in new[] { "user-agent", "all-proxy", "http-user", "http-passwd", "all-proxy-user", "all-proxy-passwd" })
            options.TryAdd(key, "");
        return options;
    }

    /// <summary>
    /// Maps the seeding mode to aria2's seed-ratio/seed-time pair (aria2 stops on whichever is
    /// reached first). "ratio": cap by share ratio, with an effectively-infinite time so it can't
    /// cut seeding short. "time": cap by minutes, with seed-ratio=0 (aria2's "ignore ratio").
    /// "off": don't seed, via seed-time=0 (since seed-ratio=0 alone means "seed forever"). Both
    /// keys are always written so a previous runtime value can't linger (changeGlobalOption has no
    /// way to unset an option).
    /// </summary>
    private static void AddSeedOptions(Dictionary<string, string> options, AppSettings s)
    {
        switch (s.SeedMode)
        {
            case "time":
                options["seed-ratio"] = "0"; // ignore ratio — time is the limiter
                options["seed-time"] = s.SeedTimeMinutes.ToString(CultureInfo.InvariantCulture);
                break;
            case "off":
                options["seed-ratio"] = "0";
                options["seed-time"] = "0"; // stop seeding the moment the download completes
                break;
            default: // "ratio"
                options["seed-ratio"] = s.SeedRatio.ToString("0.0##", CultureInfo.InvariantCulture);
                options["seed-time"] = "525600"; // a year in minutes, so the ratio cuts off first
                break;
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
