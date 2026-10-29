using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Services.Hardware;

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
    private readonly IOptions<OllamaRouterOptions> _routerOptions;
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
        IOptions<OllamaRouterOptions> routerOptions,
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
    public bool TryStartRun(RoutingTarget? target, IEnumerable<UsageRole>? roles, string? model)
    {
        if (!_runGate.Wait(0))
        {
            return false;
        }

        var rolesList = roles?.ToList() ?? new List<UsageRole>();
        var effectiveTargets = _routerOptions.Value.GetEffectiveTargets(_logger)
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

        _ = Task.Run(() => RunLoopAsync(selected, rolesList, model, cts.Token));

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
        _routerOptions.Value.GetEffectiveTargets(_logger)
            .Where(t => !string.Equals(t.Name, "Cloud", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name!)
            .ToList();

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
                    await RunTargetBenchmarkAsync(target, roles, modelFilter, ct).ConfigureAwait(false);
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
        CancellationToken ct)
    {
        var name = target.Name!;
        var url = target.Url?.TrimEnd('/');
        var startedAt = DateTimeOffset.UtcNow;

        // 1. Filter catalog candidates by roles/model.
        var candidates = FilterCandidates(roles, modelFilter);

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

        // 3. For each candidate: sizing, pull, warmup, benchmark loop.
        foreach (var (candidate, role) in candidates)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Evaluate sizing for each ladder context.
            var sizingByContext = new Dictionary<int, CandidateSizingEstimate>();
            var anySurvived = false;
            foreach (var ctx in ladder)
            {
                var estimate = CandidateSizingCalculator.Evaluate(candidate, ctx, profile);
                sizingByContext[ctx] = estimate;
                if (estimate.Status is MemoryFitStatus.Fits or MemoryFitStatus.Assumed)
                {
                    anySurvived = true;
                }
            }

            // If ALL contexts are pruned → record sizing-pruned rows, no pull.
            if (!anySurvived)
            {
                foreach (var ctx in ladder)
                {
                    var est = sizingByContext[ctx];
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

            // 5. Get the Modelfile num_ctx (if available) to cap the ladder.
            int? modelfileNumCtx = null;
            try
            {
                var show = await _catalogClient.GetShowAsync(url, candidate.Name, ct).ConfigureAwait(false);
                if (show is not null)
                {
                    modelfileNumCtx = ModelMetadataParser.ParseNumCtx(show);
                }
            }
            catch
            {
                // Fail-soft: no Modelfile cap.
            }

            var effectiveMaxCtx = Math.Min(candidate.NativeContext, modelfileNumCtx ?? candidate.NativeContext);

            // 6. Warmup: one generate at the smallest non-pruned context.
            var warmupCtx = ladder.Where(ctx => ctx <= effectiveMaxCtx &&
                sizingByContext[ctx].Status is MemoryFitStatus.Fits or MemoryFitStatus.Assumed)
                .FirstOrDefault();
            if (warmupCtx > 0)
            {
                await WarmupAsync(url, candidate.Name, warmupCtx, ct).ConfigureAwait(false);
            }

            // 7. Benchmark loop (ascending ladder).
            foreach (var ctx in ladder.Where(c => c <= effectiveMaxCtx))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var est = sizingByContext[ctx];

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

                // Surviving context: benchmark it.
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
    //  Warmup + benchmark
    // ------------------------------------------------------------------

    private async Task WarmupAsync(string? baseUrl, string modelName, int numCtx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        try
        {
            var body = new JsonObject
            {
                ["model"] = modelName,
                ["prompt"] = "Write a short sentence.",
                ["stream"] = false,
                ["options"] = new JsonObject
                {
                    ["num_ctx"] = numCtx,
                    ["num_predict"] = 16,
                    ["temperature"] = 0
                }
            };

            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(GenerateTimeout);

            using var warmupRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), GeneratePath)) { Content = content };
            await _httpClient
                .SendAsync(warmupRequest, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Warmup failure is non-fatal: the model may still work for the real benchmark.
            _logger.LogDebug(ex, "Warmup for {Model} at {NumCtx} ctx failed; continuing.", modelName, numCtx);
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
            // Build the deterministic filler prompt.
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
    /// Builds a deterministic real-text filler prompt whose estimated token count is ≈
    /// <paramref name="numCtx"/> (a few percent under is fine — the benchmark row records the
    /// measured prefill speed at whatever the model actually prefilled). The prompt must be
    /// *real text*, not padding bytes, so prefill is representative. Token math is O(1)
    /// per estimator call (tokens × repetitions, never a re-estimate of the whole string)
    /// so 262k contexts do not blow the estimator's sum. A fixed benchmark suffix is
    /// appended.
    /// </summary>
    internal string BuildFillerPrompt(int numCtx, string? modelName = null)
    {
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

        var suffixTokens = _tokenEstimator.EstimateTokens(Suffix, modelName);
        var targetFillerTokens = Math.Max(baseTokens, numCtx - suffixTokens - 8);

        var repetitions = Math.Max(1, targetFillerTokens / baseTokens);
        // Defensive cap: even a pathological estimator (tokens-per-char) must not yield an
        // unbounded prompt string. The target is then simply not reached.
        const int MaxRepetitions = 100_000;
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
