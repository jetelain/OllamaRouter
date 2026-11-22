using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OllamaRouter.App.Connection;

namespace OllamaRouter.App;

/// <summary>
/// The result of a local-Ollama detection pass (task #103, spec §3.4): whether an Ollama
/// binary was found, where, and whether an Ollama process is currently running.
/// </summary>
internal sealed record OllamaDetection(bool Installed, string? BinaryPath, bool ProcessRunning);

/// <summary>
/// The outcome of a one-click local-Ollama-for-the-router configuration (task #103, spec
/// §3.4): the success flag, a per-stage human message (naming the stage that failed on
/// partial failure) and which of the two services actually came up.
/// </summary>
internal sealed record LocalOllamaConfigResult(bool Success, string Message, bool OllamaStarted, bool RouterStarted);

/// <summary>
/// The outcome of running the official Ollama install/update script (task #150): the success
/// flag, a human-readable message (naming the exit code / output tail on failure) and whether
/// Ollama was confirmed restarted (the update path only — the install path leaves the
/// re-detection to the caller, one owner, like every other Step 1 action).
/// </summary>
internal sealed record OllamaScriptResult(bool Success, string Message, bool OllamaStarted);

/// <summary>
/// The LAN-exposure state of the local Ollama as surfaced by Step 1 (task #160):
/// <see cref="Lan"/> = OLLAMA_HOST binds all interfaces (<c>0.0.0.0</c> — usable from other
/// computers on the network); <see cref="Loopback"/> = bound to <c>127.0.0.1</c> only (Ollama's
/// default); <see cref="Unknown"/> = undeterminable (the UI shows the Re-check hint).
/// </summary>
public enum OllamaLanState
{
    Unknown,
    Loopback,
    Lan,
}

/// <summary>
/// The outcome of an effective-Ollama-port check (task #150; extended by task #160): whether
/// Ollama is running, the port it is serving on (null = running but no candidate port opened)
/// and the detected LAN-exposure state — computed INSIDE this same detection pass so a Step 1
/// refresh stays exactly one binary detection (the task #147 invariant).
/// </summary>
internal sealed record OllamaPortCheck(bool OllamaRunning, int? DetectedPort, OllamaLanState LanState);

/// <summary>
/// The captured outcome of a script process run (task #150): the exit code plus the last
/// ~500 characters (trimmed) of each stream — enough for a readable failure message without
/// keeping the whole installer transcript.
/// </summary>
internal sealed record ProcessRunResult(int ExitCode, string OutputTail, string ErrorTail);

/// <summary>
/// The Step-1 engine of the Setup Wizard (task #103, spec §3.4): detects a local Ollama,
/// writes the OLLAMA_HOST / OLLAMA_FLASH_ATTENTION / OLLAMA_KV_CACHE_TYPE environment,
/// restarts Ollama on <c>:11435</c> (explicit env — no stale inheritance), waits for the port
/// and (for the full configure path) starts the router daemon on <c>:11434</c>.
/// <para>
/// Launch contract per OS (task #157 — parity with the daemon-side OllamaProcessLauncher):
/// Windows → the TRAY APP (<c>ollama app.exe --fast-startup hidden</c>, explicit env — the
/// spawned serve child binds <c>:11435</c>); macOS → <c>open -g --env … -a Ollama.app</c>;
/// Linux (Update restart) → <c>&lt;binary&gt; serve</c> with the explicit env. Tray app absent on
/// Windows → readable failure (no headless serve fallback — fail fast, no port wait).
/// </para>
/// <para>
/// BCL-only; EVERY side effect is behind an injectable seam so tests are platform-independent
/// and spawn nothing (no real processes, env writes, network or sleeps — the port-wait delay
/// is a manual gate in tests).
/// </para>
/// </summary>
internal sealed class LocalOllamaConfigurator
{
    internal const int OllamaPort = 11435;
    internal const int RouterPort = 11434;
    internal const string HostEnvVar = "OLLAMA_HOST";
    internal const string FlashAttentionEnvVar = "OLLAMA_FLASH_ATTENTION";
    internal const string KvCacheTypeEnvVar = "OLLAMA_KV_CACHE_TYPE";

    /// <summary>The macOS Ollama.app bundle name (for <c>open -a</c>).</summary>
    internal const string OllamaAppBundle = "/Applications/Ollama.app";

    /// <summary>The Windows Ollama tray-app executable (official installer location; same
    /// convention as the daemon-side OllamaProcessLauncher auto-start).</summary>
    internal static readonly string WindowsTrayAppPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Ollama", "ollama app.exe");

    /// <summary>
    /// The readable "Ollama not found" failure message (task #157): on Windows it names the
    /// tray-app location searched + the recovery (no headless serve fallback); macOS/Linux keep
    /// the existing not-installed wording (byte-identical to pre-#157).
    /// </summary>
    internal static string OllamaNotFoundMessage(bool windows, bool afterUpdate) => windows
        ? $"The Ollama tray app was not found at '{WindowsTrayAppPath}'"
          + (afterUpdate ? " after the update script" : string.Empty)
          + " — start Ollama manually or reinstall it from ollama.com."
        : afterUpdate
            ? "No local Ollama installation was found after the update script — check the installer output."
            : "No local Ollama installation was found — install Ollama and retry.";

    // -- Injectable seams (all default to real BCL behavior; tests replace them) ------

    /// <summary>Windows platform probe (default <see cref="OperatingSystem.IsWindows"/>; the
    /// string argument is an opaque probe context — the candidate binary path).</summary>
    internal Func<string, bool>? IsWindows { get; set; }

    /// <summary>macOS platform probe (default <see cref="OperatingSystem.IsMacOS"/>).</summary>
    internal Func<string, bool>? IsMacOS { get; set; }

    /// <summary>Binary detection over a candidate-path list (installed / path / running).</summary>
    internal Func<IReadOnlyList<string>, OllamaDetection>? BinaryProbe { get; set; }

    /// <summary>Environment writer: (name, value, target) — the Windows User-target path.</summary>
    internal Action<string, string, EnvironmentVariableTarget>? EnvWriter { get; set; }

    /// <summary>Process-name query: name → the running pids (as strings) for that exact name.</summary>
    internal Func<string, IReadOnlyList<string>>? ProcessNames { get; set; }

    /// <summary>Process killer: (name, pid) → whether it was killed.</summary>
    internal Func<string, int, bool>? ProcessKiller { get; set; }

    /// <summary>Process starter: launch (tray app / serve / open) → the child handle id
    /// (captured <see cref="ProcessStartInfo"/> is what tests inspect).</summary>
    internal Func<ProcessStartInfo, string>? ProcessStarter { get; set; }

    /// <summary>Port probe: (port) → whether a TCP connect to <c>127.0.0.1:port</c> opens.</summary>
    internal Func<int, bool>? PortProbe { get; set; }

    /// <summary>Port-wait delay (tests: a manual gate — never a real sleep in tests).</summary>
    internal Func<CancellationToken, Task>? Delay { get; set; }

    /// <summary>The port-wait time budget (default 10 s; tests shorten it for the timeout case).</summary>
    internal TimeSpan PortWaitBudget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs a process to completion and captures the exit code + stdout/stderr tails (task
    /// #150: the official install/update scripts). Default: <c>Process.Start</c> +
    /// <c>WaitForExitAsync</c>; redirected streams are read to end and trimmed to the last
    /// ~500 characters each (the tails stay empty for <c>UseShellExecute</c> launches — the
    /// Windows UAC case, where redirection is impossible).
    /// </summary>
    internal Func<ProcessStartInfo, CancellationToken, Task<ProcessRunResult>>? ScriptRunner { get; set; }

    /// <summary>
    /// Reads the persisted OLLAMA_HOST value (task #150): Windows reads the User environment;
    /// macOS/Linux return null — no reliable GUI-readable persisted value (the candidate
    /// port-probe fallback covers them).
    /// </summary>
    internal Func<string?>? OllamaHostReader { get; set; }

    /// <summary>
    /// Non-loopback IPv4 addresses of this machine (task #160): empty when undeterminable
    /// (e.g. an offline machine) — the detection then falls through to the persisted-env
    /// rule instead of probing.
    /// </summary>
    internal Func<IReadOnlyList<IPAddress>>? NonLoopbackLocalAddresses { get; set; }

    /// <summary>
    /// LAN bind connect probe (task #160): (ip, port) → <c>true</c> = a TCP connect to the
    /// given address:port SUCCEEDED (Ollama is bound to all interfaces — LAN), <c>false</c> =
    /// the connect was REFUSED or did not complete within the probe window (loopback-only),
    /// <c>null</c> = probe ERROR (a socket exception — the caller falls through to the
    /// persisted-env rule).
    /// </summary>
    internal Func<string, int, bool?>? LanBindProbe { get; set; }

    /// <summary>
    /// macOS <c>launchctl getenv &lt;name&gt;</c> output capture (task #160): name → the trimmed
    /// stdout value, or null when unset/empty/failed. <see cref="ProcessStarter"/> returns a
    /// pid only, so this is the new output-capturing seam (the DefaultScriptRunner pattern).
    /// Invoked on macOS only (the persisted-env rule's platform branch).
    /// </summary>
    internal Func<string, string?>? LaunchctlGetenv { get; set; }

    /// <summary>
    /// Candidate ports probed (in order, 11435 first) when the persisted-env read yields
    /// nothing confirmed (task #150). Documented heuristic: the FIRST open candidate is
    /// assumed to be Ollama's; a non-Ollama listener on 11435 would mask a drifted Ollama
    /// port (rare, accepted).
    /// </summary>
    internal IReadOnlyList<int> OllamaPortCandidates { get; set; } = [11435, 11434, 11436, 11437, 11438, 11439, 11440];

    private static readonly Func<string, bool> DefaultIsWindows = static _ => OperatingSystem.IsWindows();
    private static readonly Func<string, bool> DefaultIsMacOS = static _ => OperatingSystem.IsMacOS();
    private static readonly Func<string?> DefaultOllamaHostReader = static () =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(HostEnvVar, EnvironmentVariableTarget.User)
            : null;
    private static readonly Func<CancellationToken, Task> DefaultPortDelay =
        static ct => Task.Delay(TimeSpan.FromMilliseconds(500), ct);

    // ------------------------------------------------------------------
    //  Public surface
    // ------------------------------------------------------------------

    /// <summary>The OLLAMA_HOST value: loopback by default, LAN-mesh (0.0.0.0) when enabled.</summary>
    public string OllamaHost(bool lanMesh) => lanMesh ? $"0.0.0.0:{OllamaPort}" : $"127.0.0.1:{OllamaPort}";

    /// <summary>Whether the one-click "Configure Local Ollama for Router" action is supported
    /// (Windows + macOS; Linux gets manual instructions and the command is disabled).</summary>
    public bool SupportsOneClick => (IsWindows ?? DefaultIsWindows)(string.Empty) || (IsMacOS ?? DefaultIsMacOS)(string.Empty);

    /// <summary>Detects a local Ollama (binary present? path? process running?) — read-only.</summary>
    public Task<OllamaDetection> DetectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (win, mac) = Platform();
        var paths = win ? WindowsBinarySearchPaths : mac ? MacBinarySearchPaths : LinuxBinarySearchPaths;
        var detection = (BinaryProbe ?? DefaultBinaryProbe)(paths);
        return Task.FromResult(detection);
    }

    /// <summary>
    /// The FULL one-click configure: env + stop Ollama + start Ollama on <c>:11435</c> +
    /// wait for the port + start the router daemon on <c>:11434</c> (the existing
    /// <see cref="IAppConnectionService.DaemonLauncher"/>).
    /// </summary>
    public Task<LocalOllamaConfigResult> ConfigureForRouterAsync(
        bool lanMesh, IAppConnectionService connection, CancellationToken ct) =>
        ConfigureAsync(lanMesh, connection, startRouter: true, ct);

    /// <summary>
    /// The START-ONLY path (Ollama installed but not running): env + stop + start Ollama on
    /// <c>:11435</c> + wait for the port — WITHOUT starting the router.
    /// </summary>
    public Task<LocalOllamaConfigResult> StartOllamaOnlyAsync(bool lanMesh, CancellationToken ct) =>
        ConfigureAsync(lanMesh, connection: null, startRouter: false, ct);

    // ------------------------------------------------------------------
    //  Install / update / port check (task #150)
    // ------------------------------------------------------------------

    /// <summary>
    /// One-click INSTALL via the official ollama.com script (task #150 — all three OSes):
    /// Windows → <c>powershell.exe</c> + <c>install.ps1</c>, elevated through the UAC
    /// <c>runas</c> verb; macOS/Linux → <c>/bin/sh</c> + <c>curl -fsSL …/install.sh | sh</c>.
    /// Never throws: launch failures (e.g. a DECLINED UAC prompt → <c>Win32Exception</c>) and
    /// non-zero exits are mapped to readable messages naming the failure. The Step 1 view
    /// model performs the re-detection on completion (one owner, same pattern as every other
    /// action).
    /// </summary>
    public async Task<OllamaScriptResult> InstallOllamaAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (started, launchError, run) = await RunOfficialScriptAsync(ct).ConfigureAwait(false);
        if (!started)
        {
            return new OllamaScriptResult(false, $"The Ollama installer could not be started: {launchError}", false);
        }

        if (run!.ExitCode != 0)
        {
            return new OllamaScriptResult(
                false, $"The Ollama installer exited with code {run.ExitCode}.{OutputTailsText(run)}", false);
        }

        return new OllamaScriptResult(true, "Ollama installer finished — detecting the new state…", false);
    }

    /// <summary>
    /// One-click UPDATE (task #150 — all three OSes; task #160 preserves the detected
    /// LAN bind). The ordering is contractual: STOP Ollama → run the official script →
    /// (on Windows: persist the detected bind to the User env — the post-reboot source of
    /// truth) → RESTART Ollama (explicit env + port wait) with the SAME OLLAMA_HOST bind
    /// the caller detected (<paramref name="lanMesh"/>: true = <c>0.0.0.0</c>, false =
    /// loopback) — the update must NOT silently reset a LAN-configured Ollama to loopback.
    /// On a script failure (or a failing persisted-env write) Ollama is honestly left
    /// STOPPED: the message says so and names the recovery (retry Update / manual start) —
    /// no restart is attempted after a failed step.
    /// </summary>
    public async Task<OllamaScriptResult> UpdateOllamaAsync(bool lanMesh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (win, _) = Platform();

        // 1. Stop FIRST (contractual order) — Ollama stays stopped until the update succeeds.
        StopOllamaProcesses(win);

        // 2. The official script (the same per-OS launch as install).
        var (started, launchError, run) = await RunOfficialScriptAsync(ct).ConfigureAwait(false);
        if (!started)
        {
            return new OllamaScriptResult(false,
                $"Ollama was stopped and the update failed: the update script could not be started ({launchError}). " +
                "Retry Update, or start Ollama again manually.", false);
        }

        if (run!.ExitCode != 0)
        {
            return new OllamaScriptResult(false,
                $"Ollama was stopped and the update failed: the update script exited with code {run.ExitCode}." +
                OutputTailsText(run) + " Retry Update, or start Ollama again manually.", false);
        }

        // 3. Re-detect (the script may have installed/moved the binary) + restart with
        //    EXPLICIT env + port wait (the existing start-only path — on Windows this is the
        //    tray app; on Linux this is <binary> serve with the explicit env and NO persistent
        //    env write, per the M1 scope). Task #160 decision 5 (bind-config preservation):
        //    on Windows the detected bind is ALSO persisted to the User env — the
        //    post-reboot source of truth — so the update never silently resets a
        //    LAN-configured Ollama to loopback.
        if (win)
        {
            try
            {
                WriteEnvironment(windows: true, host: OllamaHost(lanMesh), ct);
            }
            catch (Exception ex)
            {
                // A failing persisted-env write is a READABLE failure (the ConfigureAsync
                // convention): Ollama is already stopped — say so and name the recovery.
                return new OllamaScriptResult(false,
                    $"Ollama was stopped and the update failed: the OLLAMA_HOST environment " +
                    $"variable could not be persisted ({ex.Message}). " +
                    "Retry Update, or start Ollama again manually.", false);
            }
        }

        var startInfo = BuildOllamaStartInfo(win, host: OllamaHost(lanMesh), ct);
        if (startInfo is null)
        {
            return new OllamaScriptResult(false, OllamaNotFoundMessage(win, afterUpdate: true), false);
        }

        (ProcessStarter ?? DefaultProcessStarter)(startInfo);
        if (!await WaitForPortAsync(OllamaPort, ct).ConfigureAwait(false))
        {
            return new OllamaScriptResult(false,
                $"Ollama did not open port {OllamaPort} within the wait budget — check that it is not " +
                "bound to a different port.", false);
        }

        return new OllamaScriptResult(true, $"Ollama updated and serving on :{OllamaPort}.", true);
    }

    /// <summary>
    /// Effective-Ollama-port + LAN-state detection (task #150; task #160 — read-only, non-
    /// throwing by design): Ollama not running → <c>(false, null, …)</c> with ZERO port
    /// probes (the LAN state comes from the persisted env); a CONFIRMED persisted OLLAMA_HOST
    /// (its own probe open) wins — e.g. <c>0.0.0.0:11434</c> detected even though the probes
    /// hit loopback; otherwise the first OPEN candidate port (11435 first — the common case
    /// is a single probe). Running but nothing open → <c>(true, null, …)</c> (the UI shows the
    /// Re-check hint). The LAN state itself is socket-probe / persisted-env derived INSIDE
    /// this same pass — no extra binary detection. The Step 1 view model reuses its own
    /// detection pass through the <c>(detection, ct)</c> overload so one refresh performs
    /// exactly ONE binary detection (task #147 "the initial entry runs exactly once"
    /// invariant).
    /// </summary>
    public async Task<OllamaPortCheck> DetectPortAsync(CancellationToken ct)
    {
        OllamaDetection detection;
        try
        {
            detection = await DetectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // A failing binary detection is treated as "not running" with an undeterminable
            // (Unknown) LAN state — read-only by design, never a throw.
            return new OllamaPortCheck(false, null, OllamaLanState.Unknown);
        }

        return await DetectPortAsync(detection, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The port-detection core over a PROVIDED detection (task #150; extended by task #160):
    /// avoids a second binary probe for callers that already ran <see cref="DetectAsync"/> —
    /// the Step 1 refresh uses this so each refresh stays exactly one detection (task #147
    /// invariant). The LAN state is computed INSIDE this same pass (socket probe against a
    /// non-loopback address when running, persisted-env rule otherwise — NEVER an additional
    /// binary probe).
    /// </summary>
    internal Task<OllamaPortCheck> DetectPortAsync(OllamaDetection detection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!detection.ProcessRunning)
        {
            // no port probes for a stopped Ollama — the LAN state comes from the persisted env
            return Task.FromResult(new OllamaPortCheck(false, null, LanStateFromPersistedEnv()));
        }

        var probe = PortProbe ?? DefaultPortProbe;

        // 1. The CONFIRMED persisted value wins when its own probe opens (Windows: User env).
        string? host;
        try
        {
            host = (OllamaHostReader ?? DefaultOllamaHostReader)();
        }
        catch
        {
            host = null; // a failing reader is treated as "no persisted value"
        }

        var envPort = TryParseOllamaHostPort(host);
        if (envPort is not null)
        {
            try
            {
                if (probe(envPort.Value))
                {
                    return Task.FromResult(new OllamaPortCheck(true, envPort, LanStateForRunning(envPort.Value)));
                }
            }
            catch
            {
                // A failing probe is treated as closed (fall through to the candidates).
            }
        }

        // 2. Candidate probe in order (11435 first — the common case is a single probe).
        foreach (var port in OllamaPortCandidates)
        {
            try
            {
                if (probe(port))
                {
                    return Task.FromResult(new OllamaPortCheck(true, port, LanStateForRunning(port)));
                }
            }
            catch
            {
                // Skip failing probes.
            }
        }

        // 3. Running but nothing open → the persisted-env rule (the UI shows the Re-check hint).
        return Task.FromResult(new OllamaPortCheck(true, null, LanStateFromPersistedEnv()));
    }

    /// <summary>
    /// The LAN state for a RUNNING Ollama (task #160): a decisive LAN-bind probe against one
    /// of this machine's non-loopback IPv4 addresses wins — true = connect succeeded (bound
    /// to all interfaces → LAN), false = refused or probe-window timeout (loopback-only);
    /// a probe ERROR or the ABSENCE of a non-loopback IPv4 (offline machine) falls through
    /// to the persisted-env rule. Socket probes only — this never touches the binary probe.
    /// </summary>
    private OllamaLanState LanStateForRunning(int port)
    {
        try
        {
            var addresses = (NonLoopbackLocalAddresses ?? DefaultNonLoopbackAddresses)();
            if (addresses.Count > 0)
            {
                try
                {
                    var result = (LanBindProbe ?? DefaultLanBindProbe)(addresses[0].ToString(), port);
                    if (result is true)
                    {
                        return OllamaLanState.Lan;
                    }

                    if (result is false)
                    {
                        return OllamaLanState.Loopback;
                    }
                }
                catch
                {
                    // A throwing probe is treated as a probe error (fall through to the env rule).
                }
            }
        }
        catch
        {
            // A throwing address resolver is treated as "no addresses" (fall through).
        }

        return LanStateFromPersistedEnv();
    }

    /// <summary>
    /// The LAN state from the PERSISTED OLLAMA_HOST (task #160): Windows reads the User
    /// environment (the existing <see cref="OllamaHostReader"/> seam), macOS reads
    /// <c>launchctl getenv OLLAMA_HOST</c> through the new <see cref="LaunchctlGetenv"/>
    /// output-capturing seam, Linux → the loopback default (no GUI-readable persisted value).
    /// Each reader failure degrades to null → the parser's loopback default.
    /// </summary>
    private OllamaLanState LanStateFromPersistedEnv()
    {
        var (win, mac) = Platform();
        string? host;
        try
        {
            host = win
                ? (OllamaHostReader ?? DefaultOllamaHostReader)()
                : mac
                    ? (LaunchctlGetenv ?? DefaultLaunchctlGetenv)(HostEnvVar)
                    : null; // Linux: no GUI-readable persisted value → the loopback default
        }
        catch
        {
            host = null; // a failing reader is treated as "no persisted value"
        }

        return TryParseOllamaHostLanState(host);
    }

    /// <summary>
    /// Pure OLLAMA_HOST LAN-state parser (task #160 — unit-testable). The host part is the
    /// trimmed text before the LAST ':' (so <c>"[::]:11435"</c> yields <c>::</c>), with
    /// surrounding square brackets stripped; <c>0.0.0.0</c> / <c>::</c> / <c>*</c> →
    /// <see cref="OllamaLanState.Lan"/>, <c>127.0.0.1</c> / <c>localhost</c> / a hostless
    /// value (<c>":11435"</c> / a bare port) → <see cref="OllamaLanState.Loopback"/>
    /// (Ollama's default bind); null / empty → Loopback; anything else (e.g.
    /// <c>"192.168.1.5"</c>) → <see cref="OllamaLanState.Unknown"/>.
    /// </summary>
    internal static OllamaLanState TryParseOllamaHostLanState(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return OllamaLanState.Loopback; // unset → Ollama's default bind (loopback)
        }

        var trimmed = host.Trim();
        var colon = trimmed.LastIndexOf(':');
        if (colon < 0)
        {
            // A bare value has no host component: a (in-range) port is Ollama's bare-port form
            // whose host defaults to loopback; anything else is junk → Unknown.
            return TryParseOllamaHostPort(trimmed) is not null
                ? OllamaLanState.Loopback
                : OllamaLanState.Unknown;
        }

        var hostPart = trimmed[..colon];
        if (hostPart.Length >= 2 && hostPart[0] == '[' && hostPart[^1] == ']')
        {
            hostPart = hostPart[1..^1]; // bracketed IPv6: "[::]" → "::"
        }

        return hostPart switch
        {
            "" or "127.0.0.1" or "localhost" => OllamaLanState.Loopback,
            "0.0.0.0" or "::" or "*" => OllamaLanState.Lan,
            _ => OllamaLanState.Unknown,
        };
    }

    /// <summary>
    /// Pure OLLAMA_HOST port parser (task #150 — unit-testable): <c>"127.0.0.1:11435"</c> /
    /// <c>"0.0.0.0:11435"</c> / <c>":11435"</c> / <c>"11435"</c> → 11435; null / empty /
    /// junk / <c>"127.0.0.1:"</c> / out-of-range → null.
    /// </summary>
    internal static int? TryParseOllamaHostPort(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var trimmed = host.Trim();
        var colon = trimmed.LastIndexOf(':');
        var portText = colon >= 0 ? trimmed[(colon + 1)..] : trimmed;
        if (portText.Length is 0 or > 5)
        {
            return null;
        }

        return int.TryParse(portText, out var port) && port > 0 && port <= ushort.MaxValue ? port : null;
    }

    // ------------------------------------------------------------------
    //  Pipeline
    // ------------------------------------------------------------------

    private async Task<LocalOllamaConfigResult> ConfigureAsync(
        bool lanMesh, IAppConnectionService? connection, bool startRouter, CancellationToken ct)
    {
        var (win, mac) = Platform();
        if (!win && !mac)
        {
            return new LocalOllamaConfigResult(
                false, "One-click Ollama configuration supports Windows and macOS only.", false, false);
        }

        var host = OllamaHost(lanMesh);
        try
        {
            // 1. Environment variables.
            WriteEnvironment(win, host, ct);

            // 2. Stop a running Ollama (only the Ollama process names — never anything else).
            StopOllamaProcesses(win);

            // 3. Start Ollama on :11435 (explicit env — no stale inheritance).
            var startInfo = BuildOllamaStartInfo(win, host, ct);
            if (startInfo is null)
            {
                return new LocalOllamaConfigResult(
                    false, OllamaNotFoundMessage(win, afterUpdate: false), false, false);
            }

            (ProcessStarter ?? DefaultProcessStarter)(startInfo);
            if (!await WaitForPortAsync(OllamaPort, ct).ConfigureAwait(false))
            {
                return new LocalOllamaConfigResult(
                    false,
                    $"Ollama did not open port {OllamaPort} within the wait budget — check that it is not " +
                    "bound to a different port.",
                    false, false);
            }

            // 4. Start the router daemon on :11434 (existing launcher: binary resolution, port
            //    pre-check, conflict diagnostics).
            if (startRouter)
            {
                var launcher = connection?.DaemonLauncher
                    ?? throw new InvalidOperationException("No daemon launcher for the local profile.");
                await launcher.StartAsync(ct).ConfigureAwait(false);
                return new LocalOllamaConfigResult(
                    true,
                    $"Ollama is serving on {host} and the router is starting on port {RouterPort}.",
                    true, true);
            }

            return new LocalOllamaConfigResult(
                true, $"Ollama is serving on {host}.", true, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LocalOllamaConfigResult(false, $"Configuration failed: {ex.Message}", false, false);
        }
    }

    private void WriteEnvironment(bool windows, string host, CancellationToken ct)
    {
        if (windows)
        {
            var writer = EnvWriter ?? ((n, v, t) => Environment.SetEnvironmentVariable(n, v, t));
            writer(HostEnvVar, host, EnvironmentVariableTarget.User);
            writer(FlashAttentionEnvVar, "1", EnvironmentVariableTarget.User);
            writer(KvCacheTypeEnvVar, "q8_0", EnvironmentVariableTarget.User);
            return;
        }

        // macOS: launchctl setenv (so GUI-launched Ollama.app inherits the values).
        var starter = ProcessStarter ?? DefaultProcessStarter;
        var envs = new (string Name, string Value)[]
        {
            (HostEnvVar, host),
            (FlashAttentionEnvVar, "1"),
            (KvCacheTypeEnvVar, "q8_0"),
        };
        foreach (var (name, value) in envs)
        {
            ct.ThrowIfCancellationRequested();
            starter(new ProcessStartInfo("launchctl", $"setenv {name} {value}") { UseShellExecute = false });
        }
    }

    private void StopOllamaProcesses(bool windows)
    {
        var names = windows
            ? new[] { "ollama", "ollama app" }
            : new[] { "Ollama", "ollama" };
        var lookup = ProcessNames ?? DefaultProcessNames;
        var killer = ProcessKiller ?? DefaultProcessKiller;
        foreach (var name in names)
        {
            foreach (var pid in lookup(name))
            {
                if (int.TryParse(pid, out var id))
                {
                    killer(name, id);
                }
            }
        }
    }

    private ProcessStartInfo? BuildOllamaStartInfo(bool windows, string host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Task #157: FULL platform path selection — Windows branches to the tray app (task
        // #157, parity with OllamaProcessLauncher); the Linux non-.app branch produces
        // <binary> serve with the EXPLICIT env (no persistent env write on Linux).
        // (The `windows` parameter is vestigial — Platform() is authoritative.)
        var (win, mac) = Platform();
        if (win)
        {
            // Task #157: launch the Ollama TRAY APP (parity with OllamaProcessLauncher). The
            // spawned serve child inherits the explicit env below and binds :11435. Probe the
            // tray path through the existing BinaryProbe seam and equality-gate: the default
            // probe's PATH fallback could surface a bare ollama(.exe) that must NOT be launched
            // with --fast-startup hidden (and no headless serve fallback is allowed — the
            // caller emits the readable tray-app-not-found failure instead).
            var tray = WindowsTrayAppPath;
            var trayDetection = (BinaryProbe ?? DefaultBinaryProbe)([tray]);
            if (!trayDetection.Installed ||
                !string.Equals(trayDetection.BinaryPath, tray, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var app = new ProcessStartInfo(tray, "--fast-startup hidden")
            {
                UseShellExecute = false, // required: honored only when false — the env below matters
                CreateNoWindow = true, // no-op for the GUI tray app; preserves the "no window" intent
                WorkingDirectory = Path.GetDirectoryName(tray)!,
            };
            app.EnvironmentVariables[HostEnvVar] = host;
            app.EnvironmentVariables[FlashAttentionEnvVar] = "1";
            app.EnvironmentVariables[KvCacheTypeEnvVar] = "q8_0";
            return app;
        }

        var paths = mac ? MacBinarySearchPaths : LinuxBinarySearchPaths;
        var detection = (BinaryProbe ?? DefaultBinaryProbe)(paths);
        if (!detection.Installed || string.IsNullOrWhiteSpace(detection.BinaryPath))
        {
            return null;
        }

        var binary = detection.BinaryPath;
        if (!binary.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            // <binary> serve with EXPLICIT env (the child must not inherit a stale OLLAMA_HOST).
            var serve = new ProcessStartInfo(binary, "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            serve.EnvironmentVariables[HostEnvVar] = host;
            serve.EnvironmentVariables[FlashAttentionEnvVar] = "1";
            serve.EnvironmentVariables[KvCacheTypeEnvVar] = "q8_0";
            return serve;
        }

        // macOS Ollama.app present: open the bundle in the background with the host env.
        return new ProcessStartInfo("open", $"-g --env {HostEnvVar}={host} -a {binary}")
        {
            UseShellExecute = false,
        };
    }

    private async Task<bool> WaitForPortAsync(int port, CancellationToken ct)
    {
        var probe = PortProbe ?? DefaultPortProbe;
        var delay = Delay ?? DefaultPortDelay;
        var deadline = Environment.TickCount64 + (long)PortWaitBudget.TotalMilliseconds;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (probe(port))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await delay(ct).ConfigureAwait(false);
        }
    }

    private (bool Windows, bool MacOs) Platform()
    {
        var win = (IsWindows ?? DefaultIsWindows)(string.Empty);
        var mac = (IsMacOS ?? DefaultIsMacOS)(string.Empty);
        return (win, mac);
    }

    // ------------------------------------------------------------------
    //  Production defaults (real BCL behavior; never exercised by unit tests)
    // ------------------------------------------------------------------

    private static readonly string[] WindowsBinarySearchPaths =
    {
        @"C:\Program Files\Ollama\ollama.exe",
        @"C:\Program Files (x86)\Ollama\ollama.exe",
        @"C:\ProgramData\chocolatey\bin\ollama.exe",
    };

    private static readonly string[] MacBinarySearchPaths =
    {
        OllamaAppBundle,
        @"/usr/local/bin/ollama",
        @"/opt/homebrew/bin/ollama",
    };

    private static readonly string[] LinuxBinarySearchPaths =
    {
        @"/usr/local/bin/ollama",
        @"/usr/bin/ollama",
        @"/snap/bin/ollama",
    };

    private static OllamaDetection DefaultBinaryProbe(IReadOnlyList<string> paths)
    {
        string? found = null;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path) || (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)))
                {
                    found = path;
                    break;
                }
            }
            catch (Exception)
            {
                // Skip unreadable candidates.
            }
        }

        if (found is null)
        {
            found = FindOnPath("ollama");
        }

        return new OllamaDetection(found is not null, found, RunningOllamaProcess());
    }

    private static string? FindOnPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), name + suffix);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // Skip malformed PATH entries.
            }
        }

        return null;
    }

    private static bool RunningOllamaProcess()
    {
        try
        {
            return Process.GetProcesses().Any(p =>
            {
                var name = p.ProcessName;
                p.Dispose();
                return name.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Ollama", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> DefaultProcessNames(string name)
    {
        var pids = new List<string>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        pids.Add(process.Id.ToString());
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // Best-effort enumeration.
        }

        return pids;
    }

    private static bool DefaultProcessKiller(string name, int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string DefaultProcessStarter(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
        // The child is detached and its exit code is never read: release the handle right away.
        var id = process.Id;
        process.Dispose();
        return id.ToString();
    }

    private async Task<(bool Started, string? LaunchError, ProcessRunResult? Run)> RunOfficialScriptAsync(
        CancellationToken ct)
    {
        try
        {
            var run = await (ScriptRunner ?? DefaultScriptRunner)(BuildInstallScriptStartInfo(), ct)
                .ConfigureAwait(false);
            return (true, null, run);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null); // e.g. a declined UAC prompt → Win32Exception
        }
    }

    /// <summary>
    /// The official ollama.com installer launch (task #150): Windows → PowerShell +
    /// <c>install.ps1</c> with <c>UseShellExecute</c> + <c>Verb="runas"</c> (UAC elevation —
    /// stream redirection is impossible, so the Step 1 status message + the native installer
    /// console are the "progress"); macOS/Linux → <c>/bin/sh</c> + <c>curl | sh</c>
    /// (redirected — the captured output tails drive the failure message).
    /// </summary>
    private ProcessStartInfo BuildInstallScriptStartInfo()
    {
        var (win, _) = Platform();
        if (win)
        {
            return new ProcessStartInfo(
                    "powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://ollama.com/install.ps1 | iex\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };
        }

        return new ProcessStartInfo("/bin/sh", "-c \"curl -fsSL https://ollama.com/install.sh | sh\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
    }

    /// <summary>The readable output-tail suffix for a non-zero script exit (empty when both
    /// tails are empty — the UAC case, where redirection is impossible).</summary>
    private static string OutputTailsText(ProcessRunResult run)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(run.OutputTail))
        {
            parts.Add($"output: {run.OutputTail}");
        }

        if (!string.IsNullOrWhiteSpace(run.ErrorTail))
        {
            parts.Add($"error: {run.ErrorTail}");
        }

        return parts.Count == 0 ? string.Empty : $" ({string.Join(" | ", parts)})";
    }

    /// <summary>
    /// The default TCP port probe (task #103): returns <c>true</c> ONLY when a connect to
    /// <c>127.0.0.1:port</c> SUCCEEDS. A failed connect (refused/reset) also completes the
    /// async operation, so <see cref="TcpClient.EndConnect"/> must be called to distinguish
    /// success from failure (internal static so the unit tests cover the real default).
    /// </summary>
    internal static bool DefaultPortProbe(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.BeginConnect("127.0.0.1", port, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500)))
            {
                return false; // the connect did not complete within the probe window
            }

            client.EndConnect(connect); // throws when the connect FAILED
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The default non-loopback-address resolver (task #160): this machine's IPv4 addresses
    /// that are NOT loopback (<see cref="Dns.GetHostAddresses"/filtered), empty on any failure
    /// (e.g. DNS failure on an offline machine) so the detection falls through to the
    /// persisted-env rule.
    /// </summary>
    private static IReadOnlyList<IPAddress> DefaultNonLoopbackAddresses()
    {
        try
        {
            return Array.FindAll(
                Dns.GetHostAddresses(Dns.GetHostName()),
                address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        }
        catch (Exception)
        {
            return []; // a failing resolver degrades to "no addresses" (never throws)
        }
    }

    /// <summary>
    /// The default LAN bind connect probe (task #160): (ip, port) → <c>true</c> when a connect
    /// to the given address:port SUCCEEDS (Ollama is bound to all interfaces), <c>false</c>
    /// when the connect is REFUSED or does not complete within the 500 ms probe window
    /// (same window as <see cref="DefaultPortProbe"/> — a timeout means loopback-only), and
    /// <c>null</c> on any OTHER exception (a probe error — the caller falls through to the
    /// persisted-env rule). Internal static so the bounded real-socket unit test can cover
    /// the real default (the <see cref="DefaultPortProbe"/> convention).
    /// </summary>
    internal static bool? DefaultLanBindProbe(string ip, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.BeginConnect(ip, port, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500)))
            {
                return false; // the connect did not complete within the probe window ⇒ loopback-only
            }

            try
            {
                client.EndConnect(connect); // throws when the connect FAILED
                return true;
            }
            catch (SocketException socketException) when (
                socketException.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset)
            {
                return false; // refused/reset ⇒ loopback-only
            }
        }
        catch (Exception)
        {
            return null; // a probe ERROR (any other exception) ⇒ the caller uses the env rule
        }
    }

    /// <summary>
    /// The default macOS <c>launchctl getenv</c> reader (task #160 — the DefaultScriptRunner
    /// pattern): runs <c>launchctl getenv &lt;name&gt;</c> with a redirected stdout and returns
    /// the trimmed value, or null when the output is empty, the process cannot be started
    /// (not on macOS) or anything else goes wrong — never throws.
    /// </summary>
    private static string? DefaultLaunchctlGetenv(string name)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("launchctl", $"getenv {name}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            }) ?? throw new InvalidOperationException($"Failed to start 'launchctl'.");

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(); // `launchctl getenv` exits instantly; the read drained it
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (Exception)
        {
            return null; // missing launchctl / spawn failure → "no persisted value"
        }
    }

    private static readonly Func<ProcessStartInfo, CancellationToken, Task<ProcessRunResult>> DefaultScriptRunner =
        DefaultScriptRunnerCore;

    private static async Task<ProcessRunResult> DefaultScriptRunnerCore(ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        // UseShellExecute=true (the Windows UAC case) forbids stream redirection — the tails
        // stay empty there (the exit code still drives the result).
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
        {
            return new ProcessRunResult(process.ExitCode, string.Empty, string.Empty);
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        return new ProcessRunResult(process.ExitCode, Tail(stdout), Tail(stderr));
    }

    /// <summary>The last ~500 characters (trimmed) of a stream capture (the message tails).</summary>
    private static string Tail(string output, int maxChars = 500)
    {
        var trimmed = output.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[^maxChars..];
    }
}
