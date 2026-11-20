using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.App.ViewModels.InstallModelWizard;

/// <summary>
/// One fleet-target row of Step 1 — Targets of the Install Model wizard (task #125, unified
/// per-target inputs by task #131): the liveness + hardware summary (from
/// <c>GET /api/router/fleet</c>), the already-present badge (from
/// <c>GET /api/router/cartography</c>), the pullability (Ollama-type targets only —
/// non-Ollama rows render disabled with a note), the selection checkbox and — in BOTH manual
/// and assisted mode — the editable per-target quant/tag (prefilled with the step-0 model
/// reference's EXPLICIT tag) + context with the computed per-target alias hint and the
/// <see cref="InstallAssistSuggester"/> badge + provenance line (assisted mode).
/// </summary>
public sealed partial class InstallTargetRowVm : ObservableObject
{
    public InstallTargetRowVm(
        string name,
        string liveness,
        bool enabled,
        bool ollamaType,
        string hardwareText,
        bool present,
        bool selected,
        string modelReference,
        string context = "",
        string? suggestionBadge = null,
        string? suggestionProvenance = null,
        bool probeable = false)
    {
        Name = name;
        Liveness = liveness;
        Enabled = enabled;
        OllamaType = ollamaType;
        HardwareText = hardwareText;
        Present = present;
        _selected = selected;
        _context = context;
        _suggestionBadge = suggestionBadge;
        _suggestionProvenance = suggestionProvenance;
        _showReprobe = probeable;

        // The FULL step-0 reference, parsed ONCE (the step-0 gate guarantees validity — the
        // parse failure below is the impossible case): the tag prefill is the EXPLICIT tag
        // only (a tagless reference prefills empty, never "latest").
        BaseReference = modelReference.Trim();
        if (ModelPullNaming.TryParseModelReference(modelReference, out var parsedName, out var parsedTag))
        {
            BaseName = parsedName;
            _quantTag = parsedTag ?? string.Empty;
        }
        else
        {
            BaseName = modelReference;
            _quantTag = string.Empty;
        }
    }

    /// <summary>The target name (the wire <c>target</c> of the pull request).</summary>
    public string Name { get; }

    /// <summary>The liveness text (Online / Offline / Probing).</summary>
    public string Liveness { get; }

    /// <summary>Whether the target is enabled in the router config.</summary>
    public bool Enabled { get; }

    /// <summary>Whether the target speaks the Ollama protocol (pullable); non-Ollama targets
    /// are OpenAI-compatible / OS-native and can never be pulled.</summary>
    public bool OllamaType { get; }

    /// <summary>
    /// The hardware summary line (e.g. "RTX 4090 · VRAM 24564 MB · 1.0 GB/s"). Observable
    /// (task #144): the daemon-side ssh hardware probe updates the line IN PLACE after its
    /// completion — "Probing hardware…" while in flight, the formatted profile on success,
    /// the original unknown text on failure (fail-soft).
    /// </summary>
    [ObservableProperty]
    private string _hardwareText = string.Empty;

    /// <summary>Whether the model is ALREADY present on this target (the cartography badge —
    /// the pull is a fast no-op in that case, the alias step still runs).</summary>
    public bool Present { get; }

    /// <summary>Whether the row is pullable at all (Ollama type; the server re-validates
    /// enabled/online with a fresh liveness probe at pull time).</summary>
    public bool Pullable => OllamaType;

    /// <summary>The step-0 model reference as entered (trimmed; the tagless-fallback target
    /// of <see cref="EffectiveModelRef"/>).</summary>
    public string BaseReference { get; }

    /// <summary>The step-0 reference's NAME part (the per-target reference is composed as
    /// <c>{BaseName}:{QuantTag}</c>).</summary>
    public string BaseName { get; }

    /// <summary>The note for non-pullable rows (null for pullable rows).</summary>
    public string? Note => OllamaType ? null : "not pullable (OpenAI-compatible / OS-native)";

    /// <summary>The selection (drives the install plan; only pullable rows are selectable).</summary>
    [ObservableProperty]
    private bool _selected;

    /// <summary>The per-target quant/tag input (BOTH modes; prefilled with the step-0
    /// reference's EXPLICIT tag, blank = pull the step-0 reference as-is).</summary>
    [ObservableProperty]
    private string _quantTag;

    /// <summary>The per-target context input (BOTH modes; blank = invalid while the alias
    /// is ON).</summary>
    [ObservableProperty]
    private string _context;

    /// <summary>The "Suggested: {n}" badge (assisted mode; the user may override the input —
    /// the suggestion never locks it).</summary>
    [ObservableProperty]
    private string? _suggestionBadge;

    /// <summary>The suggestion provenance line (benchmark / fleet catalog / hint / veto).</summary>
    [ObservableProperty]
    private string? _suggestionProvenance;

    /// <summary>Whether the row's context parses to >= 1 (the per-row gate while the alias is
    /// ON).</summary>
    public bool HasValidContext => int.TryParse(Context, out var value) && value >= 1;

    /// <summary>Whether the row's composed model reference is valid: an EMPTY tag falls back
    /// to the step-0 reference (valid by the step-0 gate); else the composed
    /// <c>BaseName:tag</c> must parse with the same <see cref="ModelPullNaming"/> rules the
    /// server enforces.</summary>
    public bool HasValidModelRef =>
        string.IsNullOrWhiteSpace(QuantTag)
        || ModelPullNaming.TryParseModelReference($"{BaseName}:{QuantTag.Trim()}", out _, out _);

    /// <summary>The per-row quant/tag hint (null while valid).</summary>
    public string? ModelRefError => HasValidModelRef
        ? null
        : $"invalid quant/tag '{QuantTag.Trim()}' — use letters, digits, '.', '_' or '-' (e.g. q4_K_M)";

    /// <summary>The per-target model reference of the pull plan: the composed
    /// <c>BaseName:tag</c>, else the step-0 reference as-is (validity =
    /// <see cref="HasValidModelRef"/>).</summary>
    public string EffectiveModelRef =>
        string.IsNullOrWhiteSpace(QuantTag) ? BaseReference : $"{BaseName}:{QuantTag.Trim()}";

    /// <summary>The parent-driven alias hint chip text (set by the step view model —
    /// <c>ComputedAlias</c> while the alias is ON, null otherwise; the row never reads the
    /// switch itself).</summary>
    [ObservableProperty]
    private string? _aliasHint;

    /// <summary>Whether a daemon-side hardware probe is in flight for this row (task #144
    /// busy state of the per-row re-probe affordance).</summary>
    [ObservableProperty]
    private bool _probingHardware;

    /// <summary>
    /// Whether the per-row re-probe affordance is shown (task #144): seeded true when the
    /// row is ssh-probeable with UNKNOWN hardware; flips to false after a SUCCESSFUL probe
    /// (auto or manual); a FAILED probe keeps it true so the user can retry manually.
    /// </summary>
    [ObservableProperty]
    private bool _showReprobe;

    /// <summary>Whether the re-probe action is available (not already in flight).</summary>
    public bool CanReprobe => !ProbingHardware;

    /// <summary>The transient content of the re-probe button (busy state).</summary>
    public string ReprobeButtonContent => ProbingHardware ? "Probing…" : "Probe";

    partial void OnProbingHardwareChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReprobe));
        OnPropertyChanged(nameof(ReprobeButtonContent));
    }

    /// <summary>The computed per-target context alias from THIS row's own reference +
    /// context (pure, independent of the alias switch; null while invalid/uncomputable).</summary>
    public string? ComputedAlias =>
        HasValidModelRef && HasValidContext &&
        ModelPullNaming.TryComputeAliasName(EffectiveModelRef, int.Parse(Context), out var alias)
            ? alias
            : null;

    partial void OnQuantTagChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidModelRef));
        OnPropertyChanged(nameof(ModelRefError));
        OnPropertyChanged(nameof(EffectiveModelRef));
        OnPropertyChanged(nameof(ComputedAlias));
    }

    partial void OnContextChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidContext));
        OnPropertyChanged(nameof(EffectiveContext));
        OnPropertyChanged(nameof(ComputedAlias));
    }

    partial void OnSelectedChanged(bool value) => OnPropertyChanged(nameof(HasValidContext));

    /// <summary>
    /// The effective context (tokens) of this row for the install plan (the parsed
    /// per-target input in BOTH modes — null while invalid).
    /// </summary>
    public int? EffectiveContext => HasValidContext ? int.Parse(Context) : null;
}

/// <summary>
/// Step 1 — Targets of the Install Model wizard (task #125, manual/assisted UNIFIED by
/// task #131): fetch-on-entry of the fleet (liveness/hardware), the cartography
/// (already-present badges), the config (target types) and — assisted mode only — the
/// benchmark results + fleet-catalog model detail (the per-target context suggestions via
/// the pure <see cref="InstallAssistSuggester"/>).
/// Every pullable row gets its OWN quant/tag (prefilled with the step-0 reference's
/// EXPLICIT tag) + context input in BOTH modes; the "create context alias" switch (default
/// ON) gates the per-row completeness. Gate: >= 1 selected target; valid tag + context +
/// computable alias per SELECTED row while the alias is ON.
/// Re-entrant fetch-on-entry is LATEST-WINS (task #130): a re-entrant entry while a
/// previous fetch is in flight starts its own fetch; only the newest entry's result is
/// ever applied, so overlapping entries can never duplicate rows or clobber the latest
/// response.
/// </summary>
public sealed partial class InstallTargetsStepViewModel : ObservableObject, IWizardStep
{
    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private readonly Func<string> _modelReference;
    private readonly Func<bool> _assistedMode;
    private readonly List<InstallTargetRowVm> _subscribedRows = [];

    /// <summary>The transient hardware line of a row while its daemon-side ssh probe is in
    /// flight (task #144).</summary>
    private const string ProbingHardwareText = "Probing hardware…";

    /// <summary>The latest-wins fetch generation (task #130): every <see cref="OnStepEnteredAsync"/>
    /// entry increments it; a fetch finishing after a newer entry started is superseded and
    /// discards its result (it must not touch Rows / Error / IsFetching / the gate).</summary>
    private int _fetchGeneration;

    public InstallTargetsStepViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<string> modelReference,
        Func<bool> assistedMode)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _modelReference = modelReference ?? throw new ArgumentNullException(nameof(modelReference));
        _assistedMode = assistedMode ?? throw new ArgumentNullException(nameof(assistedMode));
    }

    /// <summary>The target rows (fleet order; non-Ollama rows disabled).</summary>
    public ObservableCollection<InstallTargetRowVm> Rows { get; } = [];

    /// <summary>The "create context alias" switch (default ON — the context input is only
    /// required while it is ON).</summary>
    [ObservableProperty]
    private bool _createAlias = true;

    /// <summary>The fetch error (null while healthy).</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Whether a fetch is in flight.</summary>
    [ObservableProperty]
    private bool _isFetching;

    /// <summary>The Next gate: >= 1 selected pullable target AND — while the alias is ON —
    /// every selected row has a valid quant/tag, a valid context and a computable alias.
    /// </summary>
    public bool IsValid =>
        Rows.Count > 0
        && Rows.Any(r => r.Pullable && r.Selected)
        && (!CreateAlias || SelectedRowsComplete());

    /// <summary>The human hint of a blocked Next (shown by the window).</summary>
    public string? NextGateHint =>
        Rows.Count == 0
            ? "No fleet targets to install on."
            : !Rows.Any(r => r.Pullable && r.Selected)
                ? "Select at least one Ollama target."
                : CreateAlias && Rows.Any(r => r.Pullable && r.Selected && !r.HasValidModelRef)
                    ? "Enter a valid quant/tag (e.g. q4_K_M) for every selected target."
                    : CreateAlias && Rows.Any(r => r.Pullable && r.Selected && !r.HasValidContext)
                        ? "Enter a context (tokens) of at least 1 for every selected target."
                        : CreateAlias && Rows.Any(r => r.Pullable && r.Selected && r.ComputedAlias is null)
                            ? "The computed context alias would collide with the model reference on a selected target."
                            : null;

    private bool SelectedRowsComplete() =>
        Rows.Where(r => r.Pullable && r.Selected)
            .All(r => r.HasValidModelRef && r.HasValidContext && r.ComputedAlias is not null);

    /// <summary>
    /// The install plan of the selected targets (fleet order): (target, per-target model
    /// reference, context-or-null, alias-or-null, already-present) — the review step renders
    /// it, the install loop executes it SEQUENTIALLY in this order (one pull per target with
    /// THAT target's own reference + alias context).
    /// </summary>
    public List<(string Target, string Model, int? Context, string? Alias, bool Present)> BuildPlan()
    {
        var plan = new List<(string, string, int?, string?, bool)>();
        foreach (var row in Rows.Where(r => r.Pullable && r.Selected))
        {
            var context = CreateAlias ? row.EffectiveContext : null;
            plan.Add(InstallAssistSuggester.PlanTarget(row.Name, row.EffectiveModelRef, context, row.Present));
        }

        return plan;
    }

    /// <summary>
    /// Fetch-on-entry (setup-wizard style, try/catch to hint text): the fleet + cartography +
    /// config are REQUIRED (a failure empties the rows and the gate blocks); the benchmark
    /// results + fleet-catalog model detail are BEST-EFFORT (their absence only loses the
    /// suggestions / ceilings — a 404 model detail is the NORMAL "not in the fleet yet"
    /// case). Assisted mode prefills the per-target contexts from the assistance and
    /// preselects every ONLINE Ollama target; manual mode leaves the selection to the user.
    /// Re-entrancy (task #130): the wizard fires the step lifecycle fire-and-forget, so a
    /// rapid Back→Next burst can start a SECOND fetch while the first is still in flight.
    /// Every entry claims its own fetch generation (latest-wins); a superseded fetch — one
    /// that finishes after a newer entry has started, in ANY completion order — discards its
    /// whole result, so <see cref="Rows"/> always reflects the LATEST entry's response and a
    /// target never appears twice. Each row name is additionally deduplicated
    /// (case-insensitive, first occurrence wins) as defense in depth against a duplicated
    /// wire payload.
    /// </summary>
    public async Task OnStepEnteredAsync(CancellationToken ct = default)
    {
        // Claim this entry's generation BEFORE any state write or await: a newer entry
        // increments the field, and any superseded fetch observes the mismatch after its
        // last await and bails without touching shared state.
        var generation = Interlocked.Increment(ref _fetchGeneration);

        IsFetching = true;
        Error = null;
        try
        {
            var model = _modelReference();
            var assisted = _assistedMode();
            var client = _clientAccessor();

            var fleet = await client.GetFleetAsync(ct).ConfigureAwait(false);
            var cartography = await client.GetCartographyAsync(ct).ConfigureAwait(false);
            var config = await client.GetConfigAsync(ct).ConfigureAwait(false);

            // Task #144: the hardware profiles are fetched BEST-EFFORT (their absence only
            // loses the auto-population — the fleet rows still render their hardware line via
            // the fleet endpoint). A FAILED fetch means "unknown state unknown" → NO
            // auto-probes this entry (the step fetch must never fail on it).
            RouterFleetHardwareResponse? hardware = null;
            try
            {
                hardware = await client.GetFleetHardwareAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort: see above — no auto-probes this entry.
            }

            var hardwareByName = hardware is null
                ? null
                : hardware.Nodes.ToDictionary(n => n.Target, StringComparer.OrdinalIgnoreCase);

            RouterBenchmarkResultsResponse? benchmarks = null;
            try
            {
                benchmarks = await client.GetBenchmarkResultsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort: no benchmark data → the catalog/hint suggestions still apply.
            }

            RouterModelDetailResponse? detail = null;
            try
            {
                detail = await client.GetModelDetailAsync(model, ct).ConfigureAwait(false);
            }
            catch (OllamaRouterApiException)
            {
                // Best-effort: a 404 (model not in the fleet yet) is the NORMAL first-install
                // case — no per-target ceilings for the assistance.
            }

            var modelRow = cartography.Models.FirstOrDefault(m =>
                string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase) ||
                m.Name.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase));

            var rows = new List<InstallTargetRowVm>(fleet.Targets.Count);
            var emittedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in fleet.Targets)
            {
                if (string.Equals(item.Name, "Cloud", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the Cloud pseudo-target has no Ollama daemon to pull onto
                }

                // Defense in depth (task #130): a malformed/duplicated wire payload must still
                // yield exactly one row per target name (keep the first occurrence) — the
                // BuildPlan() uniqueness then inherits from the row uniqueness.
                if (!emittedNames.Add(item.Name))
                {
                    continue;
                }

                // Task #144: resolve the config target ONCE (type AND ssh credential come
                // from it; the lookup is reused for both).
                var configTarget = config.Targets.FirstOrDefault(t =>
                    string.Equals(t.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                var type = configTarget?.Type ?? "ollama";
                var ollamaType = string.Equals(type, "ollama", StringComparison.OrdinalIgnoreCase);

                var deployment = modelRow?.Deployments
                    .FirstOrDefault(d => string.Equals(d.Target, item.Name, StringComparison.OrdinalIgnoreCase));

                var benchmarkRows = benchmarks?.Targets
                    .FirstOrDefault(t => string.Equals(t.Target, item.Name, StringComparison.OrdinalIgnoreCase))?
                    .Results
                    .Where(r => string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var catalogRow = detail?.Targets
                    .FirstOrDefault(t => string.Equals(t.Target, item.Name, StringComparison.OrdinalIgnoreCase));

                string? badge = null;
                string? provenance = null;
                var context = string.Empty;
                if (assisted)
                {
                    var suggestion = InstallAssistSuggester.SuggestContext(
                        item.Name,
                        benchmarkRows,
                        catalogRow?.Capable ?? false,
                        catalogRow?.ContextCeiling,
                        catalogRow?.VetoReason);
                    badge = suggestion.Context is { } suggested ? $"Suggested: {suggested}" : null;
                    provenance = suggestion.Provenance;
                    context = suggestion.Context is { } prefilled ? prefilled.ToString() : string.Empty;
                }

                rows.Add(new InstallTargetRowVm(
                    item.Name,
                    item.Liveness,
                    item.Enabled,
                    ollamaType,
                    FormatHardware(item),
                    deployment?.Present ?? false,
                    selected: assisted && ollamaType &&
                        string.Equals(item.Liveness, "Online", StringComparison.OrdinalIgnoreCase),
                    modelReference: model,
                    context: context,
                    suggestionBadge: badge,
                    suggestionProvenance: provenance,
                    probeable: hardwareByName is not null &&
                        (hardwareByName.TryGetValue(item.Name, out var hardwareNode) is false ||
                         string.Equals(hardwareNode.Source, "none", StringComparison.OrdinalIgnoreCase) ||
                         hardwareNode.Profile is null) &&
                        ollamaType &&
                        !string.Equals(item.Location, "local", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(configTarget?.SshUser)));
            }

            // Superseded (a newer entry has started): discard the WHOLE result — Rows,
            // Error, IsFetching and the gate stay owned by the latest entry.
            if (generation != Volatile.Read(ref _fetchGeneration))
            {
                return;
            }

            UnsubscribeRows();
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            SubscribeRows();

            // Task #144 auto-probe pass — AFTER the latest-wins check above, so a
            // superseded entry never reaches it. One fire-and-forget daemon-side probe per
            // eligible row (at most one per step entry per row — NO retry storm). The probe
            // is server-bounded at ~30 s and must never block this entry's completion
            // (IsFetching settles with the probes still in flight).
            foreach (var row in Rows)
            {
                if (row.ShowReprobe && !row.ProbingHardware)
                {
                    var originalText = row.HardwareText;
                    row.HardwareText = ProbingHardwareText;
                    row.ProbingHardware = true;
                    _ = ProbeRowAsync(row, generation, originalText);
                }
            }
        }
        catch (OllamaRouterApiException ex)
        {
            // Superseded failures are discarded (an older error must not overwrite the
            // latest entry's state).
            if (generation == Volatile.Read(ref _fetchGeneration))
            {
                Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
            }
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _fetchGeneration))
            {
                Error = ex.Message;
            }
        }
        finally
        {
            // Teardown runs only for the LATEST generation — IsFetching stays true until the
            // latest fetch settles (an older in-flight fetch finishing first must not clear
            // it while the newer fetch still runs).
            if (generation == Volatile.Read(ref _fetchGeneration))
            {
                IsFetching = false;
                RefreshAliasHints();
                RaisePlanChanged();
            }
        }
    }

    /// <inheritdoc />
    public Task OnStepLeftAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        UnsubscribeRows();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Keeps the gate + hint fresh on ROW changes (a checkbox toggle, a quant/tag edit or a
    /// context edit — the row's own property notifications alone would leave <see cref="IsValid"/>
    /// / <see cref="NextGateHint"/> stale on the parent) and re-sets that row's alias hint.
    /// NOTE: <see cref="InstallTargetRowVm.AliasHint"/> is deliberately NOT in the reacted set
    /// (it is set BY this handler — reacting to it would re-enter forever).
    /// </summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstallTargetRowVm.Selected) or nameof(InstallTargetRowVm.QuantTag)
            or nameof(InstallTargetRowVm.Context)
            or nameof(InstallTargetRowVm.HasValidContext))
        {
            RaisePlanChanged();
            if (sender is InstallTargetRowVm row)
            {
                row.AliasHint = CreateAlias ? row.ComputedAlias : null;
            }
        }
    }

    private void SubscribeRows()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            _subscribedRows.Add(row);
        }
    }

    private void UnsubscribeRows()
    {
        foreach (var row in _subscribedRows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        _subscribedRows.Clear();
    }

    private static string FormatHardware(RouterTargetItem item)
    {
        var parts = new List<string>();
        var gpu = item.Hardware?.GpuName;
        parts.Add(string.IsNullOrWhiteSpace(gpu) ? "CPU" : gpu);
        if (item.Hardware?.MaxVramMB is { } vram)
        {
            parts.Add($"VRAM {vram} MB");
        }

        if (item.Hardware?.BandwidthGBs is { } bandwidth and > 0)
        {
            parts.Add($"BW {bandwidth:0.#} GB/s");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Formats a daemon-side hardware profile into the same one-line summary shape as
    /// <see cref="FormatHardware(RouterTargetItem)"/> (GPU · VRAM · BW): the first non-blank
    /// GPU model name (else "CPU"), the total fast memory when > 0, the effective
    /// bandwidth when > 0.
    /// </summary>
    private static string FormatHardwareProfile(NodeHardwareProfile profile)
    {
        var parts = new List<string>();
        var gpu = profile.Gpus.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.ModelName))?.ModelName;
        parts.Add(string.IsNullOrWhiteSpace(gpu) ? "CPU" : gpu);
        if (profile.TotalFastMemoryMB > 0)
        {
            parts.Add($"VRAM {profile.TotalFastMemoryMB} MB");
        }

        if (profile.EffectiveBandwidthGBs > 0)
        {
            parts.Add($"BW {profile.EffectiveBandwidthGBs:0.#} GB/s");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The daemon-side hardware probe of one row (task #144): runs to completion on the
    /// thread pool with <see cref="CancellationToken.None"/> — the probe is server-bounded at
    /// ~30 s and settles long after the step entry. LATEST-WINS (task #130): the generation
    /// check runs BEFORE the first row write, so a superseded entry's probe result is
    /// discarded WITHOUT touching any row. Success → the row's hardware line is replaced IN
    /// PLACE by the formatted profile and the re-probe affordance disappears; failure → the
    /// line reverts to the original unknown text (fail-soft: NO <see cref="Error"/>, NO gate
    /// impact, the re-probe affordance stays for a manual retry).
    /// </summary>
    private async Task ProbeRowAsync(InstallTargetRowVm row, int generation, string originalText)
    {
        NodeHardwareProfile? profile = null;
        try
        {
            profile = await _clientAccessor().ProbeTargetHardwareAsync(row.Name, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fail-soft by design: the exception is swallowed — the row degrades back to the
            // original unknown text and the manual re-probe affordance remains.
        }

        // A superseded entry's probe result never touches Rows/Error — return BEFORE any
        // row write (the guard applies to probe completions, not only fetches).
        if (generation != Volatile.Read(ref _fetchGeneration))
        {
            return;
        }

        await RunOnUiAsync(() =>
        {
            if (profile is not null)
            {
                row.HardwareText = FormatHardwareProfile(profile);
                row.ProbingHardware = false;
                row.ShowReprobe = false;
            }
            else
            {
                row.HardwareText = originalText;
                row.ProbingHardware = false;
                // ShowReprobe stays true: the manual re-probe affordance remains.
            }
        });
    }

    /// <summary>
    /// The per-row MANUAL re-probe affordance (task #144, unprobed rows): re-runs the
    /// daemon-side hardware probe with the CURRENT fetch generation (if a newer step entry
    /// starts meanwhile, its fetch re-reads everything and the stale result is discarded by
    /// the latest-wins check in <see cref="ProbeRowAsync"/>). No-op for a null/stale row or a
    /// probe already in flight.
    /// </summary>
    [RelayCommand]
    public async Task ReProbeHardwareAsync(InstallTargetRowVm? row, CancellationToken ct = default)
    {
        if (row is null || !Rows.Contains(row) || row.ProbingHardware)
        {
            return;
        }

        var originalText = row.HardwareText;
        row.HardwareText = ProbingHardwareText;
        row.ProbingHardware = true;
        _ = ProbeRowAsync(row, Volatile.Read(ref _fetchGeneration), originalText);
    }

    /// <summary>
    /// Marshals <paramref name="action"/> to the UI thread (inline when there is no Avalonia
    /// application — the tests — or the caller is already on the UI thread). Used ONLY by
    /// the probe-completion path: probes settle ~30 s later on a thread-pool thread and
    /// must not rely on the fetch path's no-dispatch convention.
    /// </summary>
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

    partial void OnCreateAliasChanged(bool value)
    {
        RefreshAliasHints();
        RaisePlanChanged();
    }

    /// <summary>
    /// (Re-)sets every row's parent-driven alias hint from the current alias switch state:
    /// the row's own <see cref="InstallTargetRowVm.ComputedAlias"/> while the alias is ON,
    /// null otherwise.
    /// </summary>
    private void RefreshAliasHints()
    {
        foreach (var row in Rows)
        {
            row.AliasHint = CreateAlias ? row.ComputedAlias : null;
        }
    }

    private void RaisePlanChanged()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(NextGateHint));
    }

    /// <summary>
    /// Re-raises the plan/gate state (the window re-queries it after step lifecycle calls;
    /// row selection/context changes raise it through the row's own property notifications +
    /// this hook).
    /// </summary>
    public void RefreshGate() => RaisePlanChanged();
}
