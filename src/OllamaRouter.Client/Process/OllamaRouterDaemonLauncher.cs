using System.Diagnostics;
using System.Net;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Security;

// The namespace OllamaRouter.Client.Process hides System.Diagnostics.Process inside this file.
using ProcessType = System.Diagnostics.Process;

namespace OllamaRouter.Client.Process;

/// <summary>
/// Default <see cref="IOllamaRouterDaemonLauncher"/> implementation (task #99): probes the
/// daemon port, auto-starts a DETACHED headless user process and gracefully stops it.
/// <para>
/// The client stays an AOT leaf library: only BCL APIs are used (<see cref="System.Net.Http"/>,
/// <see cref="System.Diagnostics.Process"/>) — no reflection, no new packages.
/// </para>
/// <para>
/// Probe classification: <c>200</c> + parseable router identity payload (or a 401 after the
/// client's own 401 re-read retry — in key-locked mode only OllamaRouter 401s the
/// <c>/api/*</c> surface) → <see cref="RouterDaemonState.Running"/>; any OTHER HTTP payload
/// (a raw Ollama daemon 404s the identity route; a 200 with a non-router body is unparseable)
/// → <see cref="RouterDaemonState.PortOccupiedByOtherService"/>; connection refused/reset or a
/// probe timeout → <see cref="RouterDaemonState.Stopped"/>.
/// </para>
/// </summary>
public sealed class OllamaRouterDaemonLauncher : IOllamaRouterDaemonLauncher
{
    /// <summary>The default daemon base URL (the conventional Ollama port, loopback).</summary>
    public static readonly Uri DefaultBaseUrl = new("http://127.0.0.1:11434/");

    /// <summary>Daemon executable names, in preference order (post-#100 AssemblyName first).</summary>
    private static readonly string[] ExecutableNames = ["ollamarouter-server", "OllamaRouter"];

    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    /// <summary>
    /// The <see cref="System.Diagnostics.Process"/> seam (task #99): <see cref="System.Diagnostics.Process"/> is
    /// sealed and unmockable, so the launcher drives every process interaction through this
    /// adapter. The private real-<see cref="System.Diagnostics.Process"/> adapter is the default behavior.
    /// </summary>
    internal interface RouterDaemonProcess
    {
        /// <summary>The operating-system process id.</summary>
        int Id { get; }

        /// <summary>Whether the process has exited (a query failure is treated as exited).</summary>
        bool HasExited { get; }

        /// <summary>Kills the process and its entire tree.</summary>
        void KillEntireProcessTree();

        /// <summary>Releases the underlying handle.</summary>
        void Dispose();
    }

    /// <summary>The real-<see cref="System.Diagnostics.Process"/> adapter (default behavior).</summary>
    private sealed class ProcessAdapter : RouterDaemonProcess
    {
        private readonly ProcessType _process;

        public ProcessAdapter(ProcessType process) => _process = process;

        public int Id => _process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (Exception)
                {
                    // A query failure (e.g. the process vanished on Unix) is treated as exited
                    // so the wait loop always terminates and the final probe decides the outcome.
                    return true;
                }
            }
        }

        public void KillEntireProcessTree() => _process.Kill(entireProcessTree: true);

        public void Dispose() => _process.Dispose();
    }

    private readonly IOllamaRouterApiClient _client;
    private readonly string? _installDirectory;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _startPollInterval;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _stopExitTimeout;

    private readonly object _gate = new();

    private RouterDaemonProcess? _trackedProcess;

    /// <summary>
    /// Test seam: replaces the real <c>Process.Start</c> + adapter. Tests NEVER start real
    /// child processes through this hook.
    /// </summary>
    internal Func<ProcessStartInfo, RouterDaemonProcess>? ProcessStarter { get; set; }

    /// <summary>
    /// Test seam: replaces the best-effort <c>Process.GetProcessById</c> lookup (null on
    /// failure). Default: real <see cref="System.Diagnostics.Process.GetProcessById(int)"/> (null on
    /// any lookup failure — no such process, or a vanished/foreign process).
    /// </summary>
    internal Func<int, RouterDaemonProcess?>? ProcessLookup { get; set; }

    /// <summary>
    /// Test guard: when <c>false</c> the environment-derived search roots (the
    /// <c>OLLAMAROUTER_HOME</c> env var and the platform default install directory) are skipped
    /// so a real OllamaRouter installed on a developer/CI machine can never leak into
    /// resolution tests. Defaults to <c>true</c>.
    /// </summary>
    internal bool UseDefaultSearchRoots { get; set; } = true;

    /// <summary>
    /// Creates the launcher. The client is built from <paramref name="baseUrl"/> (default
    /// <c>http://127.0.0.1:11434/</c>), <paramref name="apiKey"/>, <paramref name="keyReader"/>
    /// and <paramref name="httpClient"/> (the standard client auth/timeout semantics apply).
    /// All timeout overrides must be positive (defaults: probe 2 s, start poll 250 ms, start
    /// timeout 5 s, stop-exit timeout 10 s).
    /// </summary>
    public OllamaRouterDaemonLauncher(
        Uri? baseUrl = null,
        string? apiKey = null,
        IDefaultKeyReader? keyReader = null,
        HttpClient? httpClient = null,
        string? installDirectory = null,
        TimeSpan? probeTimeout = null,
        TimeSpan? startPollInterval = null,
        TimeSpan? startTimeout = null,
        TimeSpan? stopExitTimeout = null)
        : this(
            new OllamaRouterApiClient(baseUrl ?? DefaultBaseUrl, apiKey, keyReader, httpClient),
            installDirectory,
            probeTimeout,
            startPollInterval,
            startTimeout,
            stopExitTimeout)
    {
    }

    /// <summary>
    /// Test-friendly constructor over a prebuilt <see cref="IOllamaRouterApiClient"/> (same
    /// timeout validation and defaults as the public constructor).
    /// </summary>
    internal OllamaRouterDaemonLauncher(
        IOllamaRouterApiClient client,
        string? installDirectory = null,
        TimeSpan? probeTimeout = null,
        TimeSpan? startPollInterval = null,
        TimeSpan? startTimeout = null,
        TimeSpan? stopExitTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _installDirectory = string.IsNullOrWhiteSpace(installDirectory) ? null : installDirectory;
        _probeTimeout = RequirePositive(probeTimeout ?? TimeSpan.FromSeconds(2), "probeTimeout");
        _startPollInterval = RequirePositive(startPollInterval ?? TimeSpan.FromMilliseconds(250), "startPollInterval");
        _startTimeout = RequirePositive(startTimeout ?? TimeSpan.FromSeconds(5), "startTimeout");
        _stopExitTimeout = RequirePositive(stopExitTimeout ?? TimeSpan.FromSeconds(10), "stopExitTimeout");
    }

    private static TimeSpan RequirePositive(TimeSpan value, string name) =>
        value <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(name, value, "The value must be positive.")
            : value;

    // ------------------------------------------------------------------
    //  Probe
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<RouterDaemonStatus> GetStatusAsync(CancellationToken ct = default)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(_probeTimeout);

        try
        {
            var info = await _client.GetInfoAsync(probeCts.Token).ConfigureAwait(false);
            return new RouterDaemonStatus(RouterDaemonState.Running, info, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Probe timeout (the probe CTS fired, the caller's token did not).
            return new RouterDaemonStatus(RouterDaemonState.Stopped, null, "probe timed out");
        }
        catch (OllamaRouterApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            // Only OUR router 401s the /api/* surface (key-locked mode); a raw Ollama daemon
            // 404s. Treated as running so auto-start does not fork a second daemon.
            return new RouterDaemonStatus(
                RouterDaemonState.Running, null, "key required; authenticated probe not possible");
        }
        catch (OllamaRouterApiException ex)
        {
            return new RouterDaemonStatus(
                RouterDaemonState.PortOccupiedByOtherService, null,
                $"HTTP {(int)ex.Status} — a non-router service occupies the port (e.g. a raw Ollama daemon)");
        }
        catch (System.Text.Json.JsonException)
        {
            // 200 with a non-router (unparseable) payload.
            return new RouterDaemonStatus(
                RouterDaemonState.PortOccupiedByOtherService, null,
                "non-router payload on the identity route — a non-router service occupies the port");
        }
        catch (HttpRequestException)
        {
            return new RouterDaemonStatus(RouterDaemonState.Stopped, null, "connection refused");
        }
    }

    // ------------------------------------------------------------------
    //  Start
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<RouterDaemonStatus> StartAsync(CancellationToken ct = default) =>
        StartCoreAsync(ct, throwWhenOccupied: true);

    /// <inheritdoc />
    public Task<RouterDaemonStatus> EnsureRunningAsync(CancellationToken ct = default) =>
        StartCoreAsync(ct, throwWhenOccupied: false);

    private async Task<RouterDaemonStatus> StartCoreAsync(CancellationToken ct, bool throwWhenOccupied)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status.State == RouterDaemonState.Running)
        {
            return status; // idempotent: never launch a second daemon
        }

        if (status.State == RouterDaemonState.PortOccupiedByOtherService)
        {
            if (throwWhenOccupied)
            {
                throw new RouterDaemonPortOccupiedException(status);
            }

            return status; // auto-start semantics: surface, never throw
        }

        var executablePath = ResolveExecutable(out var searchedLocations)
            ?? throw new RouterDaemonExecutableNotFoundException(searchedLocations);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var starter = ProcessStarter ?? DefaultProcessStarter;
        var process = starter(startInfo);
        var succeeded = false;
        try
        {
            var deadline = Environment.TickCount64 + (long)_startTimeout.TotalMilliseconds;
            while (true)
            {
                var probe = await GetStatusAsync(ct).ConfigureAwait(false);
                if (probe.State == RouterDaemonState.Running)
                {
                    lock (_gate)
                    {
                        _trackedProcess = process;
                    }

                    succeeded = true; // the tracked handle stays alive for StopAsync
                    return probe;
                }

                if (probe.State == RouterDaemonState.PortOccupiedByOtherService)
                {
                    // The child lost the bind race (a foreign owner already serves the port).
                    throw new RouterDaemonPortOccupiedException(probe);
                }

                if (Environment.TickCount64 >= deadline)
                {
                    throw new RouterDaemonStartTimeoutException(_startTimeout, executablePath);
                }

                await Task.Delay(_startPollInterval, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Any failure (occupied / timeout / cancellation) cleans up the child we started
            // exactly once; a successful start retains the handle for StopAsync.
            if (!succeeded)
            {
                KillAndDispose(process);
            }
        }
    }

    private static void KillAndDispose(RouterDaemonProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.KillEntireProcessTree();
            }
        }
        catch (Exception)
        {
            // Best-effort child cleanup on the failure paths.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>The default starter: real <see cref="System.Diagnostics.Process.Start(ProcessStartInfo)"/> + drained pipes.</summary>
    private static RouterDaemonProcess DefaultProcessStarter(ProcessStartInfo startInfo)
    {
        var process = new ProcessType { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new RouterDaemonException($"Failed to start the daemon executable '{startInfo.FileName}'.");
        }

        // Redirected-but-discarded streams: a fully detached child with NO console window,
        // NO shell and NO pipe-backpressure deadlock (the daemon writes its own Serilog file
        // logs; console output is expendable). The drain task keeps consuming the pipe buffers
        // until the child exits (equivalent to Stream.DiscardBuffer, which is not exposed in
        // the .NET 10 BCL).
        _ = Task.Run(() =>
        {
            try
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
            }
            catch (Exception)
            {
                // Best-effort drain: the process handle can be released while draining
                // (failure-path kill + dispose), which disposes the redirected pipes.
            }
        });
        return new ProcessAdapter(process);
    }

    // ------------------------------------------------------------------
    //  Executable resolution
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves the daemon executable: the first hit of the NAME × ROOT matrix (names in
    /// preference order; file extension <c>.exe</c> on Windows, bare on Unix). Returns null
    /// (with the FULL probed path list) when nothing is found.
    /// </summary>
    private string? ResolveExecutable(out List<string> searchedLocations)
    {
        var suffix = IsWindows ? ".exe" : string.Empty;
        var roots = new List<string>();
        if (_installDirectory is not null)
        {
            roots.Add(_installDirectory);
        }

        if (UseDefaultSearchRoots)
        {
            var home = Environment.GetEnvironmentVariable("OLLAMAROUTER_HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                roots.Add(home);
            }

            // The sibling directory of the calling executable (CLI/App installed next to the
            // server) is probed BEFORE the platform default install dir (the approved
            // install-dir → OLLAMAROUTER_HOME → sibling → platform-default order): with the
            // test guard off it is skipped too, so a test host that carries a real router
            // apphost next to its DLLs (the referenced server project's apphost) can never
            // leak in.
            roots.Add(AppContext.BaseDirectory);

            roots.Add(IsWindows
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OllamaRouter")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollamarouter"));
        }

        var searched = new List<string>(ExecutableNames.Length * roots.Count);
        foreach (var name in ExecutableNames)
        {
            foreach (var root in roots)
            {
                var candidate = Path.Combine(root, name + suffix);
                searched.Add(candidate);
                if (File.Exists(candidate))
                {
                    searchedLocations = searched;
                    return candidate;
                }
            }
        }

        searchedLocations = searched;
        return null;
    }

    // ------------------------------------------------------------------
    //  Stop
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<RouterDaemonStatus> StopAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status.State == RouterDaemonState.Stopped)
        {
            return status; // idempotent no-op
        }

        if (status.State == RouterDaemonState.PortOccupiedByOtherService)
        {
            throw new RouterDaemonPortOccupiedException(status); // never kill a process we don't own
        }

        // Graceful shutdown — BEST-EFFORT: a 401 (router key-locked, no readable key) or a
        // dying server (connection reset) falls through to the wait/kill fallback so
        // `ollamarouter stop` works without a key.
        try
        {
            await _client.ShutdownAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException)
        {
            // Expected in keyless key-locked operation — fall through.
        }
        catch (HttpRequestException)
        {
            // The server is already dying — fall through.
        }

        var process = ResolveProcessHandle(status);
        if (process is not null)
        {
            var deadline = Environment.TickCount64 + (long)_stopExitTimeout.TotalMilliseconds;
            while (!process.HasExited && Environment.TickCount64 < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }

            if (!process.HasExited)
            {
                try
                {
                    process.KillEntireProcessTree();
                }
                catch (Exception)
                {
                    if (!process.HasExited)
                    {
                        throw new RouterDaemonStopFailedException(process.Id);
                    }
                }
            }

            lock (_gate)
            {
                if (ReferenceEquals(_trackedProcess, process))
                {
                    _trackedProcess = null;
                }
            }

            process.Dispose();
        }

        return await GetStatusAsync(ct).ConfigureAwait(false);
    }

    private RouterDaemonProcess? ResolveProcessHandle(RouterDaemonStatus status)
    {
        lock (_gate)
        {
            // The tracked handle is authoritative when this launcher started the daemon.
            if (_trackedProcess is { } tracked)
            {
                return tracked;
            }
        }

        var processId = status.Info?.ProcessId;
        if (processId is not { } pid || pid <= 0)
        {
            return null; // 401 probe (no telemetry) or a pid-less payload: cannot track —
                         // the final probe still decides the outcome.
        }

        var lookup = ProcessLookup ?? DefaultProcessLookup;
        return lookup(pid);
    }

    private static RouterDaemonProcess? DefaultProcessLookup(int processId)
    {
        try
        {
            return new ProcessAdapter(ProcessType.GetProcessById(processId));
        }
        catch (Exception)
        {
            // Best-effort lookup: no such process (ArgumentException) or a vanished/foreign
            // process (any other OS-level failure) → not trackable. Note: .NET 10 removed
            // the legacy ProcessException type, so the lookup seam swallows the family.
            return null;
        }
    }
}
