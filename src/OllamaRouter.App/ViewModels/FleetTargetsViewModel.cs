using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// One merged fleet-target row of the Fleet &amp; Cloud Targets tab (task #102, spec §5.2):
/// <c>GET /api/router/fleet</c> (liveness, effective hardware, execution, effective preload
/// policy) + <c>GET /api/router/config</c> targets (editable configuration) +
/// <c>GET /api/router/fleet/hardware</c> (per-node hardware profiles).
/// <para>
/// The EFFECTIVE <see cref="SpeedFactor"/> / <see cref="MaxVramMB"/> /
/// <see cref="EffectiveKeepAliveSeconds"/> (+ <see cref="KeepAliveSource"/>) are READ-ONLY
/// values reported by the fleet endpoint (API-bound scope: no wire API persists them).
/// </para>
/// </summary>
public sealed partial class FleetTargetRow : ObservableObject
{
    /// <summary>The canonical target name.</summary>
    public required string Name { get; init; }

    /// <summary>The target base URL.</summary>
    public string? Url { get; init; }

    /// <summary>The protocol family ("ollama" / "openai" / "osnative").</summary>
    public string Type { get; init; } = "ollama";

    /// <summary>The informational provider label (e.g. "OpenRouter", "Groq").</summary>
    public string? Provider { get; init; }

    /// <summary>The PERSISTED administrative enablement (config flag; distinct from the
    /// Monitor tab's runtime toggle).</summary>
    public bool Enabled { get; init; }

    /// <summary>The liveness state ("Online" / "Offline" / "Probing" / "Unknown").</summary>
    public string Liveness { get; init; } = "Unknown";

    /// <summary>The last probe latency in milliseconds (null when unknown).</summary>
    public long? LatencyMs { get; init; }

    /// <summary>The location classification ("local" / "remote" / "cloud").</summary>
    public string Location { get; init; } = "remote";

    // -- Effective hardware (READ-ONLY, from GET /api/router/fleet) -------------

    /// <summary>The effective max VRAM in MB (read-only; no wire API to persist it).</summary>
    public int? MaxVramMB { get; init; }

    /// <summary>The effective speed factor (read-only; no wire API to persist it).</summary>
    public double? SpeedFactor { get; init; }

    /// <summary>The effective parallel budget (read-only).</summary>
    public int MaxParallel { get; init; } = 1;

    /// <summary>Whether the node reports unified memory.</summary>
    public bool IsUnifiedMemory { get; init; }

    /// <summary>The system RAM in MB (when reported).</summary>
    public long? SystemRamMB { get; init; }

    /// <summary>The memory technology label (when reported).</summary>
    public string? MemoryType { get; init; }

    /// <summary>The effective bandwidth in GB/s (when reported).</summary>
    public double? BandwidthGBs { get; init; }

    /// <summary>The GPU name (when reported).</summary>
    public string? GpuName { get; init; }

    /// <summary>The GPU vendor (when reported).</summary>
    public string? GpuVendor { get; init; }

    /// <summary>The EFFECTIVE configured preload keep-alive in seconds (-1 = indefinitely;
    /// null for Cloud; read-only).</summary>
    public int? EffectiveKeepAliveSeconds { get; init; }

    /// <summary>Which rule produced <see cref="EffectiveKeepAliveSeconds"/>
    /// ("explicit" / "class" / "default").</summary>
    public string? KeepAliveSource { get; init; }

    // -- Editable configuration (from GET /api/router/config targets) -----------

    /// <summary>The free-form hardware hint (display-only in v1).</summary>
    public string? HardwareHint { get; init; }

    /// <summary>The persisted maxParallel budget.</summary>
    public int ConfigMaxParallel { get; init; } = 1;

    /// <summary>The PERSISTED config max-VRAM override in MB (task #153; null = unset —
    /// distinct from the fleet endpoint's EFFECTIVE <see cref="MaxVramMB"/>, which already
    /// falls back to the hardware profile). Drives the card's VRAM-override annotation.</summary>
    public int? ConfigMaxVramMB { get; init; }

    /// <summary>Whether an upstream API key is configured (bool flag; key material is never
    /// exposed).</summary>
    public bool ApiKeyConfigured { get; init; }

    /// <summary>The node power-draw override in watts (null = unset; blank = no change on
    /// edit — clearing a set override is a documented API limitation).</summary>
    public double? PowerWatts { get; init; }

    /// <summary>The declared models roster (null/empty when none declared).</summary>
    public IReadOnlyList<TargetModelDto>? Models { get; init; }

    /// <summary>The daemon-side hardware probe ssh user (null = unset; null/blank → the row
    /// is not ssh-probeable; the probe host is the target URL's host, key-based auth).</summary>
    public string? SshUser { get; init; }

    /// <summary>The daemon-side hardware probe ssh port (null = the default port 22).</summary>
    public int? SshPort { get; init; }

    // -- Hardware probe busy state (task #144; carried by name across row rebuilds) --

    /// <summary>Whether a daemon-side hardware probe is in flight for this target (auto OR
    /// explicit; the view model carries it by name across the 5 s row rebuilds).</summary>
    [ObservableProperty]
    private bool _isProbing;

    /// <summary>A transient inline probe error (explicit 'Update hardware' failures ONLY —
    /// auto-probes stay silent; shown until the next refresh, then cleared — no timer).</summary>
    [ObservableProperty]
    private string? _probeError;

    // -- Execution (from GET /api/router/fleet) ---------------------------------

    /// <summary>The in-flight request count.</summary>
    public int InFlightRequests { get; init; }

    /// <summary>The first resident (active) model (when loaded).</summary>
    public string? ActiveModel { get; init; }

    /// <summary>The full resident model list (empty when idle).</summary>
    public IReadOnlyList<string> ResidentModels { get; init; } = [];

    // -- Hardware profile inspector (from GET /api/router/fleet/hardware) -------

    /// <summary>The profile provenance ("local" / "peer" / "persisted" / "manual" / "none").</summary>
    public string? HardwareSource { get; init; }

    /// <summary>Whether the node was reachable for the hardware probe.</summary>
    public bool? HardwareReachable { get; init; }

    /// <summary>The full hardware profile (null when unknown).</summary>
    public NodeHardwareProfile? HardwareProfile { get; init; }

    // -- Display leaves (compiled XAML bindings cannot traverse non-observable wire
    //    DTOs, so the nullable sub-paths are flattened here — read-only projections).
    //    Task #153: the one-line-per-topic hardware summary + keep-alive label + the
    //    VRAM-override annotation (via <see cref="FleetTargetDisplay"); the removed display
    //    leaves (ModelsCount, the per-field hardware leaves, Gpus) are gone — the raw data
    //    properties the form/probe flows use (Models, ApiKeyConfigured, SpeedFactor, …) stay.

    /// <summary>The always-expanded hardware section's header (never null; task #153): the
    /// provenance label with a null source coalesced to "none", e.g.
    /// <c>Hardware profile (peer)</c> / <c>Hardware profile (none)</c>.</summary>
    public string HardwareProfileHeader =>
        $"Hardware profile ({HardwareSource ?? "none"})";

    /// <summary>The merged OS line of the hardware summary (task #153 decision 3; null when
    /// the profile is unknown or the OS/arch are both blank).</summary>
    public string? HardwareOsLine =>
        HardwareProfile is null
            ? null
            : FleetTargetDisplay.BuildOsLine(HardwareProfile.Os, HardwareProfile.Architecture);

    /// <summary>The merged RAM/VRAM/bandwidth line of the hardware summary (task #153
    /// decision 3; null when the profile is unknown or every segment is 0).</summary>
    public string? HardwareMemoryLine =>
        HardwareProfile is null
            ? null
            : FleetTargetDisplay.BuildMemoryLine(
                HardwareProfile.SystemRamMB,
                HardwareProfile.TotalFastMemoryMB,
                HardwareProfile.EffectiveBandwidthGBs);

    /// <summary>The single-line GPU summary of the hardware section (task #153 decision 3;
    /// null when the profile is unknown or has no GPUs; unknown sub-values hidden per GPU).</summary>
    public string? HardwareGpuLine =>
        HardwareProfile?.Gpus is { Count: > 0 } gpus
            ? FleetTargetDisplay.BuildGpuLine(gpus)
            : null;

    /// <summary>The merged keep-alive chip (task #153 decision 5): seconds + the source
    /// suffix in ONE value (null = hidden).</summary>
    public string? KeepAliveLabel =>
        FleetTargetDisplay.FormatKeepAliveLabel(EffectiveKeepAliveSeconds, KeepAliveSource);

    /// <summary>The <c>VRAM override: {value}</c> annotation (task #153 decision 2): present
    /// only when the PERSISTED config MaxVramMB differs from the profile's fast memory
    /// (null = hidden).</summary>
    public string? VramOverrideLabel =>
        FleetTargetDisplay.BuildVramOverrideLabel(ConfigMaxVramMB, HardwareProfile);

    /// <summary>Whether a hardware profile is present (drives the always-expanded hardware
    /// section's value lines vs. the "no hardware profile yet" placeholder). Rows are
    /// rebuilt wholesale every poll tick, so no change notification is needed.</summary>
    public bool HasHardwareProfile => HardwareProfile is not null;

    /// <summary>The ssh credential label of the row info strip (null when unset).</summary>
    public string? SshLabel => SshUser is null ? null : $"ssh: {SshUser}@(port {SshPort ?? 22})";

    /// <summary>
    /// Whether the row is ssh-probeable (task #144 shared eligibility predicate, both
    /// surfaces): an Ollama-protocol, NON-LOCAL target with a non-blank ssh user (the
    /// daemon probes the local workstation itself; non-Ollama rows are never probed).
    /// </summary>
    public bool IsSshProbeable =>
        string.Equals(Type, "ollama", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Location, "local", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(SshUser);

    /// <summary>Whether the per-row 'Update hardware' action is available (enabled ONLY for
    /// ssh-probeable rows, and not already in flight).</summary>
    public bool CanUpdateHardware => !IsProbing && IsSshProbeable;

    /// <summary>The transient content of the 'Update hardware' button (busy state).</summary>
    public string UpdateHardwareButtonContent => IsProbing ? "Probing…" : "Update hardware";

    partial void OnIsProbingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUpdateHardware));
        OnPropertyChanged(nameof(UpdateHardwareButtonContent));
    }
}

/// <summary>
/// The Fleet &amp; Cloud Targets tab view model (task #102, spec §5.2): the merged
/// fleet + config + hardware view (1 tick = 3 GETs, 5 s poll), the add/edit/delete target
/// operations (with the masked-API-key wire semantics) and the persisted enablement toggle.
/// </summary>
public sealed partial class FleetTargetsViewModel : RouterTabViewModel
{
    /// <summary>The Fleet tab polling interval (spec §4).</summary>
    internal static readonly TimeSpan FleetInterval = TimeSpan.FromSeconds(5);

    public FleetTargetsViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<TimeSpan, CancellationToken, Task>? pollDelay = null)
        : base(clientAccessor, pollDelay)
    {
    }

    /// <inheritdoc />
    protected override TimeSpan PollInterval => FleetInterval;

    /// <summary>The merged fleet target rows (config order; Cloud pseudo-target excluded by
    /// the server).</summary>
    public ObservableCollection<FleetTargetRow> Rows { get; } = [];

    // -- Hardware probe bookkeeping (task #144): EVERY mutation under _probeGate ----
    // Refresh ticks and ~30 s probe completions race by design, so the in-flight set, the
    // pending transient errors and the auto-retry suppression are lock-guarded; ROW property
    // writes happen only inside the RunOnUiAsync marshal (rows are rebuilt every tick, so
    // the busy state is carried by NAME, never on the row objects).

    private readonly object _probeGate = new();

    /// <summary>Targets with an in-flight daemon-side hardware probe (auto OR explicit;
    /// case-insensitive).</summary>
    private readonly HashSet<string> _probingTargets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pending transient inline probe errors by target name (explicit 'Update
    /// hardware' failures; carried over to the next rebuild and consumed there — transient,
    /// no timer).</summary>
    private readonly Dictionary<string, string> _probeErrorByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Auto-probe suppression (maintainer decision 2026-11-20): a FAILED auto-probe
    /// is not retried on later refresh ticks (no retry storm against a dead host). The
    /// explicit 'Update hardware' button ALWAYS works; suppression is lifted by a successful
    /// Edit/Add/Delete of that target and by a VM rebuild.</summary>
    private readonly HashSet<string> _autoProbeNoRetry = new(StringComparer.OrdinalIgnoreCase);

    private static async Task RunOnUiAsync(Action action)
    {
        try
        {
            if (Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
            }
        }
        catch
        {
            action();
        }
    }

    /// <inheritdoc />
    protected override async Task RefreshCoreAsync(CancellationToken ct)
    {
        var client = ClientAccessor();
        var fleet = await client.GetFleetAsync(ct).ConfigureAwait(false);
        var config = await client.GetConfigAsync(ct).ConfigureAwait(false);
        var hardware = await client.GetFleetHardwareAsync(ct).ConfigureAwait(false);

        var fleetByName = fleet.Targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var configByName = config.Targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var hardwareByName = hardware.Nodes.ToDictionary(n => n.Target, StringComparer.OrdinalIgnoreCase);

        var eligibleNames = new List<string>();
        await RunOnUiAsync(() =>
        {
            Rows.Clear();
            // Config order is the canonical row order.
            foreach (var target in config.Targets)
            {
                fleetByName.TryGetValue(target.Name, out var fleetItem);
                hardwareByName.TryGetValue(target.Name, out var node);
                var hw = fleetItem?.Hardware;
                var loc = fleetItem?.Location ?? "remote";
                Views.TargetLocationVisuals.Register(target.Name, loc);
                var row = new FleetTargetRow
                {
                    Name = target.Name,
                    Url = target.Url,
                    Type = target.Type,
                    Provider = target.Provider,
                    Enabled = target.Enabled,
                    Liveness = fleetItem?.Liveness ?? "Unknown",
                    LatencyMs = fleetItem?.LatencyMs,
                    Location = loc,
                    MaxVramMB = hw?.MaxVramMB,
                    SpeedFactor = hw?.SpeedFactor,
                    MaxParallel = hw?.MaxParallel ?? target.MaxParallel,
                    IsUnifiedMemory = hw?.IsUnifiedMemory ?? false,
                    SystemRamMB = hw?.SystemRamMB,
                    MemoryType = hw?.MemoryType,
                    BandwidthGBs = hw?.BandwidthGBs,
                    GpuName = hw?.GpuName,
                    GpuVendor = hw?.GpuVendor,
                    EffectiveKeepAliveSeconds = fleetItem?.Preload?.KeepAliveSeconds,
                    KeepAliveSource = fleetItem?.Preload?.KeepAliveSource,
                    HardwareHint = target.HardwareHint,
                    ConfigMaxParallel = target.MaxParallel,
                    ConfigMaxVramMB = target.MaxVramMB,
                    ApiKeyConfigured = target.ApiKeyConfigured,
                    PowerWatts = target.PowerWatts,
                    Models = target.Models,
                    SshUser = target.SshUser,
                    SshPort = target.SshPort,
                    InFlightRequests = fleetItem?.Execution.InFlightRequests ?? 0,
                    ActiveModel = fleetItem?.Execution.ActiveModel,
                    ResidentModels = fleetItem?.Execution.ResidentModels
                        ?? (IReadOnlyList<string>)Array.Empty<string>(),
                    HardwareSource = node?.Source,
                    HardwareReachable = node?.Reachable,
                    HardwareProfile = node?.Profile,
                };

                // Task #144: carry the probe busy state over by NAME (rows are rebuilt every
                // tick but a ~30 s probe outlives them), and carry a pending explicit-probe
                // error over ONCE (consumed here → it clears on the refresh after that one).
                // The bookkeeping collections are read/CONSUMED under the SAME gate the
                // thread-pool probe cores write under (refresh ticks and ~30 s probe
                // completions race by design); the row property writes stay on the UI
                // marshal, outside the gate.
                var inFlight = false;
                var eligible = false;
                string? carriedError = null;
                lock (_probeGate)
                {
                    inFlight = _probingTargets.Contains(target.Name);
                    if (!inFlight && _probeErrorByName.TryGetValue(target.Name, out var pendingError))
                    {
                        carriedError = pendingError;
                        _probeErrorByName.Remove(target.Name);
                    }

                    // Auto-probe eligibility (task #144, maintainer-confirmed trigger): an
                    // ssh-probeable row (ollama + non-local + sshUser set) with NO stored
                    // profile yet (no node / source "none" / null profile), not already in
                    // flight and not suppressed after a failed auto-probe.
                    var noProfile = node is null
                        || string.Equals(node.Source, "none", StringComparison.OrdinalIgnoreCase)
                        || node.Profile is null;
                    eligible = row.IsSshProbeable && noProfile && !inFlight
                        && !_autoProbeNoRetry.Contains(target.Name);
                }

                row.IsProbing = inFlight;
                if (carriedError is not null)
                {
                    row.ProbeError = carriedError;
                }

                if (eligible)
                {
                    eligibleNames.Add(target.Name);
                }

                Rows.Add(row);
            }
        });

        // The auto-probe pass runs AFTER the apply (outside the UI marshal): one
        // fire-and-forget probe per eligible name — eligibleNames is a local list, so no
        // cross-thread collection access. A probe outlives this refresh (the 5 s poll must
        // never be blocked by a ~30 s ssh probe).
        foreach (var name in eligibleNames)
        {
            FireProbe(name, auto: true);
        }
    }

    /// <summary>
    /// Re-runs the daemon-side hardware probe for one row (the per-row 'Update hardware'
    /// action; the probe OVERWRITES the stored profile — including manual ones — enforced by
    /// the endpoint). Explicit probes are ALWAYS permitted for an ssh-probeable row, even
    /// when the auto-probe is suppressed for it. Fire-and-forget: the probe is server-bounded
    /// (~30 s) and must never block the tab's 5 s polling or the other rows. No tab-level
    /// banner on failure — per-row inline error only.
    /// </summary>
    [RelayCommand]
    public async Task UpdateHardwareAsync(FleetTargetRow? row, CancellationToken ct = default)
    {
        // null / not ssh-probeable / already in flight → no-op.
        if (row is null || !row.CanUpdateHardware)
        {
            return;
        }

        FireProbe(row.Name, auto: false);
    }

    /// <summary>
    /// Adds <paramref name="name"/> to the in-flight set (dedupe by name: a second fire
    /// while a probe is already in flight is a no-op) and starts the probe core
    /// fire-and-forget.
    /// </summary>
    private void FireProbe(string name, bool auto)
    {
        lock (_probeGate)
        {
            if (!_probingTargets.Add(name))
            {
                return;
            }
        }

        _ = ProbeTargetCoreAsync(name, auto);
    }

    /// <summary>
    /// The probe core (auto OR explicit): runs to completion on the thread pool with
    /// <see cref="CancellationToken.None"/> — the probe is server-bounded at ~30 s and must
    /// settle to release the per-row busy state even if the tab's ct is cancelled.
    /// Success → any pending error is dropped + a best-effort refresh (the single-writer
    /// gate may skip it while the poller holds the lock — the next tick picks up the profile,
    /// which is acceptable). Failure → explicit: a transient per-row inline error (the tab
    /// banner stays quiet); auto: SILENT + auto-retry suppression (config-condition 400
    /// failures stay retryable; host-level failures suppress — no retry storm against a dead
    /// host).
    /// </summary>
    private async Task ProbeTargetCoreAsync(string name, bool auto)
    {
        string? rowError = null;

        // The per-row busy state is set on the CURRENT row immediately (UI-marshalled — the
        // probe may be fired from a non-UI context; in tests there is no Avalonia application
        // so the marshal runs inline).
        await RunOnUiAsync(() =>
        {
            var row = Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                row.IsProbing = true;
            }
        });

        try
        {
            await ClientAccessor().ProbeTargetHardwareAsync(name, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            rowError = ex.Error ?? ex.Message;
            lock (_probeGate)
            {
                if (auto && ex.Status != System.Net.HttpStatusCode.BadRequest)
                {
                    _autoProbeNoRetry.Add(name);
                }

                if (!auto)
                {
                    _probeErrorByName[name] = rowError;
                }
            }
        }
        catch (Exception ex)
        {
            // Transport-level failure (connection refused / timeout): the explicit path gets
            // the same per-row error treatment; the auto path stays silent + suppressed.
            rowError = ex.Message;
            lock (_probeGate)
            {
                if (auto)
                {
                    _autoProbeNoRetry.Add(name);
                }

                if (!auto)
                {
                    _probeErrorByName[name] = rowError;
                }
            }
        }
        finally
        {
            lock (_probeGate)
            {
                _probingTargets.Remove(name);
            }

            // The rows are rebuilt under this task (the 5 s poll) — resolve the CURRENT row
            // by name and marshal the busy-state release to the UI thread.
            await RunOnUiAsync(() =>
            {
                var row = Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    return;
                }

                row.IsProbing = false;
                if (!auto && rowError is not null)
                {
                    row.ProbeError = rowError;
                }
            });
        }

        if (rowError is null)
        {
            // Success: drop any PENDING transient error (a previously failed explicit probe
            // whose error a refresh has not consumed yet must not resurface on a row that
            // just probed fine) and refresh so the rebuilt row shows the new profile
            // (source "ssh").
            lock (_probeGate)
            {
                _probeErrorByName.Remove(name);
            }

            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lifts the auto-probe suppression of <paramref name="name"/> (maintainer decision
    /// 2026-11-20): a successful config save (or delete) of that target means the ssh
    /// credential may have been fixed — the next refresh auto-probes it again.
    /// </summary>
    private void RemoveAutoProbeSuppression(string name)
    {
        lock (_probeGate)
        {
            _autoProbeNoRetry.Remove(name);
        }
    }

    /// <summary>
    /// Adds a fleet target (wire: <c>POST /api/router/targets</c>) and refreshes on 201.
    /// Server-side validation failures (400 invalid name/type / 409 duplicate / 422 probe
    /// failed / 400 apiKey on non-openai) surface in
    /// <see cref="RouterTabViewModel.LastError"/> with the parsed error text.
    /// </summary>
    [RelayCommand]
    public async Task AddTargetAsync(AddTargetRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        try
        {
            await ClientAccessor().AddTargetAsync(request, ct).ConfigureAwait(false);
            RemoveAutoProbeSuppression(request.Name);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            SetError(ex.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Updates a fleet target (wire: <c>PUT /api/router/targets/{name}</c>) and refreshes.
    /// The wire semantics of <c>apiKey</c> apply: null/absent = no change, <c>""</c> = clear.
    /// </summary>
    [RelayCommand]
    public async Task EditTargetAsync((string Name, UpdateTargetRequest Request) edit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(edit.Name))
        {
            throw new ArgumentException("The target name must not be empty.", nameof(edit));
        }

        if (edit.Request is null)
        {
            throw new ArgumentNullException(nameof(edit));
        }

        try
        {
            await ClientAccessor().UpdateTargetAsync(edit.Name, edit.Request, ct).ConfigureAwait(false);
            RemoveAutoProbeSuppression(edit.Name);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            SetError(ex.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a fleet target (wire: <c>DELETE /api/router/targets/{name}</c>) and refreshes
    /// (the row is gone from the next data source read).
    /// </summary>
    [RelayCommand]
    public async Task DeleteTargetAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The target name must not be empty.", nameof(name));
        }

        try
        {
            await ClientAccessor().DeleteTargetAsync(name, ct).ConfigureAwait(false);
            RemoveAutoProbeSuppression(name);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            SetError(ex.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Toggles the PERSISTED administrative enablement flag (wire:
    /// <c>PUT /api/router/targets/{name}</c> with <c>enabled</c> only) — distinct from the
    /// Monitor tab's RUNTIME toggle (<c>POST /targets</c>).
    /// </summary>
    [RelayCommand]
    public async Task TogglePersistedEnabledAsync(FleetTargetRow row, CancellationToken ct = default)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        try
        {
            await ClientAccessor()
                .UpdateTargetAsync(row.Name, new UpdateTargetRequest(Enabled: !row.Enabled), ct)
                .ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            SetError(ex.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Opens the shared target form for ADD mode (the view wires the form to the dialog).
    /// </summary>
    public TargetEditFormViewModel CreateAddForm() => new(isEdit: false);

    /// <summary>
    /// Opens the shared target form for EDIT mode, pre-filled from the row (the API key box
    /// means "no change" when untouched, "clear" when explicitly emptied).
    /// </summary>
    public TargetEditFormViewModel CreateEditForm(FleetTargetRow row) =>
        new(isEdit: true, existing: row ?? throw new ArgumentNullException(nameof(row)));

    /// <summary>
    /// Creates the dedicated "Add Ollama Node" form (task #149). The per-type add dialogs
    /// replace the shared form's fleet-tab UI entry point; <see cref="CreateAddForm"/> stays
    /// fully functional (tested) for the shared dialog's add-mode capability and reuse.
    /// </summary>
    public OllamaNodeAddFormViewModel CreateOllamaNodeAddForm() => new();

    /// <summary>
    /// Creates the dedicated "Add External Provider" form (task #149) for
    /// OpenAI-compatible endpoints — also the reuse point for task #151's wizard Step 2,
    /// which opens the SAME dedicated dialogs as the main app.
    /// </summary>
    public ExternalProviderAddFormViewModel CreateExternalProviderAddForm() => new();
}

/// <summary>
/// One editable row of the target models roster editor (name / contextLength /
/// quantization; blanks → null on submit).
/// </summary>
public sealed partial class ModelRosterEntry : ObservableObject
{
    /// <summary>The model name (required, non-blank).</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The declared context length in tokens (blank = null; must be &gt; 0).</summary>
    [ObservableProperty]
    private string _contextLength = string.Empty;

    /// <summary>The declared quantization label (blank = null).</summary>
    [ObservableProperty]
    private string _quantization = string.Empty;
}

/// <summary>
/// The shared add/edit target form (task #102, spec §5.2 — one form behind the
/// <c>TargetEditDialog</c> for both modes). Pure logic: validation + request construction;
/// the dialog view handles presentation.
/// </summary>
public sealed partial class TargetEditFormViewModel : ObservableObject
{
    private readonly bool _isEdit;

    /// <summary>The protocol family options of the type dropdown.</summary>
    public static readonly IReadOnlyList<string> TypeOptions = ["ollama", "openai", "osnative"];

    private string _name = string.Empty;
    private string _url = string.Empty;
    private string _type = "ollama";
    private string _provider = string.Empty;
    private string _maxParallel = "1";
    private string _hardwareHint = string.Empty;
    private string _apiKey = string.Empty;
    private string _powerWatts = string.Empty;
    private string _sshUser = string.Empty;
    private string _sshPort = string.Empty;
    private bool _testConnectionFirst = true;
    private bool _apiKeyTouched;
    private string? _validationErrorMessage;

    /// <summary>
    /// Creates the form. In edit mode the row's values are pre-filled and the API key box
    /// starts "untouched" (= "no change" on submit; the configured state is shown as a badge,
    /// never the key material).
    /// </summary>
    public TargetEditFormViewModel(bool isEdit, FleetTargetRow? existing = null)
    {
        _isEdit = isEdit;
        if (isEdit)
        {
            if (existing is null)
            {
                throw new ArgumentNullException(nameof(existing));
            }

            _name = existing.Name;
            _url = existing.Url ?? string.Empty;
            _type = existing.Type;
            _provider = existing.Provider ?? string.Empty;
            _maxParallel = existing.ConfigMaxParallel.ToString();
            _hardwareHint = existing.HardwareHint ?? string.Empty;
            _powerWatts = existing.PowerWatts?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty;
            _sshUser = existing.SshUser ?? string.Empty;
            _sshPort = existing.SshPort?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty;
            ExistingApiKeyConfigured = existing.ApiKeyConfigured;
            foreach (var model in existing.Models ?? (IReadOnlyList<TargetModelDto>)Array.Empty<TargetModelDto>())
            {
                Models.Add(new ModelRosterEntry
                {
                    Name = model.Name,
                    ContextLength = model.ContextLength?.ToString() ?? string.Empty,
                    Quantization = model.Quantization ?? string.Empty,
                });
            }
        }
    }

    /// <summary>True in EDIT mode (a different request type is built on submit).</summary>
    public bool IsEdit => _isEdit;

    /// <summary>The protocol family options.</summary>
    public IReadOnlyList<string> TypeChoices => TypeOptions;

    /// <summary>The target name (≤ 64 chars, no / \ :, not "Cloud").</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The target base URL (absolute http(s)).</summary>
    public string Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The protocol family ("ollama" / "openai" / "osnative").</summary>
    public string Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The informational provider label.</summary>
    public string Provider
    {
        get => _provider;
        set
        {
            if (SetProperty(ref _provider, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The maxParallel budget (must parse to an int ≥ 1).</summary>
    public string MaxParallel
    {
        get => _maxParallel;
        set
        {
            if (SetProperty(ref _maxParallel, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The free-form hardware hint.</summary>
    public string HardwareHint
    {
        get => _hardwareHint;
        set
        {
            if (SetProperty(ref _hardwareHint, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>
    /// The upstream (masked) target API key input. Wire semantics on submit: ADD mode — the
    /// value (blank → null); EDIT mode — untouched → null (NO CHANGE), explicitly emptied →
    /// <c>""</c> (CLEAR), filled → the value.
    /// </summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                _apiKeyTouched = true;
                ClearError();
            }
        }
    }

    /// <summary>Whether the user interacted with the API key box (edit mode: untouched = no
    /// change, emptied = clear).</summary>
    public bool IsApiKeyTouched => _apiKeyTouched;

    /// <summary>The node power-draw override in watts (blank = null = no change).</summary>
    public string PowerWatts
    {
        get => _powerWatts;
        set
        {
            if (SetProperty(ref _powerWatts, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>
    /// The daemon-side hardware probe ssh user (optional; the probe host is the target URL's
    /// host, key-based auth — no password). Wire semantics on submit: ADD — blank → null
    /// (unset); EDIT — blank → <c>""</c> (CLEAR, task #143 wire), value → set.
    /// </summary>
    public string SshUser
    {
        get => _sshUser;
        set
        {
            if (SetProperty(ref _sshUser, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>
    /// The daemon-side hardware probe ssh port (optional; blank = the default port 22).
    /// Wire semantics on submit: blank → null (ADD: unset; EDIT: NO CHANGE — the port wire
    /// has no clear semantics, the documented powerWatts precedent; clearing a set port
    /// requires editing config/config.json), value → set.
    /// </summary>
    public string SshPort
    {
        get => _sshPort;
        set
        {
            if (SetProperty(ref _sshPort, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>Whether an immediate liveness probe runs before persisting (add mode; default
    /// true — probe failure → 422 with a retry hint).</summary>
    public bool TestConnectionFirst
    {
        get => _testConnectionFirst;
        set => SetProperty(ref _testConnectionFirst, value);
    }

    /// <summary>The declared models roster editor rows.</summary>
    public ObservableCollection<ModelRosterEntry> Models { get; } = [];

    /// <summary>Whether the form currently has a client-side validation error.</summary>
    public bool HasValidationError => ValidationErrorMessage is not null;

    /// <summary>The masked API key box watermark (edit vs add hint).</summary>
    public string ApiKeyWatermark =>
        IsEdit
            ? (ExistingApiKeyConfigured
                ? "configured — leave blank = no change, empty = clear"
                : "blank = no change")
            : "required for OpenAI-compatible providers";

    /// <summary>Reports a submit-time validation error on the form (re-shows the message
    /// banner; the dialog stays open).</summary>
    public void ReportError(string? error) => ValidationErrorMessage = error;

    /// <summary>The client-side validation error (null when the form is valid).</summary>
    public string? ValidationErrorMessage
    {
        get => _validationErrorMessage;
        private set
        {
            if (SetProperty(ref _validationErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ApiKeyWatermark));
            }
        }
    }

    /// <summary>Whether the upstream API key is already configured (edit mode badge; key
    /// material is never shown).</summary>
    public bool ExistingApiKeyConfigured { get; set; }

    /// <summary>Adds an empty roster row.</summary>
    [RelayCommand]
    public void AddModelRow() => Models.Add(new ModelRosterEntry());

    /// <summary>Removes a roster row.</summary>
    [RelayCommand]
    public void RemoveModelRow(ModelRosterEntry? entry)
    {
        if (entry is not null)
        {
            Models.Remove(entry);
        }
    }

    /// <summary>
    /// Validates the form and builds the <c>POST /api/router/targets</c> request (add mode).
    /// </summary>
    public (AddTargetRequest Request, string? Error) BuildAddRequest()
    {
        ValidationErrorMessage = null;
        if (!ValidateCommon(out var error))
        {
            return (null!, error!);
        }

        var addModels = BuildModels(); // null = empty roster OR roster validation error
        if (addModels is null && Models.Count > 0)
        {
            return (null!, ValidationErrorMessage);
        }

        return (new AddTargetRequest(
            Name: Name.Trim(),
            Url: NormalizeUrl(),
            MaxParallel: int.Parse(MaxParallel),
            TestConnectionFirst: TestConnectionFirst,
            HardwareHint: string.IsNullOrWhiteSpace(HardwareHint) ? null : HardwareHint.Trim(),
            Type: Type,
            ApiKey: string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,
            Provider: string.IsNullOrWhiteSpace(Provider) ? null : Provider.Trim(),
            Models: addModels,
            PowerWatts: ParseWatts(),
            // Task #144: ADD — blank = null (unset); value = set.
            SshUser: SshUserWireValue(),
            SshPort: ParseSshPort()), null);
    }

    /// <summary>
    /// Validates the form and builds the <c>PUT /api/router/targets/{name}</c> request (edit
    /// mode; all fields are sent — the API-key no-change/clear semantics apply).
    /// </summary>
    public (UpdateTargetRequest Request, string? Error) BuildUpdateRequest()
    {
        ValidationErrorMessage = null;
        if (!ValidateCommon(out var error))
        {
            return (null!, error!);
        }

        var updateModels = BuildModels(); // null = empty roster OR roster validation error
        if (updateModels is null && Models.Count > 0)
        {
            return (null!, ValidationErrorMessage);
        }

        return (new UpdateTargetRequest(
            Url: NormalizeUrl(),
            Enabled: null,
            HardwareHint: string.IsNullOrWhiteSpace(HardwareHint) ? null : HardwareHint.Trim(),
            MaxParallel: int.Parse(MaxParallel),
            Type: Type,
            // Wire semantics: null = no change, "" = clear, value = set.
            ApiKey: ApiKeyWireValue(),
            Provider: string.IsNullOrWhiteSpace(Provider) ? null : Provider.Trim(),
            Models: updateModels,
            // Blank = null = NO CHANGE (clearing a previously set override is a documented
            // API limitation — editing config/config.json removes the key).
            PowerWatts: ParseWatts(),
            // Task #144 (maintainer decision 2026-11-20): EDIT — blank = "" (CLEAR per the
            // task #143 sshUser wire), value = set; sshPort blank = null (NO CHANGE — the
            // port wire cannot clear, the powerWatts precedent).
            SshUser: SshUserWireValue(),
            SshPort: ParseSshPort()), null);
    }

    private void ClearError()
    {
        if (ValidationErrorMessage is not null)
        {
            ValidationErrorMessage = null;
        }
    }

    private bool ValidateCommon(out string? error)
    {
        // Task #149: the shared name / URL / maxParallel / ssh-credential rules moved to
        // TargetFieldValidator (the dedicated per-type add dialogs reuse them); only the
        // powerWatts check stays local — it is the only form with that field.
        if (!TargetFieldValidator.ValidateName(Name, out error))
        {
            return false;
        }

        if (!TargetFieldValidator.ValidateUrl(Url, out error))
        {
            return false;
        }

        if (!TargetFieldValidator.ValidateMaxParallel(MaxParallel, out error))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(PowerWatts) &&
            (!double.TryParse(PowerWatts, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var watts) || watts < 0))
        {
            error = "powerWatts must be a non-negative number (or blank).";
            return false;
        }

        // Task #144: the daemon-side probe ssh credential (same charset the server enforces —
        // POSIX-ish, safe to interpolate into {user}@{host}).
        if (!TargetFieldValidator.ValidateSshUser(SshUser, out error))
        {
            return false;
        }

        if (!TargetFieldValidator.ValidateSshPort(SshPort, out error))
        {
            return false;
        }

        error = null;
        return true;
    }

    private string NormalizeUrl() => TargetFieldValidator.NormalizeUrl(Url);

    private double? ParseWatts() =>
        string.IsNullOrWhiteSpace(PowerWatts)
            ? null
            : double.Parse(PowerWatts, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The sshUser wire value: ADD mode — blank → null (unset); EDIT mode — blank →
    /// <c>""</c> (CLEAR per the task #143 <c>UpdateTargetRequest.SshUser</c> wire semantics),
    /// value → the value (validated by <see cref="ValidateCommon"/>).
    /// </summary>
    private string? SshUserWireValue()
    {
        var value = string.IsNullOrWhiteSpace(_sshUser) ? null : _sshUser;
        return _isEdit ? value ?? string.Empty : value;
    }

    /// <summary>The ssh port wire value: blank → null (ADD: unset; EDIT: NO CHANGE — the
    /// port wire cannot clear), else the parsed port (validated 1–65535).</summary>
    private int? ParseSshPort() => TargetFieldValidator.ParseSshPort(_sshPort);

    private IReadOnlyList<TargetModelDto>? BuildModels()
    {
        if (Models.Count == 0)
        {
            return null;
        }

        var entries = new List<TargetModelDto>(Models.Count);
        foreach (var entry in Models)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                ValidationErrorMessage = "Every roster model needs a name.";
                return null!;
            }

            int? contextLength = null;
            if (!string.IsNullOrWhiteSpace(entry.ContextLength))
            {
                if (!int.TryParse(entry.ContextLength, out var parsedContext) || parsedContext <= 0)
                {
                    ValidationErrorMessage = $"Model \"{entry.Name.Trim()}\": contextLength must be a positive integer (or blank).";
                    return null!;
                }

                contextLength = parsedContext;
            }

            entries.Add(new TargetModelDto(
                entry.Name.Trim(),
                contextLength,
                string.IsNullOrWhiteSpace(entry.Quantization) ? null : entry.Quantization.Trim()));
        }

        return entries;
    }

    /// <summary>
    /// The API key wire value in EDIT mode: null (no change) when the box is untouched,
    /// <c>""</c> (clear) when explicitly emptied, the value otherwise.
    /// </summary>
    private string? ApiKeyWireValue()
    {
        if (!_isEdit)
        {
            return null;
        }

        if (!_apiKeyTouched)
        {
            return null; // No change.
        }

        return string.IsNullOrEmpty(_apiKey) ? string.Empty : _apiKey; // "" = clear, else set.
    }
}
