using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Aria2Gui.Services.Aria2;

/// <summary>
/// Owns the bundled aria2c.exe child process: locates the binary, starts it with
/// RPC enabled on a random loopback port with a random secret, and guarantees the
/// child dies with the GUI (Job Object kill-on-close + aria2 --stop-with-process).
/// </summary>
public sealed class Aria2ProcessManager : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private nint _jobHandle;
    private StringBuilder? _stderrTail;
    private bool _disposed;

    public int RpcPort { get; private set; }

    public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public bool IsRunning
    {
        get
        {
            var process = _process;
            try
            {
                return process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Raised from a worker thread when aria2c exits (including crashes).</summary>
    public event Action? Exited;

    /// <summary>Last lines of aria2c stderr — surfaced in startup failure messages.</summary>
    public string StderrTail
    {
        get { lock (_gate) { return _stderrTail?.ToString() ?? ""; } }
    }

    public static string? FindExecutable()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "Aria2", "aria2c.exe"),
            Path.Combine(baseDir, "aria2c.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Starts aria2c. Throws <see cref="FileNotFoundException"/> if the binary is missing.</summary>
    public void Start(string downloadDirectory, string sessionFile, IReadOnlyDictionary<string, string>? extraOptions = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string exe = FindExecutable()
            ?? throw new FileNotFoundException("aria2c.exe not found next to the application.");

        Stop();

        RpcPort = GetFreeTcpPort();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("--enable-rpc");
        psi.ArgumentList.Add("--rpc-listen-all=false");
        psi.ArgumentList.Add($"--rpc-listen-port={RpcPort}");
        psi.ArgumentList.Add($"--rpc-secret={Secret}");
        psi.ArgumentList.Add("--no-conf");
        psi.ArgumentList.Add("--quiet");
        psi.ArgumentList.Add($"--dir={downloadDirectory}");
        psi.ArgumentList.Add("--continue=true");
        psi.ArgumentList.Add("--file-allocation=falloc");
        psi.ArgumentList.Add("--auto-save-interval=20");
        psi.ArgumentList.Add($"--save-session={sessionFile}");
        psi.ArgumentList.Add("--save-session-interval=30");
        psi.ArgumentList.Add("--bt-save-metadata=true");
        psi.ArgumentList.Add("--rpc-save-upload-metadata=true");
        psi.ArgumentList.Add("--follow-torrent=true");
        // Second safety net besides the Job Object: aria2 polls this PID and exits when it dies.
        psi.ArgumentList.Add($"--stop-with-process={Environment.ProcessId}");
        if (File.Exists(sessionFile))
            psi.ArgumentList.Add($"--input-file={sessionFile}");
        if (extraOptions is not null)
        {
            foreach (var (key, value) in extraOptions)
                psi.ArgumentList.Add($"--{key}={value}");
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start aria2c.exe.");

        lock (_gate)
        {
            _stderrTail = new StringBuilder();
        }
        process.ErrorDataReceived += (_, e) => AppendStderr(e.Data);
        process.OutputDataReceived += (_, _) => { }; // drain so the pipe never blocks aria2c
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Exited?.Invoke();

        AssignToKillOnCloseJob(process);
        _process = process;
    }

    /// <summary>
    /// Waits for a graceful exit (caller is expected to have sent aria2.shutdown first),
    /// then force-kills if aria2c is still alive.
    /// </summary>
    public void WaitForExitOrKill(TimeSpan timeout)
    {
        var process = _process;
        if (process is null)
            return;
        try
        {
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited and released.
        }
        finally
        {
            process.Dispose();
            _process = null;
        }
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void AppendStderr(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        lock (_gate)
        {
            var tail = _stderrTail ??= new StringBuilder();
            if (tail.Length > 4096)
                tail.Remove(0, tail.Length - 2048);
            tail.AppendLine(line);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void AssignToKillOnCloseJob(Process process)
    {
        if (_jobHandle == 0)
        {
            nint job = NativeMethods.CreateJobObjectW(0, null);
            if (job == 0)
                return; // job objects are best-effort; --stop-with-process still covers us

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = { LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
            };
            if (!NativeMethods.SetInformationJobObject(
                    job,
                    NativeMethods.JobObjectExtendedLimitInformation,
                    ref info,
                    (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                NativeMethods.CloseHandle(job);
                return;
            }
            _jobHandle = job;
        }

        NativeMethods.AssignProcessToJobObject(_jobHandle, process.Handle);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        if (_jobHandle != 0)
        {
            NativeMethods.CloseHandle(_jobHandle);
            _jobHandle = 0;
        }
    }

    private static class NativeMethods
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        public const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(nint hJob, int jobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(nint hObject);
    }
}
