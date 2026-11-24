using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Routing;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Catalog;

namespace OllamaRouter.Services.Benchmarking;

/// <summary>
/// The automated micro-benchmark engine (task #28): pre-flight memory sizing, model
/// pull-if-missing, the ascending context ladder with <c>num_predict: 64</c>, the
/// ≥ 20 t/s viability gate (exactly 20.0 is viable) and the <c>benchmarks.json</c> +
/// <c>targets-speed.json</c> persistence.
/// <para>
/// Execution model: in-process single-flight via a <see cref="SemaphoreSlim(1,1)"/>;
/// <see cref="TryStartRun"/> is non-blocking and stores the run spec in a
/// <see cref="CancellationTokenSource"/>-backed slot. A fire-and-forget
/// <see cref="Task.Run"/> loop drains queued targets sequentially.
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> triggers the run CTS
/// (status → <see cref="BenchmarkRunStatus.Cancelled"/>, partial rows persisted).
/// </para>
/// </summary>
public sealed class MicroBenchmarkRunner : IMicroBenchmarkRunner
{
    // AOT-safe request body builders use JsonNode (allowed at runtime; no reflection).
    // All response bodies are parsed with JsonNode/JsonObject (no AOT deserialization
    // of unregistered shapes).

    private const string PullPath = "/api/pull";
    private const string GeneratePath = "/api/generate";
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShowTimeout = TimeSpan.FromSeconds(10);
    private static readonly string SizingSourceProfile = "profile";
    private static readonly string SizingSourceAssumed = "assumed";
    private const string SpeedSourceBenchmark = "benchmark";
    private const string SpeedSourceLearned = "learned";

    private readonly IOptions<CandidateModelCatalogOptions> _catalogOptions;
    private readonly IFleetHardwareService _fleetHardware;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly IOptionsMonitor<OllamaRouterOptions> _routerOptions;
    private readonly IOllamaModelCatalogClient _catalogClient;
    private readonly HttpClient _httpClient;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MicroBenchmarkRunner> _logger;
    private readonly string _benchmarksFilePath;
    private readonly string _speedSnapshotFilePath;

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly ConcurrentDictionary<string, BenchmarkTargetState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _queueOrder; // ordered target names for the current run
    private CancellationTokenSource? _runCts;
    private IDisposable? _runStoppingRegistration;

    public MicroBenchmarkRunner(
        IOptions<CandidateModelCatalogOptions> catalogOptions,
        IFleetHardwareService fleetHardware,
        ITokenEstimator tokenEstimator,
        IOptionsMonitor<OllamaRouterOptions> routerOptions,
        IOllamaModelCatalogClient catalogClient,
        HttpClient httpClient,
        IHostApplicationLifetime lifetime,
        ILogger<MicroBenchmarkRunner> logger,
        string benchmarksFilePath,
        string speedSnapshotFilePath)
    {
        _catalogOptions = catalogOptions ?? throw new ArgumentNullException(nameof(catalogOptions));
        _fleetHardware = fleetHardware ?? throw new ArgumentNullException(nameof(fleetHardware));
        _tokenEstimator = tokenEstimator ?? throw new ArgumentNullException(nameof(tokenEstimator));
        _routerOptions = routerOptions ?? throw new ArgumentNullException(nameof(routerOptions));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _benchmarksFilePath = benchmarksFilePath ?? throw new ArgumentNullException(nameof(benchmarksFilePath));
        _speedSnapshotFilePath = speedSnapshotFilePath ?? throw new ArgumentNullException(nameof(speedSnapshotFilePath));

        LoadPersistedState();
        _lifetime.ApplicationStopping.Register(CancelRun);
    }

    // ------------------------------------------------------------------
    //  IMicroBenchmarkRunner
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    public bool TryStartRun(
        RoutingTarget? target,
        IEnumerable<UsageRole>? roles,
        string? model,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? excludedModels = null)
    {
        if (!_runGate.Wait(0))
        {
            return false;
        }

        var rolesList = roles?.ToList() ?? new List<UsageRole>();
        var effectiveTargets = _routerOptions.CurrentValue.GetEffectiveTargets(_logger)
            .Where(t => !string.Equals(t.Name, "Cloud", StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<OllamaTargetOptions> selected;
        if (target is not null && !string.IsNullOrWhiteSpace(target.Value))
        {
            var match = effectiveTargets.FirstOrDefault(t =>
                string.Equals(t.Name, target.Value, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _runGate.Release();
                return false;
            }
            selected = [match];
        }
        else
        {
            selected = effectiveTargets;
        }

        if (selected.Count == 0)
        {
            _runGate.Release();
            return true; // nothing to do, but the run "completed" immediately
        }

        var cts = new CancellationTokenSource();
        var stoppingRegistration = _lifetime.ApplicationStopping.Register(cts.Cancel);
        _runCts = cts;
        _runStoppingRegistration = stoppingRegistration;

        // Mark all targets as Running immediately so GetStatusAsync can report them.
        lock (_stateGate)
        {
            _queueOrder = selected.Select(t => t.Name!).ToList();
            var now = DateTimeOffset.UtcNow;
            foreach (var t in selected)
            {
                _states[t.Name!] = new BenchmarkTargetState(
                    t.Name!, BenchmarkRunStatus.Running, now, null, now,
                    SizingSourceAssumed, []);
            }
        }

        _ = Task.Run(() => RunLoopAsync(selected, rolesList, model, excludedModels, cts.Token));

        return true;
    }

    /// <inheritdoc/>
    public Task<BenchmarkTargetResult?> GetStatusAsync(RoutingTarget target)
    {
        lock (_stateGate)
        {
            if (_states.TryGetValue(target.Value, out var state))
            {
                return Task.FromResult<BenchmarkTargetResult?>(new BenchmarkTargetResult(
                    state.Target, state.Status, state.StartedAt, state.FinishedAt, state.Results));
            }
        }

        return Task.FromResult<BenchmarkTargetResult?>(null);
    }

    /// <inheritdoc/>
    public IReadOnlyList<BenchmarkTargetResult> GetAllResults()
    {
        lock (_stateGate)
        {
            var ordered = _queueOrder is { Count: > 0 } order
                ? order.Select(name => _states.TryGetValue(name, out var s) ? s : null)
                        .Where(s => s is not null)
                        .Select(s => s!)
                        .Cast<BenchmarkTargetState>()
                        .Concat(_states.Values.Where(s => !order.Contains(s.Target, StringComparer.OrdinalIgnoreCase)))
                : _states.Values;

            return ordered
                .Select(s => new BenchmarkTargetResult(
                    s.Target, s.Status, s.StartedAt, s.FinishedAt, s.Results))
                .ToList();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetEffectiveTargetNames() =>
        _routerOptions.CurrentValue.GetEffectiveTargets(_logger)
            .Where(t => !string.Equals(t.Name, "Cloud", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name!)
            .ToList();

    /// <inheritdoc/>
    public RouterBenchmarkCandidatesResponse GetBenchmarkCandidates()
    {
        // The full deduplicated catalog — exactly the candidate set a run with no roles/model
        // filter benchmarks for every target (sizing-pruned models included).
        var candidates = FilterCandidates([], null);
        var ladder = _catalogOptions.Value.BenchmarkContextLadder;

        var groups = new List<RouterBenchmarkCandidateTarget>();
        // Same effective non-Cloud derivation as TryStartRun (configuration order).
        var effectiveTargets = _routerOptions.CurrentValue.GetEffectiveTargets(_logger)
            .Where(t => !string.Equals(t.Name, "Cloud", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var target in effectiveTargets)
        {
            var name = target.Name!;

            // STORE read only (D1): IFleetHardwareService.GetProfile never probes a peer —
            // the candidates read must stay network-free, so the call is fail-soft to null.
            NodeHardwareProfile? profile = null;
            try
            {
                profile = _fleetHardware.GetProfile(name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to get hardware profile for {Target}; reporting an unknown profile in the candidate plan.",
                    name);
            }

            var rows = new List<RouterBenchmarkCandidateModel>();
            foreach (var (candidate, _) in candidates)
            {
                // ONE shared derivation with the run loop (D2): the endpoint can never
                // drift from what a run would actually do on this node.
                var fit = EvaluateCandidateFit(candidate, ladder, profile);
                var verdict = fit.AssumedSizing
                    ? BenchmarkCandidateFit.Assumed
                    : fit.AnySurvived
                        ? BenchmarkCandidateFit.Fits
                        : BenchmarkCandidateFit.Pruned;
                rows.Add(new RouterBenchmarkCandidateModel(
                    candidate.Name,
                    candidate.Roles,
                    candidate.Quantization,
                    candidate.Architecture,
                    candidate.TotalParams,
                    candidate.ActiveParams,
                    candidate.NativeContext,
                    verdict,
                    fit.MaxSurvivingCtx,
                    // Task #169: the curated-catalog class metadata (a plain string on the
                    // wire — CandidateModelClass lives in this server assembly; D1).
                    candidate.ModelClass.ToString(),
                    candidate.AllowCpuOffload,
                    candidate.EstimatedWeightsMB));
            }

            groups.Add(new RouterBenchmarkCandidateTarget(name, profile, rows));
        }

        return new RouterBenchmarkCandidatesResponse(groups);
    }

    /// <inheritdoc/>
    public void ClearResults(bool keepRunning = false)
    {
        lock (_stateGate)
        {
            if (keepRunning)
            {
                // The delete applies to completed targets only: in-flight (Running) states are
                // retained and the in-flight work continues for its remaining targets.
                foreach (var (name, state) in _states.ToList())
                {
                    if (state.Status != BenchmarkRunStatus.Running)
                    {
                        _states.TryRemove(name, out _);
                    }
                }
            }
            else
            {
                _states.Clear();
            }

            _queueOrder = null;
        }

        DeleteFileSafe(_benchmarksFilePath);
        RemoveBenchmarkSpeedEntries();
    }

    // ------------------------------------------------------------------
    //  Run loop
    // ------------------------------------------------------------------

    private async Task RunLoopAsync(
        List<OllamaTargetOptions> targets,
        List<UsageRole> roles,
        string? modelFilter,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? excludedModels,
        CancellationToken ct)
    {
        try
        {
            foreach (var target in targets)
            {
                if (ct.IsCancellationRequested)
                {
                    // The CTS can already be cancelled when the loop starts (ApplicationStopping
                    // fired between TryStartRun and the first iteration): mark the current
                    // target too, not just the ones after it, or it would stay "Running".
                    MarkTargetCancelled(target.Name!);
                    MarkRemainingCancelled(targets, target);
                    break;
                }

                // Fault isolation: one target's unexpected failure must not wedge the whole
                // run or leave the target in "Running" forever.
                try
                {
                    await RunTargetBenchmarkAsync(target, roles, modelFilter, excludedModels, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Cancellation while this target's work is in flight: the target has not
                    // written its own final state — mark it Cancelled (it would otherwise be
                    // stuck in "Running" in memory and in benchmarks.json) before bailing out.
                    MarkTargetCancelled(target.Name!);
                    MarkRemainingCancelled(targets, target);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Benchmark run for target {Target} failed unexpectedly.", target.Name);
                    lock (_stateGate)
                    {
                        var name = target.Name!;
                        if (_states.TryGetValue(name, out var existing) && existing.Status == BenchmarkRunStatus.Running)
                        {
                            var now = DateTimeOffset.UtcNow;
                            _states[name] = new BenchmarkTargetState(
                                name, BenchmarkRunStatus.Failed, existing.StartedAt, now, now,
                                existing.SizingSource, existing.Results);
                        }
                    }
                    PersistStateSafe();
                }
            }
        }
        finally
        {
            _runGate.Release();
            var cts = _runCts;
            var stoppingRegistration = _runStoppingRegistration;
            _runCts = null;
            _runStoppingRegistration = null;
            if (cts is not null && stoppingRegistration is not null)
            {
                stoppingRegistration.Dispose();
                cts.Dispose();
            }
        }
    }

    private void MarkTargetCancelled(string name)
    {
        lock (_stateGate)
        {
            if (_states.TryGetValue(name, out var existing) && existing.Status == BenchmarkRunStatus.Running)
            {
                var now = DateTimeOffset.UtcNow;
                _states[name] = new BenchmarkTargetState(
                    name, BenchmarkRunStatus.Cancelled, existing.StartedAt, now, now,
                    existing.SizingSource, existing.Results);
            }
        }
    }

    private void MarkRemainingCancelled(List<OllamaTargetOptions> allTargets, OllamaTargetOptions? afterTarget)
    {
        lock (_stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            var start = afterTarget is not null ? allTargets.IndexOf(afterTarget) + 1 : 0;
            for (var i = start; i < allTargets.Count; i++)
            {
                var name = allTargets[i].Name!;
                if (_states.TryGetValue(name, out var existing) && existing.Status == BenchmarkRunStatus.Running)
                {
                    _states[name] = new BenchmarkTargetState(
                        name, BenchmarkRunStatus.Cancelled, existing.StartedAt, now, now,
                        existing.SizingSource, existing.Results);
                }
            }
        }

        PersistStateSafe();
    }

    private async Task RunTargetBenchmarkAsync(
        OllamaTargetOptions target,
        List<UsageRole> roles,
        string? modelFilter,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? excludedModels,
        CancellationToken ct)
    {
        var name = target.Name!;
        var url = target.Url?.TrimEnd('/');
        var startedAt = DateTimeOffset.UtcNow;

        // 1. Filter catalog candidates by roles/model, then drop the run-scoped exclusions of
        // THIS target (task #152): a case-insensitive name set (blank entries dropped; unknown
        // keys never match). Excluded models produce no rows — no sizing, no /api/tags presence
        // check, no /api/pull, no /api/show, no eviction, no generate.
        var excludedForTarget = ExcludedModelNamesFor(name, excludedModels);
        var candidates = FilterCandidates(roles, modelFilter)
            .Where(c => !excludedForTarget.Contains(c.Candidate.Name))
            .ToArray();

        // 2. Get the hardware profile for sizing.
        NodeHardwareProfile? profile = null;
        try
        {
            profile = _fleetHardware.GetProfile(name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get hardware profile for {Target}; using fail-open sizing.", name);
        }

        var sizingSource = profile is not null && profile.TotalFastMemoryMB > 0
            ? SizingSourceProfile
            : SizingSourceAssumed;

        var ladder = _catalogOptions.Value.BenchmarkContextLadder;
        var allResults = new List<BenchmarkContextResult>();

        // Last candidate that was measured in this target run (task #167 cold-start
        // isolation): it is evicted BEFORE the next candidate's first rung so its weights +
        // KV (Ollama's default 5-minute keep-alive) do not stay resident during the next
        // candidate's cold loads (distorted layer offloading / spurious OOMs). Sizing-pruned
        // and pull-failed candidates load nothing and never update it; after the run ends
        // nothing is unloaded (the last model stays resident for real traffic).
        string? previousMeasuredModel = null;

        // 3. For each candidate: sizing, pull, cap, benchmark loop (evict → measure).
        foreach (var (candidate, role) in candidates)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Evaluate sizing for each ladder context (ONE shared derivation with
            // GetBenchmarkCandidates — the same map / AnySurvived values as before the
            // task #152 extraction, so the run behavior is byte-identical).
            var fit = EvaluateCandidateFit(candidate, ladder, profile);

            // If ALL contexts are pruned → record sizing-pruned rows, no pull.
            if (!fit.AnySurvived)
            {
                foreach (var ctx in ladder)
                {
                    allResults.Add(new BenchmarkContextResult(
                        candidate.Name, role, ctx,
                        0, 0, 0, 0,
                        Viable: false,
                        Reason: "sizing-pruned"));
                }
                continue;
            }

            // 4. Pull the model if not already present.
            var pullResult = await EnsureModelAsync(url, candidate.Name, ct).ConfigureAwait(false);
            if (pullResult == PullResult.Failed)
            {
                allResults.Add(new BenchmarkContextResult(
                    candidate.Name, role, ladder[0],
                    0, 0, 0, 0,
                    Viable: false,
                    Reason: "error:pull"));
                continue;
            }

            // 5. Derive the effective context cap (task #167): candidate.NativeContext, refined
            // DOWNWARD only by the live GGUF architectural ceiling (model_info["*.context_length"]
            // via /api/show). The Modelfile PARAMETER num_ctx is NEVER a cap — options.num_ctx at
            // generate time overrides it up to the model's native context (the conservative
            // Modelfile defaults would silently drop rungs like 128k/160k/256k). Fail-soft:
            // show failure / missing model_info → NativeContext alone.
            long? ggufNativeCtx = null;
            try
            {
                var show = await _catalogClient.GetShowAsync(url, candidate.Name, ct).ConfigureAwait(false);
                ggufNativeCtx = ModelMetadataParser.ExtractNativeContextLength(show?["model_info"] as JsonObject);
            }
            catch
            {
                // Fail-soft: no GGUF cap.
            }

            var effectiveMaxCtx = (int)Math.Min((long)candidate.NativeContext, ggufNativeCtx ?? (long)candidate.NativeContext);

            // Cross-candidate cold-start eviction (task #167): release the previously measured
            // candidate BEFORE this candidate's cold loads (its VRAM residency would otherwise
            // distort the offloading decision and trigger spurious OOMs).
            if (previousMeasuredModel is not null)
            {
                await UnloadModelAsync(url, previousMeasuredModel, ct).ConfigureAwait(false);
            }

            // 6. Benchmark loop (ascending ladder): evict → measure at every surviving rung.
            // There is NO warmup (removed in task #167 — the proven benchmark.py methodology
            // is evict → single generate): the first measurement generate of a rung loads the
            // weights + allocates the KV buffer at EXACTLY this rung's num_ctx + prefills +
            // decodes, and llama.cpp builds its compute graph at load time, so
            // eval_count/eval_duration (the viability gate input) are representative
            // steady-state decode.
            foreach (var ctx in ladder.Where(c => c <= effectiveMaxCtx))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var est = fit.SizingByContext[ctx];

                // Pruned context: record the row, continue to next.
                if (est.Status is MemoryFitStatus.PrunedWeights or MemoryFitStatus.PrunedKvCache)
                {
                    allResults.Add(new BenchmarkContextResult(
                        candidate.Name, role, ctx,
                        0, 0, 0, 0,
                        Viable: false,
                        Reason: "sizing-pruned"));
                    continue;
                }

                // Surviving context: evict this model so the KV allocation is a clean
                // cold start at exactly this rung's num_ctx (no inherited smaller buffer or
                // partial context from the previous rung), then benchmark it.
                await UnloadModelAsync(url, candidate.Name, ct).ConfigureAwait(false);

                var (viable, result) = await RunSingleBenchmarkAsync(
                    url, candidate, role, ctx, ct).ConfigureAwait(false);

                if (result is not null)
                {
                    allResults.Add(result);
                }

                if (!viable && result?.Reason is not null &&
                    result.Reason != "sizing-pruned")
                {
                    // Stop rule: first non-viable measured row stops the ladder.
                    break;
                }
            }

            // This candidate was measured (or at least attempted) — the next candidate evicts
            // IT before its own first rung. Sizing-pruned and pull-failed candidates continue
            // before reaching here and never update the tracker (they load nothing).
            previousMeasuredModel = candidate.Name;
        }

        // 8. Determine the final status and persist.
        var finishedAt = DateTimeOffset.UtcNow;
        var hasMeasured = allResults.Any(r => r.Reason is null);
        var hasPruned = allResults.Any(r => r.Reason == "sizing-pruned");
        var hasError = allResults.Any(r => r.Reason is not null && r.Reason.StartsWith("error:", StringComparison.Ordinal));
        var hasOom = allResults.Any(r => r.Reason == "oom");

        BenchmarkRunStatus status;
        if (ct.IsCancellationRequested)
        {
            status = BenchmarkRunStatus.Cancelled;
        }
        else if (allResults.Count == 0)
        {
            status = BenchmarkRunStatus.Failed;
        }
        else if (hasError || hasOom)
        {
            status = hasMeasured ? BenchmarkRunStatus.PartiallySucceeded : BenchmarkRunStatus.Failed;
        }
        else
        {
            status = BenchmarkRunStatus.Succeeded;
        }

        var state = new BenchmarkTargetState(
            name, status, startedAt, finishedAt, finishedAt, sizingSource, allResults);

        lock (_stateGate)
        {
            _states[name] = state;
        }

        PersistStateSafe();
        SeedSpeedSnapshot(name, allResults);
    }

    // ------------------------------------------------------------------
    //  Candidate filtering
    // ------------------------------------------------------------------

    private (CandidateModel Candidate, UsageRole Role)[] FilterCandidates(
        List<UsageRole> roles,
        string? modelFilter)
    {
        var catalog = _catalogOptions.Value.Models;
        var results = new List<(CandidateModel, UsageRole)>();

        foreach (var candidate in catalog)
        {
            // Model name filter.
            if (modelFilter is not null &&
                !string.Equals(candidate.Name, modelFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Role filter: when roles are specified, the candidate must match at least one
            // requested role (the first match wins). Explicit nullable loop — LINQ
            // FirstOrDefault on a non-nullable List<UsageRole> would collapse a missing
            // match into default(UsageRole) (BasicModel = 0), never null.
            UsageRole? matchingRole = null;
            foreach (var requested in roles)
            {
                if (candidate.Roles.Contains(requested))
                {
                    matchingRole = requested;
                    break;
                }
            }
            if (matchingRole is null)
            {
                // No requested role matches this candidate (or none were requested):
                // without a role filter the first role in the record tags the row.
                if (roles.Count > 0)
                {
                    continue;
                }
                matchingRole = candidate.Roles[0];
            }

            results.Add((candidate, matchingRole ?? candidate.Roles[0]));
        }

        // De-duplicate by model name (first matching role wins).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<(CandidateModel, UsageRole)>();
        foreach (var (c, r) in results)
        {
            if (seen.Add(c.Name))
            {
                deduped.Add((c, r));
            }
        }

        return deduped.ToArray();
    }

    /// <summary>
    /// The per-ladder-context fit verdict of one catalog candidate on one node profile
    /// (task #152): the per-context <see cref="CandidateSizingEstimate"/> map over the WHOLE
    /// ladder, whether ANY context survives sizing (<see cref="AnySurvived"/> — the run loop's
    /// pull / sizing-pruned decision), the largest surviving ladder context capped by the
    /// model's <see cref="CandidateModel.NativeContext"/> (<see cref="MaxSurvivingCtx"/>; 0 when
    /// nothing survives) and whether sizing ran fail-open (<see cref="AssumedSizing"/> — the
    /// run loop's "assumed" sizing source rule: null profile or TotalFastMemoryMB <= 0).
    /// <para>
    /// ONE shared derivation: the run loop and <see cref="GetBenchmarkCandidates"/> both use
    /// these exact values, so the published candidate plan can never drift from what a run
    /// would actually do on the node. Accepted nuance (task #167): the network-free plan caps
    /// at NativeContext, while a live run may refine the effective context cap DOWNWARD only,
    /// when the daemon's GGUF <c>model_info["*.context_length"]</c> reports a smaller
    /// architectural ceiling (a no-op for every curated model, whose GGUF context_length
    /// equals NativeContext = 262,144). The Modelfile <c>PARAMETER num_ctx</c> is never a cap.
    /// </para>
    /// </summary>
    internal sealed record CandidateFitEvaluation(
        Dictionary<int, CandidateSizingEstimate> SizingByContext,
        bool AnySurvived,
        int MaxSurvivingCtx,
        bool AssumedSizing);

    internal CandidateFitEvaluation EvaluateCandidateFit(
        CandidateModel candidate,
        IReadOnlyList<int> ladder,
        NodeHardwareProfile? profile)
    {
        var sizingByContext = new Dictionary<int, CandidateSizingEstimate>();
        var anySurvived = false;
        var maxSurvivingCtx = 0;
        foreach (var ctx in ladder)
        {
            var estimate = CandidateSizingCalculator.Evaluate(candidate, ctx, profile);
            sizingByContext[ctx] = estimate;
            if (estimate.Status is MemoryFitStatus.Fits or MemoryFitStatus.Assumed)
            {
                anySurvived = true;
                // Cap by NativeContext: the run loop never measures a rung above the model's
                // native context (effectiveMaxCtx = min(NativeContext, live GGUF
                // context_length ?? NativeContext) — the Modelfile PARAMETER num_ctx is
                // never a cap, task #167).
                if (ctx <= candidate.NativeContext && ctx > maxSurvivingCtx)
                {
                    maxSurvivingCtx = ctx;
                }
            }
        }

        var assumedSizing = profile is null || profile.TotalFastMemoryMB <= 0;
        return new CandidateFitEvaluation(sizingByContext, anySurvived, maxSurvivingCtx, assumedSizing);
    }

    /// <summary>
    /// The run-scoped excluded model names of ONE target (task #152): the map is matched
    /// case-insensitively by target key (a JSON-deserialized dictionary is ordinal, so the
    /// keys are walked, not probed), blank model entries are dropped, unknown keys and model
    /// names are no-ops by construction.
    /// </summary>
    private static HashSet<string> ExcludedModelNamesFor(
        string targetName,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? excludedModels)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludedModels is null)
        {
            return set;
        }

        foreach (var (key, names) in excludedModels)
        {
            if (!string.Equals(key, targetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    set.Add(name);
                }
            }
        }

        return set;
    }

    // ------------------------------------------------------------------
    //  Model pull
    // ------------------------------------------------------------------

    private enum PullResult { Present, Pulled, Failed }

    private async Task<PullResult> EnsureModelAsync(string? baseUrl, string modelName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return PullResult.Failed;
        }

        try
        {
            // Check if the model is already present via /api/tags.
            var tags = await _catalogClient.GetTagsAsync(baseUrl, ct).ConfigureAwait(false);
            foreach (var tag in tags)
            {
                var tagName = tag?["name"]?.ToString() ?? tag?["model"]?.ToString();
                if (string.Equals(tagName, modelName, StringComparison.OrdinalIgnoreCase))
                {
                    return PullResult.Present;
                }
            }

            // Model absent: pull it.
            _logger.LogInformation("Pulling model {Model} from {Url}...", modelName, baseUrl);
            var pullBody = new JsonObject { ["model"] = modelName, ["stream"] = false };
            using var content = new StringContent(pullBody.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PullTimeout);

            using var pullRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), PullPath)) { Content = content };
            using var response = await _httpClient
                .SendAsync(pullRequest, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Pull of {Model} failed: {Status} {Body}", modelName, response.StatusCode, errorBody);
                return PullResult.Failed;
            }

            // Parse the pull response for errors.
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var json = JsonNode.Parse(raw) as JsonObject;
            if (json?["error"]?.GetValue<string>() is { Length: > 0 } error)
            {
                _logger.LogWarning("Pull of {Model} reported error: {Error}", modelName, error);
                return PullResult.Failed;
            }

            _logger.LogInformation("Model {Model} pulled successfully.", modelName);
            return PullResult.Pulled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pull of {Model} from {Url} failed.", modelName, baseUrl);
            return PullResult.Failed;
        }
    }

    // ------------------------------------------------------------------
    //  Cold-start eviction + benchmark
    // ------------------------------------------------------------------

    /// <summary>
    /// Cold-start model eviction (task #167): asks Ollama to release
    /// <paramref name="modelName"/> immediately — the exact <c>POST /api/generate</c>
    /// <c>{"model", "keep_alive": 0}</c> isolation call of the proven benchmark.py
    /// methodology — via the already-injected catalog client (its own 4 s bound; the
    /// call is a fast memory release, never a reload). The next measurement generate then
    /// loads fresh and allocates the KV buffer at EXACTLY the rung's num_ctx. Fail-soft:
    /// a failed eviction only distorts VRAM residency for that rung — the run continues
    /// (warning logged) and the next rung's own eviction re-attempts for the same model.
    /// Short-circuits on a blank url/model or an already-cancelled token; a caller-requested
    /// cancellation during the call propagates as <see cref="OperationCanceledException"/>
    /// (the run-loop's Cancelled contract).
    /// </summary>
    private async Task UnloadModelAsync(string? baseUrl, string modelName, CancellationToken ct)
    {
        if (ct.IsCancellationRequested ||
            string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        var unloaded = await _catalogClient
            .UnloadModelAsync(baseUrl, modelName, ct)
            .ConfigureAwait(false);
        if (!unloaded)
        {
            _logger.LogWarning(
                "Cold-start eviction for {Model} on {Url} did not land; the run continues " +
                "(the next rung's own eviction re-attempts for that model).",
                modelName, baseUrl);
        }
    }

    /// <summary>
    /// Runs a single benchmark generation at the given context size and returns
    /// (viable, resultRow). The result row is null on HTTP/parse failure (the caller
    /// records an error row).
    /// </summary>
    private async Task<(bool Viable, BenchmarkContextResult? Result)> RunSingleBenchmarkAsync(
        string? baseUrl,
        CandidateModel candidate,
        UsageRole role,
        int numCtx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (false, new BenchmarkContextResult(
                candidate.Name, role, numCtx, 0, 0, 0, 0,
                Viable: false, Reason: "error:no-url"));
        }

        try
        {
            // Build the deterministic fixed-size filler prompt (rung-invariant, task #167).
            var filler = BuildFillerPrompt(numCtx, candidate.Name);

            var body = new JsonObject
            {
                ["model"] = candidate.Name,
                ["prompt"] = filler,
                ["stream"] = false,
                ["options"] = new JsonObject
                {
                    ["num_ctx"] = numCtx,
                    ["num_predict"] = 64,
                    ["temperature"] = 0
                }
            };

            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(GenerateTimeout);

            using var generateRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), GeneratePath)) { Content = content };
            using var response = await _httpClient
                .SendAsync(generateRequest, cts.Token)
                .ConfigureAwait(false);

            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var json = JsonNode.Parse(raw) as JsonObject;

            if (json is null)
            {
                return (false, new BenchmarkContextResult(
                    candidate.Name, role, numCtx, 0, 0, 0, 0,
                    Viable: false, Reason: "error:bad-response"));
            }

            // Ollama error body: {"error": "..."}.
            if (json["error"]?.GetValue<string>() is { Length: > 0 } error)
            {
                _logger.LogWarning("Ollama error for {Model} at {NumCtx} ctx: {Error}",
                    candidate.Name, numCtx, error);
                return (false, new BenchmarkContextResult(
                    candidate.Name, role, numCtx, 0, 0, 0, 0,
                    Viable: false, Reason: "oom"));
            }

            // Parse metrics.
            var evalCount = json["eval_count"]?.GetValue<int>() ?? 0;
            var evalDurationNs = json["eval_duration"]?.GetValue<long>() ?? 0;
            var promptEvalCount = json["prompt_eval_count"]?.GetValue<int>() ?? 0;
            var promptEvalDurationNs = json["prompt_eval_duration"]?.GetValue<long>() ?? 0;

            // Guard: eval_duration <= 0 or non-finite → error.
            if (evalDurationNs <= 0 || evalCount <= 0)
            {
                return (false, new BenchmarkContextResult(
                    candidate.Name, role, numCtx, evalCount, evalDurationNs, 0, 0,
                    Viable: false, Reason: "error:bad-metrics"));
            }

            var decodeTps = evalCount / (evalDurationNs * 1e-9);
            var prefillTps = promptEvalDurationNs > 0
                ? promptEvalCount / (promptEvalDurationNs * 1e-9)
                : 0;

            if (!double.IsFinite(decodeTps) || decodeTps < 0)
            {
                return (false, new BenchmarkContextResult(
                    candidate.Name, role, numCtx, evalCount, evalDurationNs, 0, 0,
                    Viable: false, Reason: "error:bad-metrics"));
            }

            // Threshold: exactly 20.0 is viable ("at least 20").
            var viable = decodeTps >= CandidateModelCatalogOptions.MinViableTokensPerSecond;

            var reason = viable ? null : "below-threshold";

            return (viable, new BenchmarkContextResult(
                candidate.Name, role, numCtx,
                evalCount, evalDurationNs,
                decodeTps, prefillTps,
                Viable: viable,
                Reason: reason));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Benchmark of {Model} at {NumCtx} ctx failed.", candidate.Name, numCtx);
            return (false, new BenchmarkContextResult(
                candidate.Name, role, numCtx, 0, 0, 0, 0,
                Viable: false, Reason: $"error:exception:{ex.GetType().Name}:{ex.Message}"));
        }
    }

    // ------------------------------------------------------------------
    //  Filler prompt
    // ------------------------------------------------------------------

    /// <summary>
    /// Fixed filler-prompt size target in estimated tokens (task #167): the middle of the
    /// ~512–1,024 token window the benchmark methodology prescribes for the measurement
    /// prefill.
    /// </summary>
    private const int FillerPromptTargetTokens = 768;

    /// <summary>
    /// Builds the deterministic real-text filler prompt for the measurement generate
    /// (task #167): a FIXED string of ≈ <see cref="FillerPromptTargetTokens"/> estimated
    /// tokens, BYTE-IDENTICAL at every ladder rung. Cold-start KV sizing comes from
    /// <c>options.num_ctx</c> in the measurement body — NOT from the prompt length — so the
    /// old numCtx-scaled multi-megabyte prompt (whose 256k prefill alone exceeded the 5-minute
    /// GenerateTimeout) is never built; the model allocates the full rung KV cache and
    /// decodes on a bounded, representative prefill. The prompt must be *real text*, not
    /// padding bytes, so the prefill speed is representative; token math is O(1) per
    /// estimator call (tokens × repetitions, never a re-estimate of the whole string). A
    /// fixed benchmark suffix is appended.
    /// <para>
    /// <paramref name="numCtx"/> is retained for call-site clarity only — by design it does
    /// NOT influence the output (the prompt is rung-invariant).
    /// </para>
    /// </summary>
    internal string BuildFillerPrompt(int numCtx, string? modelName = null)
    {
        // Rung-invariant by design: options.num_ctx (not the prompt) sizes the KV cache.
        _ = numCtx;

        const string Suffix = "Summarize the above text in one paragraph. Begin now:";
        const string BaseSentence =
            "The quick brown fox jumps over the lazy dog near the riverbank while the " +
            "sun sets behind the distant mountains and the evening breeze carries the " +
            "scent of wildflowers across the meadow below. ";

        var baseTokens = _tokenEstimator.EstimateTokens(BaseSentence, modelName);
        if (baseTokens <= 0)
        {
            baseTokens = 20; // fallback
        }

        var repetitions = Math.Max(1, (FillerPromptTargetTokens + baseTokens - 1) / baseTokens);
        // Defensive cap: even a pathological estimator must not yield an unbounded prompt
        // string (a 1-token/char estimator caps out at a few KB of real text). The target
        // is then simply not reached.
        const int MaxRepetitions = 100;
        if (repetitions > MaxRepetitions)
        {
            repetitions = MaxRepetitions;
        }

        return string.Concat(Enumerable.Repeat(BaseSentence, repetitions)) + Suffix;
    }

    // ------------------------------------------------------------------
    //  Persistence
    // ------------------------------------------------------------------

    private void LoadPersistedState()
    {
        try
        {
            if (File.Exists(_benchmarksFilePath))
            {
                var json = File.ReadAllText(_benchmarksFilePath);
                var loaded = JsonSerializer.Deserialize(
                    json, OllamaRouterJsonSerializerContext.Default.DictionaryStringBenchmarkTargetState);
                if (loaded is not null)
                {
                    foreach (var (name, state) in loaded)
                    {
                        _states[name] = state;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt file → log + start empty (fail-soft, mirrors Task #27).
            _logger.LogWarning(ex,
                "Failed to read benchmark state from {Path}; starting with an empty store.",
                _benchmarksFilePath);
        }
    }

    private void PersistStateSafe()
    {
        try
        {
            var directory = Path.GetDirectoryName(_benchmarksFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (_stateGate)
            {
                var state = new Dictionary<string, BenchmarkTargetState>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in _states)
                {
                    state[pair.Key] = pair.Value;
                }

                File.WriteAllText(
                    _benchmarksFilePath,
                    JsonSerializer.Serialize(state, OllamaRouterJsonSerializerContext.Default.DictionaryStringBenchmarkTargetState));
            }
        }
        catch (Exception ex)
        {
            // Failsafe: the in-memory state always wins even when the disk write fails.
            _logger.LogWarning(ex,
                "Failed to persist benchmark state to {Path}.", _benchmarksFilePath);
        }
    }

    private void DeleteFileSafe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete {Path}.", path);
        }
    }

    private void SeedSpeedSnapshot(string targetName, IReadOnlyList<BenchmarkContextResult> results)
    {
        // Only viable (measured) rows contribute. Group by model name.
        var viableByModel = results
            .Where(r => r.Viable && r.Reason is null)
            .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        if (viableByModel.Count == 0)
        {
            return;
        }

        try
        {
            var snapshot = LoadSpeedSnapshot();
            var now = DateTimeOffset.UtcNow;

            foreach (var (modelName, rows) in viableByModel)
            {
                // The largest viable context row is the representative steady-state speed.
                var bestRow = rows.OrderByDescending(r => r.NumCtx).First();
                var sampleCount = rows.Count;

                // Guard: never overwrite "learned" entries (task #23 merge safety).
                if (snapshot.TryGetValue(targetName, out var models) &&
                    models.TryGetValue(modelName, out var existing) &&
                    string.Equals(existing.Source, SpeedSourceLearned, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!snapshot.TryGetValue(targetName, out models))
                {
                    models = new Dictionary<string, SpeedSnapshotEntry>(StringComparer.OrdinalIgnoreCase);
                    snapshot[targetName] = models;
                }

                models[modelName] = new SpeedSnapshotEntry(
                    bestRow.DecodeTokensPerSecond, sampleCount, SpeedSourceBenchmark, now);
            }

            SaveSpeedSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to seed speed snapshot to {Path}.", _speedSnapshotFilePath);
        }
    }

    private void RemoveBenchmarkSpeedEntries()
    {
        try
        {
            var snapshot = LoadSpeedSnapshot();
            var changed = false;

            foreach (var (targetName, models) in snapshot.ToList())
            {
                foreach (var (modelName, entry) in models.ToList())
                {
                    if (string.Equals(entry.Source, SpeedSourceBenchmark, StringComparison.OrdinalIgnoreCase))
                    {
                        models.Remove(modelName);
                        changed = true;
                    }
                }

                if (models.Count == 0)
                {
                    snapshot.Remove(targetName);
                }
            }

            if (changed)
            {
                SaveSpeedSnapshot(snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to remove benchmark speed entries from {Path}.", _speedSnapshotFilePath);
        }
    }

    private Dictionary<string, Dictionary<string, SpeedSnapshotEntry>> LoadSpeedSnapshot()
    {
        try
        {
            if (File.Exists(_speedSnapshotFilePath))
            {
                var json = File.ReadAllText(_speedSnapshotFilePath);
                var loaded = JsonSerializer.Deserialize(
                    json, OllamaRouterJsonSerializerContext.Default.DictionaryStringDictionaryStringSpeedSnapshotEntry);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read speed snapshot from {Path}; starting empty.", _speedSnapshotFilePath);
        }

        return new Dictionary<string, Dictionary<string, SpeedSnapshotEntry>>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveSpeedSnapshot(
        Dictionary<string, Dictionary<string, SpeedSnapshotEntry>> snapshot)
    {
        var directory = Path.GetDirectoryName(_speedSnapshotFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            _speedSnapshotFilePath,
            JsonSerializer.Serialize(snapshot, OllamaRouterJsonSerializerContext.Default.DictionaryStringDictionaryStringSpeedSnapshotEntry));
    }

    // ------------------------------------------------------------------
    //  Cancellation
    // ------------------------------------------------------------------

    private void CancelRun()
    {
        _runCts?.Cancel();
    }
}
