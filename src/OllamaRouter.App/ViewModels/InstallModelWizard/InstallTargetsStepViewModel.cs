using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Services.Benchmarking;

namespace OllamaRouter.App.ViewModels.InstallModelWizard;

/// <summary>
/// One fleet-target row of Step 1 — Targets of the Install Model wizard (task #125): the
/// liveness + hardware summary (from <c>GET /api/router/fleet</c>), the already-present
/// badge (from <c>GET /api/router/cartography</c>), the pullability (Ollama-type targets
/// only — non-Ollama rows render disabled with a note), the selection checkbox and — in
/// assisted mode — the editable per-target context with the
/// <see cref="InstallAssistSuggester"/> badge + provenance line.
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
        bool assisted = false,
        string context = "",
        string? suggestionBadge = null,
        string? suggestionProvenance = null)
    {
        Name = name;
        Liveness = liveness;
        Enabled = enabled;
        OllamaType = ollamaType;
        HardwareText = hardwareText;
        Present = present;
        Assisted = assisted;
        _selected = selected;
        _context = context;
        _suggestionBadge = suggestionBadge;
        _suggestionProvenance = suggestionProvenance;
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

    /// <summary>The hardware summary line (e.g. "RTX 4090 · VRAM 24564 MB · 1.0 GB/s").</summary>
    public string HardwareText { get; }

    /// <summary>Whether the model is ALREADY present on this target (the cartography badge —
    /// the pull is a fast no-op in that case, the alias step still runs).</summary>
    public bool Present { get; }

    /// <summary>Whether the row is pullable at all (Ollama type; the server re-validates
    /// enabled/online with a fresh liveness probe at pull time).</summary>
    public bool Pullable => OllamaType;

    /// <summary>Whether the wizard ran in assisted mode (drives the per-target context input
    /// visibility in the row template).</summary>
    public bool Assisted { get; }

    /// <summary>The note for non-pullable rows (null for pullable rows).</summary>
    public string? Note => OllamaType ? null : "not pullable (OpenAI-compatible / OS-native)";

    /// <summary>The selection (drives the install plan; only pullable rows are selectable).</summary>
    [ObservableProperty]
    private bool _selected;

    /// <summary>The per-target context input (assisted mode; blank = invalid while the alias
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

    /// <summary>Whether the row's context parses to >= 1 (the assisted-mode gate per row).</summary>
    public bool HasValidContext => int.TryParse(Context, out var value) && value >= 1;

    partial void OnContextChanged(string value) => OnPropertyChanged(nameof(HasValidContext));

    partial void OnSelectedChanged(bool value) => OnPropertyChanged(nameof(HasValidContext));

    /// <summary>
    /// The effective context (tokens) of this row for the install plan: the parsed per-target
    /// input in assisted mode, else the shared manual value.
    /// </summary>
    public int? EffectiveContext(bool assisted, int? sharedContext) =>
        assisted ? (HasValidContext ? int.Parse(Context) : null) : sharedContext;
}

/// <summary>
/// Step 1 — Targets of the Install Model wizard (task #125): fetch-on-entry of the fleet
/// (liveness/hardware), the cartography (already-present badges), the config (target types)
/// and — assisted mode only — the benchmark results + fleet-catalog model detail (the
/// per-target context suggestions via the pure <see cref="InstallAssistSuggester"/>).
/// Manual mode: a single shared context input + the "create context alias" switch (default
/// ON) with the computed alias preview. Assisted mode: editable per-target contexts
/// prefilled from the assistance (user can override before confirming) and the default
/// selection of every ONLINE Ollama target. Gate: >= 1 selected target; valid context(s)
/// while the alias is ON.
/// </summary>
public sealed partial class InstallTargetsStepViewModel : ObservableObject, IWizardStep
{
    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private readonly Func<string> _modelReference;
    private readonly Func<bool> _assistedMode;
    private readonly List<InstallTargetRowVm> _subscribedRows = [];

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

    /// <summary>The shared context (tokens) input (manual mode; blank = invalid while the
    /// alias is ON).</summary>
    [ObservableProperty]
    private string _sharedContext = string.Empty;

    /// <summary>The computed alias preview of the manual mode (null when the alias is OFF or
    /// the context is invalid).</summary>
    [ObservableProperty]
    private string? _aliasPreview;

    /// <summary>Whether the assisted mode was active when the rows were built (drives the
    /// per-target context inputs' visibility).</summary>
    [ObservableProperty]
    private bool _assistedActive;

    /// <summary>The fetch error (null while healthy).</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Whether a fetch is in flight.</summary>
    [ObservableProperty]
    private bool _isFetching;

    /// <summary>The Next gate: >= 1 selected pullable target AND valid context(s) while the
    /// alias is ON.</summary>
    public bool IsValid =>
        Rows.Count > 0
        && Rows.Any(r => r.Pullable && r.Selected)
        && (!CreateAlias || ContextsValid());

    /// <summary>The human hint of a blocked Next (shown by the window).</summary>
    public string? NextGateHint =>
        Rows.Count == 0
            ? "No fleet targets to install on."
            : !Rows.Any(r => r.Pullable && r.Selected)
                ? "Select at least one Ollama target."
                : CreateAlias && !ContextsValid()
                    ? "Enter a context (tokens) of at least 1 for every selected target."
                    : null;

    private bool ContextsValid()
    {
        if (AssistedActive)
        {
            return Rows.Where(r => r.Pullable && r.Selected).All(r => r.HasValidContext);
        }

        return int.TryParse(SharedContext, out var value) && value >= 1;
    }

    /// <summary>
    /// The install plan of the selected targets (fleet order): (target, context-or-null,
    /// alias-or-null, already-present) — the review step renders it, the install loop
    /// executes it SEQUENTIALLY in this order.
    /// </summary>
    public List<(string Target, int? Context, string? Alias, bool Present)> BuildPlan()
    {
        var model = _modelReference();
        var assisted = AssistedActive;
        var shared = CreateAlias && int.TryParse(SharedContext, out var ctx) && ctx >= 1 ? (int?)ctx : null;
        var plan = new List<(string, int?, string?, bool)>();
        foreach (var row in Rows.Where(r => r.Pullable && r.Selected))
        {
            var context = CreateAlias ? row.EffectiveContext(assisted, shared) : null;
            plan.Add(InstallAssistSuggester.PlanTarget(row.Name, model, context, row.Present));
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
    /// </summary>
    public async Task OnStepEnteredAsync(CancellationToken ct = default)
    {
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
            foreach (var item in fleet.Targets)
            {
                if (string.Equals(item.Name, "Cloud", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the Cloud pseudo-target has no Ollama daemon to pull onto
                }

                var type = config.Targets
                    .FirstOrDefault(t => string.Equals(t.Name, item.Name, StringComparison.OrdinalIgnoreCase))
                    ?.Type ?? "ollama";
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
                    assisted: assisted,
                    context: context,
                    suggestionBadge: badge,
                    suggestionProvenance: provenance));
            }

            AssistedActive = assisted;
            UnsubscribeRows();
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            SubscribeRows();
        }
        catch (OllamaRouterApiException ex)
        {
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsFetching = false;
            RaisePlanChanged();
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
    /// Keeps the gate + hint fresh on ROW changes (a checkbox toggle or a context edit —
    /// the row's own property notifications alone would leave <see cref="IsValid"/> /
    /// <see cref="NextGateHint"/> stale on the parent).
    /// </summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstallTargetRowVm.Selected) or nameof(InstallTargetRowVm.Context)
            or nameof(InstallTargetRowVm.HasValidContext))
        {
            RaisePlanChanged();
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

    partial void OnCreateAliasChanged(bool value)
    {
        RecomputeAliasPreview();
        RaisePlanChanged();
    }

    partial void OnSharedContextChanged(string value)
    {
        RecomputeAliasPreview();
        RaisePlanChanged();
    }

    private void RecomputeAliasPreview()
    {
        var preview = CreateAlias
            && int.TryParse(SharedContext, out var context)
            && context >= 1
            && ModelPullNaming.TryComputeAliasName(_modelReference(), context, out var alias)
                ? alias
                : null;
        AliasPreview = preview;
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
