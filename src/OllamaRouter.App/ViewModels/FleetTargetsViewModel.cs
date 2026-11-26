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
    /// decision 3; unified-memory correction task #164: unified profiles render
    /// <c>RAM: {…} (unified)</c> with no VRAM segment; null when the profile is unknown or
    /// every segment is 0). The unified flag comes from the profile itself — the
    /// profile-anchored source of truth for the hardware section (NOT the row-level
    /// <see cref="IsUnifiedMemory"/>, which is a fleet-endpoint projection).</summary>
    public string? HardwareMemoryLine =>
        HardwareProfile is { } profile
            ? FleetTargetDisplay.BuildMemoryLine(
                profile.SystemRamMB,
                profile.TotalFastMemoryMB,
                profile.EffectiveBandwidthGBs,
                profile.IsUnifiedMemory)
            : null;

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
    /// Creates the dedicated "Edit Ollama Node" form pre-filled from the row (task #171
    /// entry point — <see cref="Views.FleetTargetsView"/> routes every non-<c>openai</c>
    /// row to the <see cref="Views.Dialogs.EditOllamaNodeDialog"/>, incl. <c>osnative</c>
    /// and unknown types, whose protocol family the form never changes — D3).
    /// </summary>
    public OllamaNodeEditFormViewModel CreateOllamaNodeEditForm(FleetTargetRow row) =>
        new(row ?? throw new ArgumentNullException(nameof(row)));

    /// <summary>
    /// Creates the dedicated "Edit External Provider" form pre-filled from the row
    /// (task #171 entry point — <see cref="Views.FleetTargetsView"/> routes <c>openai</c>
    /// rows to the <see cref="Views.Dialogs.EditExternalProviderDialog"/>, case-insensitively).
    /// </summary>
    public ExternalProviderEditFormViewModel CreateExternalProviderEditForm(FleetTargetRow row) =>
        new(row ?? throw new ArgumentNullException(nameof(row)));

    /// <summary>
    /// Creates the dedicated "Add Ollama Node" form (task #149) for the fleet tab's
    /// "Add Ollama Node" toolbar button.
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
