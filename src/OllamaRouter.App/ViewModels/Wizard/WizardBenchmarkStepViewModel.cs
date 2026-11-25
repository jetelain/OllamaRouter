using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Logging;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using Serilog;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// One (target × model) candidate row of Step 4 (task #152): a catalog candidate of one
/// per-target hardware group, its class/offload/size annotation, its roles, the pre-flight
/// fit verdict text and the user's selection checkbox (default selected EXCEPT pre-flight
/// Pruned candidates — task #169: they start unchecked instead of pre-checking a 70+ GB
/// model on an 8 GB node; fully user-toggleable either way — the run-scoped opt-in/opt-out
/// state, never persisted).
/// Changing <see cref="IsSelected"/> re-evaluates the owning step's Run command
/// (CanExecute = at least one candidate model selected).
/// </summary>
public sealed partial class BenchmarkCandidateRow : ObservableObject
{
    private readonly Action _selectionChanged;

    public BenchmarkCandidateRow(
        string target,
        string model,
        string classText,
        string rolesText,
        string fitText,
        Action selectionChanged,
        bool isSelected = true,
        bool isPruned = false)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        ClassText = classText;
        RolesText = rolesText;
        FitText = fitText;
        _selectionChanged = selectionChanged ?? throw new ArgumentNullException(nameof(selectionChanged));
        _isSelected = isSelected;
        IsPruned = isPruned;
    }

    /// <summary>The target (group) this candidate is tested on.</summary>
    public string Target { get; }

    /// <summary>The catalog candidate model name.</summary>
    public string Model { get; }

    /// <summary>Whether pre-flight sizing marked this candidate as pruned (does not fit).</summary>
    public bool IsPruned { get; }

    /// <summary>
    /// The class/offload/size annotation (task #169, display only):
    /// "{modelClass} · {CPU offload OK|VRAM only}" + " · ~{n.n} GB" (the curated sizing
    /// anchor in GB, 1 decimal, invariant culture) — the size segment is omitted when the
    /// model has no curated anchor (null).
    /// </summary>
    public string ClassText { get; }

    /// <summary>
    /// The candidate roles (display only): "All roles (primary: X)" when the catalog entry
    /// covers all five roles (primary = Roles[0] per the #166 catalog convention),
    /// otherwise the comma-joined role names in catalog order (primary first).
    /// </summary>
    public string RolesText { get; }

    /// <summary>
    /// The pre-flight fit verdict (display only): "ctx ≤ N" (Fits — the largest ladder
    /// context that fits this node), "won't fit this node" (Pruned — the run records
    /// sizing-pruned rows and never pulls the model) or "no profile (assumed)" (Assumed —
    /// fail-open, the benchmark proceeds and an OOM is the empirical safety net).
    /// </summary>
    public string FitText { get; }

    /// <summary>
    /// Whether the model is selected for the next run (default: selected — EXCEPT pre-flight
    /// Pruned candidates, which start unchecked, task #169).
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}

/// <summary>
/// One per-target candidate group of Step 4 (task #152): the target name, its STORED
/// hardware annotation (GPU name, effective fast memory, bandwidth — task #164: the
/// fast-memory string replaced the first-GPU VRAM string) plus a note when the store has
/// no profile, and the candidate model rows (all default selected except pre-flight Pruned
/// candidates — task #169).
/// </summary>
public sealed class BenchmarkCandidateGroupRow
{
    public required string Target { get; init; }
    public string GpuName { get; init; } = string.Empty;
    /// <summary>
    /// The effective FAST-memory annotation (task #164, 2026-11-22): <c>Fast memory: {value}</c>
    /// from the stored profile's <c>TotalFastMemoryMB</c> — system RAM for unified nodes,
    /// summed GPU VRAM for discrete ones — i.e. exactly the budget the candidate-sizing
    /// (dense fit) computation consumes; empty string when the store has no profile or the
    /// value is 0. (Property name kept as VramText to avoid XAML-binding churn — see task #164.)
    /// </summary>
    public string VramText { get; init; } = string.Empty;
    public string BandwidthText { get; init; } = string.Empty;
    public string? Note { get; init; }
    public required IReadOnlyList<BenchmarkCandidateRow> Models { get; init; }
}

/// <summary>
/// One target's benchmark-run row of Step 4 (task #103): the lifecycle status and the
/// start/finish stamps.
/// </summary>
public sealed class BenchmarkRow
{
    public required string Target { get; init; }
    public BenchmarkRunStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    public string StatusText => Status.ToString();
}

/// <summary>
/// One (target × model × context) result row of Step 4 (task #103): the measured decode
/// throughput, the viability decision (≥ <c>20</c> t/s) and the non-viable reason.
/// </summary>
public sealed class BenchmarkResultRow
{
    public required string Target { get; init; }
    public required string Model { get; init; }
    public string Role { get; init; } = string.Empty;
    public int NumCtx { get; init; }
    public double DecodeTokensPerSecond { get; init; }
    public bool Viable { get; init; }
    public string? Reason { get; init; }

    public string ThroughputText => $"{DecodeTokensPerSecond:0.0} t/s";
    public string ContextText => $"ctx {NumCtx}";
}

/// <summary>
/// Step 4 — Micro-Benchmark (task #103, spec §3.5; renumbered by task #151): the candidate
/// model plan grouped by target (task #152 — the per-target hardware, the full catalog
/// candidate set with per-model fit verdicts and the run-scoped opt-out checkboxes), the
/// benchmark trigger (<c>POST /api/router/benchmark</c>; the unchecked models travel as the
/// run-scoped <c>excludedModels</c> map, null request when nothing is unchecked) and the
/// live 2 s results poll (per-context tokens/s + viability), self-stopping when every
/// queued target is terminal.
/// </summary>
public sealed partial class WizardBenchmarkStepViewModel : ObservableObject, IWizardStep
{
    /// <summary>The viability floor (mirrors
    /// <c>CandidateModelCatalogOptions.MinViableTokensPerSecond</c>) for the display decision.</summary>
    internal const double MinViableTokensPerSecond = 20.0;

    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private readonly DashboardPoller _poller;
    // Single-writer gate (task #130 addendum): the poller tick (thread pool) and the command-
    // side refresh (UI thread) both rebuild CandidateGroups/BenchmarkRows/ResultRows via
    // Clear()/Add; without serialization they interleave ("Collection was modified") — the
    // same bug class as the Install Model wizard row duplication. Same pattern as
    // RouterTabViewModel._refreshGate.
    // Task #163: on TOP of the gate, EVERY rebuild additionally runs on the UI thread via
    // RunOnUiAsync INSIDE the gate (the FleetTargetsViewModel pattern) — the gate alone
    // cannot stop the UI thread's ItemsControl from enumerating while a thread-pool tick
    // mutates it (missed Clear()s accumulate duplicate rows).
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private List<string> _queuedTargets = [];
    // Task #152 (D7): the candidate plan is fetched ONCE per step entry (the first tick of
    // the entry, or lazily by a direct RunBenchmarks execution) and NEVER re-fetched while
    // the user stays on the step — the unchecked boxes are the run-scoped exclusion state
    // and must survive the 2 s poll. A fresh entry resets the flag (re-default all selected).
    private bool _candidatesLoaded;
    // Task #159 (D8): the daemon-reachability transition gate for the 2 s benchmark poll
    // (null = not yet observed) — identical anti-spam semantics to WizardTargetsStepViewModel
    // (plain benign single-word bool; worst case one duplicate transition log under a race).
    private bool? _daemonReachable;
    // Task #159: the resolved logger (the seam defaults to the static Serilog.Log.Logger).
    private readonly Serilog.ILogger _logger;
    private bool _disposed;

    public WizardBenchmarkStepViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<CancellationToken, Task> pollerTick,
        Func<TimeSpan, CancellationToken, Task>? pollerDelay = null,
        Serilog.ILogger? logger = null)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _logger = logger ?? Serilog.Log.Logger;
        _poller = new DashboardPoller(pollerTick ?? throw new ArgumentNullException(nameof(pollerTick)),
            TimeSpan.FromSeconds(2), pollerDelay);
    }

    /// <summary>
    /// The per-target candidate groups (task #152): the step's single hardware + candidate
    /// source of truth (the pre-#152 standalone "Node hardware" table is removed — Q2).
    /// </summary>
    public ObservableCollection<BenchmarkCandidateGroupRow> CandidateGroups { get; } = [];

    /// <summary>One row per benchmarked target (status + stamps).</summary>
    public ObservableCollection<BenchmarkRow> BenchmarkRows { get; } = [];

    /// <summary>The per-(target × model × context) result rows (all targets, last results).</summary>
    public ObservableCollection<BenchmarkResultRow> ResultRows { get; } = [];

    /// <summary>The targets queued by the last <c>RunBenchmarks</c> (drives the self-stop).</summary>
    public IReadOnlyList<string> QueuedTargets => _queuedTargets;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool HasCandidates => CandidateGroups.Count > 0;

    public bool HasBenchmarkRows => BenchmarkRows.Count > 0;

    public bool HasResultRows => ResultRows.Count > 0;

    public bool IsBenchmarkRunning => BenchmarkRows.ToList().Any(r => !IsTerminal(r.Status));

    public double ProgressPercent
    {
        get
        {
            var snapshot = BenchmarkRows.ToList();
            if (_queuedTargets.Count > 0)
            {
                var done = _queuedTargets.Count(name =>
                    snapshot.FirstOrDefault(r => string.Equals(r.Target, name, StringComparison.OrdinalIgnoreCase)) is { } row
                    && IsTerminal(row.Status));
                return (done * 100.0) / _queuedTargets.Count;
            }

            if (snapshot.Count > 0)
            {
                var done = snapshot.Count(r => IsTerminal(r.Status));
                return (done * 100.0) / snapshot.Count;
            }

            return 0.0;
        }
    }

    public string ProgressSummaryText
    {
        get
        {
            var snapshot = BenchmarkRows.ToList();
            if (_queuedTargets.Count > 0)
            {
                var done = _queuedTargets.Count(name =>
                    snapshot.FirstOrDefault(r => string.Equals(r.Target, name, StringComparison.OrdinalIgnoreCase)) is { } row
                    && IsTerminal(row.Status));
                return $"{done} / {_queuedTargets.Count} target(s) finished";
            }

            if (snapshot.Count > 0)
            {
                var done = snapshot.Count(r => IsTerminal(r.Status));
                return done == snapshot.Count
                    ? $"{snapshot.Count} target(s) finished"
                    : $"{done} / {snapshot.Count} target(s) finished";
            }

            return string.Empty;
        }
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Selects all candidates that fit (unchecks pre-flight pruned models).</summary>
    [RelayCommand(CanExecute = nameof(CanSelectCandidates))]
    private void SelectFittingOnly()
    {
        foreach (var group in CandidateGroups.ToList())
        {
            foreach (var model in group.Models)
            {
                model.IsSelected = !model.IsPruned;
            }
        }
        NotifyRunCanExecute();
    }

    /// <summary>Unchecks all candidate models.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectCandidates))]
    private void SelectNone()
    {
        foreach (var group in CandidateGroups.ToList())
        {
            foreach (var model in group.Models)
            {
                model.IsSelected = false;
            }
        }
        NotifyRunCanExecute();
    }

    private bool CanSelectCandidates() => !IsBusy && CandidateGroups.Count > 0;

    /// <summary>
    /// Queues a benchmark run over the SELECTED candidate models (task #152): the unchecked
    /// boxes become the per-target <c>excludedModels</c> map of <c>POST /api/router/benchmark</c>
    /// (run-scoped — skipped for that run only, never persisted); when nothing is unchecked
    /// the request is null = the byte-identical absent-body POST of the pre-#152 flow.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunBenchmarks()
    {
        IsBusy = true;
        try
        {
            _logger.Information("Wizard step 4: benchmark run started");
            // 1. Lazy load (direct command execution without a prior tick). Under the
            // single-writer gate so the group rebuild never interleaves with a poller-tick
            // rebuild; the gate wait is deliberately NON-CANCELLABLE (existing convention).
            if (!_candidatesLoaded)
            {
                await _refreshGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_candidatesLoaded &&
                        !await LoadCandidatesAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        return; // Error already set — the plan is still unavailable.
                    }
                }
                finally
                {
                    _refreshGate.Release();
                }
            }

            // 2. Snapshot the groups and compute the per-target exclusion map (single-writer
            // gate, non-cancellable wait). Only targets with ≥ 1 unchecked model appear in
            // the map; an empty map → the all-selected null request below (D5).
            Dictionary<string, IReadOnlyList<string>>? excluded = null;
            var hasSelected = false;
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                excluded = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in CandidateGroups.ToList())
                {
                    var uncheckedModels = group.Models
                        .Where(m => !m.IsSelected)
                        .Select(m => m.Model)
                        .ToList();
                    if (uncheckedModels.Count > 0)
                    {
                        excluded[group.Target] = uncheckedModels;
                    }
                }
                hasSelected = CandidateGroups.Any(g => g.Models.Any(m => m.IsSelected));
            }
            finally
            {
                _refreshGate.Release();
            }

            if (!hasSelected)
            {
                // CanExecute is false in this state too — defense in depth against an
                // ExecuteAsync bypass.
                Message = "Select at least one candidate model to benchmark.";
                return;
            }

            // 3. All-selected → null request = the byte-identical absent-body POST (D5);
            // otherwise the run-scoped exclusion map only (no other request fields set).
            var request = excluded is { Count: > 0 }
                ? new RouterBenchmarkRequest(ExcludedModels: excluded)
                : null;
            var response = await _clientAccessor().StartBenchmarkAsync(request).ConfigureAwait(false);
            _queuedTargets = response.QueuedTargets.ToList();
            NotifyBenchmarkProgressChanged();
            Error = null;
            Message = _queuedTargets.Count == 0
                ? "No active non-Cloud targets to benchmark — add a node in Step 2 first."
                : $"Benchmarking {_queuedTargets.Count} target(s)…";
            _logger.Information(
                _queuedTargets.Count == 0
                    ? "Wizard step 4: benchmark run completed (0 target(s) queued — no active non-Cloud targets)"
                    : "Wizard step 4: benchmark run completed — {Count} target(s) queued",
                _queuedTargets.Count);
            await _poller.StartAsync().ConfigureAwait(false);
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            Message = null;
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
            _logger.Warning("Wizard step 4: benchmark run failed: {Error}", Error);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Message = null;
            Error = $"Connection failed: {ex.Message}";
            _logger.Warning("Wizard step 4: benchmark run failed: {Error}", Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Clears the stored benchmark results (in-memory + persisted).</summary>
    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearResults()
    {
        IsBusy = true;
        try
        {
            await _clientAccessor().ClearBenchmarkResultsAsync().ConfigureAwait(false);
            _queuedTargets = [];

            // Serialize the row mutations with RefreshAsync (task #130 addendum): the poller
            // tick may be mid-rebuild on another thread. Task #163: marshal the Clear()s to
            // the UI thread INSIDE the gate (the FleetTargetsViewModel pattern).
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await RunOnUiAsync(() =>
                {
                    BenchmarkRows.Clear();
                    ResultRows.Clear();
                    NotifyBenchmarkProgressChanged();
                });
            }
            finally
            {
                _refreshGate.Release();
            }

            Message = "Benchmark results cleared.";
            _logger.Information("Wizard step 4: results cleared");
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            Message = null;
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
            _logger.Warning("Wizard step 4: clearing results failed: {Error}", Error);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Message = null;
            Error = $"Connection failed: {ex.Message}";
            _logger.Warning("Wizard step 4: clearing results failed: {Error}", Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun() => !IsBusy && HasSelectedCandidate;

    /// <summary>
    /// Whether at least one candidate model is selected (task #152): the Run button's
    /// CanExecute condition + the defense-in-depth no-op check. Snapshot enumeration (task
    /// #130 addendum): the gate serializes the writers; the snapshot keeps the read defensive
    /// against a mid-rebuild tick.
    /// </summary>
    private bool HasSelectedCandidate
    {
        get
        {
            var snapshot = CandidateGroups.ToList();
            return snapshot.Any(g => g.Models.Any(m => m.IsSelected));
        }
    }

    private bool CanClear() => !IsBusy;

    /// <summary>
    /// The poller tick / on-demand refresh: the one-shot-per-entry candidate load (task
    /// #152 D7 — fetched on the first tick of the entry, retried on each tick until the
    /// first success, never re-fetched while the user stays on the step) + the benchmark
    /// results refresh, then self-stops the poller when every queued target is terminal.
    /// Single-writer (task #130 addendum): overlapping refreshes (poller tick + command side)
    /// serialize on <c>_refreshGate</c> so the row Clear()/Add rebuilds never interleave;
    /// the rebuilds additionally run on the UI thread inside the gate (task #163,
    /// <c>RunOnUiAsync</c>). The gate wait is deliberately NON-CANCELLABLE: a self-stopped
    /// tick's cancellation cancels the endpoint reads INSIDE the gate, not the serialization
    /// wait (a cancellable wait drops the first tick out before it ever runs).
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>The refresh core (called under <c>_refreshGate</c> — single writer).</summary>
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var client = _clientAccessor();

        // D7: the candidate plan loads once per step entry. On failure the flag stays false
        // (the next tick retries) and this tick's results refresh is skipped so the
        // candidate-load error stays visible until the plan arrives.
        if (!_candidatesLoaded && !await LoadCandidatesAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var results = await client.GetBenchmarkResultsAsync(ct).ConfigureAwait(false);
            await LoadResultsAsync(results).ConfigureAwait(false);
            Error = null;
            LogReachabilityTransition(true, null);
            await MaybeSelfStopAsync().ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
            LogReachabilityTransition(false, ex.Message);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Error = $"Connection failed: {ex.Message}";
            LogReachabilityTransition(false, ex.Message);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            // Task #165: the transport abort class (e.g. 995 OperationAborted) — same
            // connection-failed family as HttpRequestException (no throw, next tick retries).
            Error = $"Connection failed: {ex.Message}";
            LogReachabilityTransition(false, ex.Message);
        }
        catch (System.IO.IOException ex)
        {
            // Task #165: the transport re-wraps the aborted socket I/O — same family.
            Error = $"Connection failed: {ex.Message}";
            LogReachabilityTransition(false, ex.Message);
        }
    }

    /// <summary>
    /// Task #159 (D8) — the benchmark-poll reachability transition gate (identical semantics
    /// to the Step 2 gate): ONE Information entry per STATE CHANGE only — the 2 s poll never
    /// spams (AC6).
    /// </summary>
    private void LogReachabilityTransition(bool reachable, string? reason)
    {
        if (_daemonReachable is { } last && last == reachable)
        {
            return;
        }

        var was = _daemonReachable is { } prev
            ? (prev ? "reachable" : "unreachable")
            : "unknown";
        var now = reachable ? "reachable" : $"unreachable ({reason ?? "no reason recorded"})";
        _logger.Information("Wizard step 4: benchmark poll — daemon {Was} → {Now}", was, now);
        _daemonReachable = reachable;
    }

    /// <summary>Step lifecycle: fresh entry = fresh run scope (task #152 D7) — reset the
    /// candidate-load flag so the first tick re-fetches the plan and re-defaults EVERY
    /// checkbox to selected (run-scoped, never persisted), then start the 2 s poller (the
    /// first tick runs immediately and performs the load).</summary>
    public Task OnStepEnteredAsync(CancellationToken ct = default)
    {
        _candidatesLoaded = false;
        return _poller.StartAsync();
    }

    /// <summary>Step lifecycle: stop the poller (no orphan work).</summary>
    public Task OnStepLeftAsync() => _poller.StopAsync();

    /// <summary>
    /// The one-shot-per-entry candidate load (task #152 D7): fetches
    /// <c>GET /api/router/benchmark/candidates</c>, rebuilds <see cref="CandidateGroups"/>
    /// (every checkbox default selected) and flips <c>_candidatesLoaded</c> ONLY on success —
    /// a failure sets <see cref="Error"/> and leaves the flag false so the next tick retries.
    /// MUST run under <c>_refreshGate</c> (single writer, task #130 addendum; the group
    /// rebuild additionally runs on the UI thread via RunOnUiAsync, task #163). Returns false
    /// on failure.
    /// </summary>
    private async Task<bool> LoadCandidatesAsync(CancellationToken ct)
    {
        try
        {
            var candidates = await _clientAccessor().GetBenchmarkCandidatesAsync(ct).ConfigureAwait(false);
            await ApplyCandidatePlanAsync(candidates).ConfigureAwait(false);
            _candidatesLoaded = true;
            return true;
        }
        catch (OllamaRouterApiException ex)
        {
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
            return false;
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Error = $"Connection failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>The candidate-plan application (under <c>_refreshGate</c> — single writer):
    /// builds the replacement groups on the calling thread, then marshals the
    /// <see cref="CandidateGroups"/> Clear()/Add rebuild (plus the Run CanExecute refresh) to
    /// the UI thread (task #163 — the FleetTargetsViewModel pattern).</summary>
    private async Task ApplyCandidatePlanAsync(RouterBenchmarkCandidatesResponse candidates)
    {
        var groups = new List<BenchmarkCandidateGroupRow>();
        foreach (var target in candidates.Targets)
        {
            var hardware = target.Hardware;
            var gpu = hardware?.Gpus.FirstOrDefault();
            var rows = new List<BenchmarkCandidateRow>(target.Candidates.Count);
            foreach (var candidate in target.Candidates)
            {
                rows.Add(new BenchmarkCandidateRow(
                    target.Target,
                    candidate.Model,
                    ClassTextOf(candidate),
                    RolesTextOf(candidate.Roles),
                    FitTextOf(candidate.Fit, candidate.MaxFitsContext),
                    NotifyRunCanExecute,
                    // Task #169 (D3): pre-flight Pruned candidates start UNCHECKED (no
                    // pre-checked 70+ GB model on an 8 GB node) but stay fully toggleable.
                    isSelected: candidate.Fit != BenchmarkCandidateFit.Pruned,
                    isPruned: candidate.Fit == BenchmarkCandidateFit.Pruned));
            }

            groups.Add(new BenchmarkCandidateGroupRow
            {
                Target = target.Target,
                GpuName = gpu?.ModelName ?? string.Empty,
                // Task #164: the header shows the effective fast memory (TotalFastMemoryMB —
                // system RAM for unified nodes, summed GPU VRAM for discrete) that the fit
                // computation consumes, instead of the first GPU's dedicated VRAM (which
                // hides the fast memory on unified nodes).
                VramText = hardware is not null && hardware.TotalFastMemoryMB > 0
                    ? $"Fast memory: {FleetTargetDisplay.FormatMemoryMB(hardware.TotalFastMemoryMB)}"
                    : string.Empty,
                BandwidthText = hardware is { } profile && profile.EffectiveBandwidthGBs > 0
                    ? $"BW: {profile.EffectiveBandwidthGBs * 1024.0:0} MB/s"
                    : string.Empty,
                Note = hardware is null ? "No hardware profile detected" : null,
                Models = rows
            });
        }

        // Single-writer rebuild (task #130 addendum): Clear()/Add under the gate; task #163:
        // additionally on the UI thread, so the UI's ItemsControl enumeration never races it.
        await RunOnUiAsync(() =>
        {
            CandidateGroups.Clear();
            foreach (var group in groups)
            {
                CandidateGroups.Add(group);
            }
            OnPropertyChanged(nameof(HasCandidates));
            SelectFittingOnlyCommand.NotifyCanExecuteChanged();
            SelectNoneCommand.NotifyCanExecuteChanged();
            NotifyRunCanExecute();
        });
    }

    private static string FitTextOf(BenchmarkCandidateFit fit, int maxFitsContext) => fit switch
    {
        BenchmarkCandidateFit.Fits => $"ctx ≤ {maxFitsContext}",
        BenchmarkCandidateFit.Pruned => "won't fit this node",
        _ => "no profile (assumed)"
    };

    /// <summary>
    /// The class/offload/size annotation (task #169, D4): "{modelClass} · {offload}" where
    /// offload = "CPU offload OK" / "VRAM only", plus " · ~{n.n} GB" (the curated sizing
    /// anchor, invariant 1-decimal GB) — the size segment is OMITTED when the anchor is
    /// null (e.g. a synthetic model without a curated anchor).
    /// </summary>
    private static string ClassTextOf(RouterBenchmarkCandidateModel candidate)
    {
        var text = $"{candidate.ModelClass} · {(candidate.AllowCpuOffload ? "CPU offload OK" : "VRAM only")}";
        if (candidate.EstimatedWeightsMB is { } mb)
        {
            text += $" · ~{(mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} GB";
        }

        return text;
    }

    /// <summary>
    /// The role display text (task #169, D4): the MoE "covers everything" case (all five
    /// roles) renders as "All roles (primary: X)" (primary = Roles[0] per the #166 catalog
    /// convention), otherwise the comma-joined role names in catalog order (primary first).
    /// </summary>
    private static string RolesTextOf(IReadOnlyList<UsageRole> roles)
    {
        if (roles.Count == 5)
        {
            return $"All roles (primary: {roles[0]})";
        }

        return string.Join(", ", roles.Select(r => r.ToString()));
    }

    private void NotifyRunCanExecute() =>
        WizardUiThread.InvokeOrPost(() => RunBenchmarksCommand.NotifyCanExecuteChanged());

    /// <summary>Builds the benchmark/result rows on the calling thread, then marshles one
    /// UI-thread action that rebuilds BOTH <see cref="BenchmarkRows"/> and
    /// <see cref="ResultRows"/> (task #163 — under <c>_refreshGate</c>, the
    /// FleetTargetsViewModel pattern).</summary>
    private async Task LoadResultsAsync(RouterBenchmarkResultsResponse results)
    {
        var benchmarkRows = new List<BenchmarkRow>(results.Targets.Count);
        var resultRows = new List<BenchmarkResultRow>();
        foreach (var target in results.Targets)
        {
            benchmarkRows.Add(new BenchmarkRow
            {
                Target = target.Target,
                Status = target.Status,
                StartedAt = target.StartedAt,
                FinishedAt = target.FinishedAt,
            });

            foreach (var result in target.Results)
            {
                resultRows.Add(new BenchmarkResultRow
                {
                    Target = target.Target,
                    Model = result.Model,
                    Role = result.Role.ToString(),
                    NumCtx = result.NumCtx,
                    DecodeTokensPerSecond = result.DecodeTokensPerSecond,
                    Viable = result.DecodeTokensPerSecond >= MinViableTokensPerSecond,
                    Reason = result.Reason,
                });
            }
        }

        // Task #163: one UI-thread rebuild action per collection (never interleave with the
        // UI's enumeration).
        await RunOnUiAsync(() =>
        {
            BenchmarkRows.Clear();
            foreach (var row in benchmarkRows)
            {
                BenchmarkRows.Add(row);
            }

            ResultRows.Clear();
            foreach (var row in resultRows)
            {
                ResultRows.Add(row);
            }

            NotifyBenchmarkProgressChanged();
        });
    }

    private void NotifyBenchmarkProgressChanged()
    {
        OnPropertyChanged(nameof(HasBenchmarkRows));
        OnPropertyChanged(nameof(HasResultRows));
        OnPropertyChanged(nameof(IsBenchmarkRunning));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressSummaryText));
    }

    /// <summary>
    /// The self-stop (task #103): once every queued target is terminal, stop the poller —
    /// fire-and-forget (<see cref="Task.Run"/>) so <c>StopAsync</c> is NEVER awaited from within
    /// its own tick (a self-await would deadlock the poller loop).
    /// </summary>
    private Task MaybeSelfStopAsync()
    {
        if (_queuedTargets.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Snapshot (task #130 addendum): the rows are enumerated while another caller may be
        // mid-rebuild; the gate serializes the writers, the snapshot makes the enumeration
        // defensive.
        var rows = BenchmarkRows.ToList();
        var allTerminal = _queuedTargets.All(name =>
            rows.FirstOrDefault(r => string.Equals(r.Target, name, StringComparison.OrdinalIgnoreCase)) is { } row
            && IsTerminal(row.Status));
        if (allTerminal)
        {
            var poller = _poller;
            _ = Task.Run(() => poller.StopAsync());
        }

        return Task.CompletedTask;
    }

    private static bool IsTerminal(BenchmarkRunStatus status) =>
        status is BenchmarkRunStatus.Succeeded
            or BenchmarkRunStatus.PartiallySucceeded
            or BenchmarkRunStatus.Failed
            or BenchmarkRunStatus.Cancelled;

    partial void OnIsBusyChanged(bool value)
    {
        WizardUiThread.InvokeOrPost(() =>
        {
            RunBenchmarksCommand.NotifyCanExecuteChanged();
            ClearResultsCommand.NotifyCanExecuteChanged();
            SelectFittingOnlyCommand.NotifyCanExecuteChanged();
            SelectNoneCommand.NotifyCanExecuteChanged();
        });
    }

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
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _poller.DisposeAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
    }
}
