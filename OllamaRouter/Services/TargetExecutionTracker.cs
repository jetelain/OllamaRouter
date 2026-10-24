using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Default implementation of <see cref="ITargetExecutionTracker"/>: a per-target slot table
/// holding the last parsed <c>GET /api/ps</c> model list (guarded by the slot's own lock) plus
/// an <see cref="Interlocked"/>-based in-flight counter (never under the lock, so
/// increment/decrement stays O(1) and unblockable), a single coalescing in-flight
/// refresh gate (the in-flight task field guarded by a dedicated lock), and
/// a 2 s TTL so at most one fan-out runs per window.
/// <para>
/// The refresh reuses the EXACT routable predicate of <see cref="FleetAggregationService"/>
/// (enabled, non-Cloud, non-blank URL, and <c>IsOnline || (withinGrace &amp;&amp; Unknown)</c>)
/// so aggregation and routing never disagree about which daemons are live. Each per-target
/// fetch is <c>FetchArrayBoundedAsync(url, "/api/ps", "models", budget, ct)</c> — one
/// blackholed daemon can never hold up the tick. A per-target failure keeps the previous data
/// and marks the slot stale (stale-while-revalidate); the refresh never throws.
/// </para>
/// </summary>
public sealed class TargetExecutionTracker : ITargetExecutionTracker
{
    /// <summary>Default: how long a successful refresh result is trusted before the next fan-out.</summary>
    private static readonly TimeSpan DefaultRefreshTtl = TimeSpan.FromSeconds(2);

    /// <summary>Fallback per-target budget when the aggregation option is missing or invalid.</summary>
    private static readonly TimeSpan DefaultPerTargetBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// During the first 10 s after the liveness prober started, an unresolved (Unknown) target
    /// is treated as refresh-eligible (identical to the middleware's routable definition) so
    /// the tracker never disagrees with routing.
    /// </summary>
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(10);

    private readonly OllamaRouterOptions _options;
    private readonly ITargetLivenessService _liveness;
    private readonly IOllamaModelCatalogClient _catalogClient;
    private readonly ILogger<TargetExecutionTracker> _logger;
    private readonly TimeSpan _refreshTtl;
    private readonly TimeSpan _perTargetBudget;
    // Auto-Shrink Manager lease invalidation hook (Phase 3, item 2): invoked per evicted model
    // name on a successful /api/ps refresh. OPTIONAL - defaults to null so existing unit tests
    // construct the tracker unmodified; Program.cs injects the singleton via the SP factory.
    private readonly IContextAutoShrinkManager? _contextShrinkManager;
    private readonly ConcurrentDictionary<RoutingTarget, TargetExecutionSlot> _slots = new();

    // When (UTC) the last fan-out completed (success or failure); MinValue = never refreshed.
    private long _lastRefreshCompletedTicks = DateTimeOffset.MinValue.UtcTicks;

    // Task the coalescing gate hands to concurrent callers; null when idle. Guarded by
    // _refreshGate; the non-null-ness of this field IS the "a refresh is running" flag.
    private Task? _inFlightRefreshTask;

    private readonly object _refreshGate = new();

    /// <summary>
    /// Creates the tracker. <paramref name="refreshTtl"/> (default 2 s) drives the refresh
    /// period and the stale-while-fresh guard; <paramref name="perTargetBudget"/> (default
    /// <see cref="AggregationOptions.PerTargetTimeoutMs"/>, 500 ms fallback) bounds each
    /// parallel <c>/api/ps</c> fetch. <paramref name="targetAvailability"/> is part of the
    /// shared routable predicate's DI surface (enabled-ness itself is enforced via the
    /// configuration's Enabled flag, matching <see cref="FleetAggregationService"/>).
    /// </summary>
    public TargetExecutionTracker(
        IOptions<OllamaRouterOptions> options,
        ITargetLivenessService liveness,
        ITargetAvailabilityService targetAvailability,
        IOllamaModelCatalogClient catalogClient,
        ILogger<TargetExecutionTracker> logger,
        TimeSpan? refreshTtl = null,
        TimeSpan? perTargetBudget = null,
        IContextAutoShrinkManager? contextShrinkManager = null)
        : this(options.Value, liveness, targetAvailability, catalogClient, logger, refreshTtl, perTargetBudget, contextShrinkManager)
    {
    }

    /// <summary>
    /// Test-friendly constructor accepting an already-resolved options instance (same defaults
    /// as the <see cref="IOptions{TOptions}"/> overload). <paramref name="contextShrinkManager"/>
    /// is the optional Auto-Shrink Manager lease-invalidation hook (null = no lease resets,
    /// the pre-Phase-3 behavior) so existing unit tests keep compiling unmodified.
    /// </summary>
    public TargetExecutionTracker(
        OllamaRouterOptions options,
        ITargetLivenessService liveness,
        ITargetAvailabilityService targetAvailability,
        IOllamaModelCatalogClient catalogClient,
        ILogger<TargetExecutionTracker> logger,
        TimeSpan? refreshTtl = null,
        TimeSpan? perTargetBudget = null,
        IContextAutoShrinkManager? contextShrinkManager = null)
    {
        _options = options;
        _liveness = liveness;
        _catalogClient = catalogClient;
        _logger = logger;
        _contextShrinkManager = contextShrinkManager;
        _refreshTtl = refreshTtl ?? DefaultRefreshTtl;

        // Per-target budget: the injected override, else the configured aggregation budget,
        // with the same 500 ms fallback as FleetAggregationService.GetPerTargetBudget.
        var ms = perTargetBudget?.TotalMilliseconds ?? options.Aggregation?.PerTargetTimeoutMs ?? 500;
        _perTargetBudget = ms > 0 ? TimeSpan.FromMilliseconds(ms) : DefaultPerTargetBudget;
    }

    /// <summary>The refresh period driven by the hosted timer service (the tracker's TTL).</summary>
    public TimeSpan RefreshTtl => _refreshTtl;

    public TargetExecutionState? GetState(RoutingTarget target)
    {
        // Cloud is a relay, never probed, never has /api/ps — never tracked; likewise any
        // name that is not a configured fleet target (no slot is created for unknown names,
        // so snapshots can never drift out of sync with GetAllStates).
        if (target == RoutingTarget.Cloud || !_liveness.GetFleetTargets().Contains(target))
        {
            return null;
        }

        return BuildState(target);
    }

    public IReadOnlyList<TargetExecutionState> GetAllStates()
    {
        var results = new List<TargetExecutionState>();
        foreach (var target in _liveness.GetFleetTargets())
        {
            if (target == RoutingTarget.Cloud)
            {
                continue; // Defensive: GetFleetTargets already excludes Cloud.
            }

            results.Add(BuildState(target));
        }

        return results;
    }

    public void IncrementInFlight(RoutingTarget target)
    {
        var slot = _slots.GetOrAdd(target, _ => new TargetExecutionSlot());
        Interlocked.Increment(ref slot.InFlight);
    }

    public void DecrementInFlight(RoutingTarget target)
    {
        var slot = _slots.GetOrAdd(target, _ => new TargetExecutionSlot());

        // Floor at 0: an unbalanced decrement (no matching increment) can never drive the
        // counter negative, keeping later snapshots honest.
        var value = Interlocked.Decrement(ref slot.InFlight);
        if (value < 0)
        {
            Interlocked.Add(ref slot.InFlight, 1);
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Coalescing gate: at most one refresh runs at a time; concurrent callers await the
        // SAME Task. The task field itself is the gate — it is assigned under _refreshGate
        // before any other thread can observe it, so a caller that sees a non-null task is
        // guaranteed to share a refresh that is genuinely in flight (no null window).
        lock (_refreshGate)
        {
            if (_inFlightRefreshTask is not null)
            {
                return _inFlightRefreshTask;
            }

            var task = RefreshOnceAsync(cancellationToken);
            // Only hold the coalescing gate while the refresh is genuinely in flight. If the
            // fan-out completed synchronously (e.g. all targets resolved inline), RefreshOnceAsync
            // has already run its finally and released the gate — re-assigning the completed task
            // here would wedge the gate non-null and block every subsequent refresh.
            if (!task.IsCompleted)
            {
                _inFlightRefreshTask = task;
            }

            return task;
        }

        async Task RefreshOnceAsync(CancellationToken ct)
        {
            try
            {
                // Stale-while-fresh guard: if the last completed fan-out is still within the
                // TTL, skip the network entirely (the hosted timer's 2 s cadence would fire a
                // second refresh within the same window after a manual trigger).
                var lastTicks = Volatile.Read(ref _lastRefreshCompletedTicks);
                if (lastTicks != 0 &&
                    DateTimeOffset.UtcNow - new DateTimeOffset(lastTicks, TimeSpan.Zero) < _refreshTtl)
                {
                    return;
                }

                var targets = GetRefreshTargets();
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    // Fan out to every online target in parallel; each fetch is individually
                    // bounded by the per-target budget so one slow daemon never holds the tick.
                    await Task.WhenAll(targets.Select(t => RefreshTargetAsync(t, linkedCts.Token)));
                }
                finally
                {
                    linkedCts.Dispose();
                }

                Volatile.Write(ref _lastRefreshCompletedTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
            catch (Exception ex)
            {
                // The refresh must never throw (it runs from a hosted timer); a fan-out-level
                // failure is logged and the next tick retries.
                _logger.LogWarning(ex, "Execution-state refresh tick failed; will retry on the next tick.");
            }
            finally
            {
                // Release the gate: the next caller starts a fresh refresh (its own TTL guard
                // will skip the network if we just completed within the window).
                lock (_refreshGate)
                {
                    _inFlightRefreshTask = null;
                }
            }
        }
        }

    /// <summary>
    /// Builds the refresh target list with the EXACT routable predicate shared with
    /// <see cref="FleetAggregationService"/>: enabled, non-Cloud, non-blank URL, and
    /// <c>IsOnline || (withinStartupGrace &amp;&amp; State == Unknown)</c>. Offline targets
    /// are skipped (never queried).
    /// </summary>
    private List<(RoutingTarget Target, string Url)> GetRefreshTargets()
    {
        var withinGrace = IsWithinStartupGrace();
        var entries = _liveness.GetEntries();
        return _options.GetEffectiveTargets(_logger)
            .Where(t => t.Enabled &&
                        !string.Equals(t.Name, RoutingTarget.Cloud.Value, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(t.Url) &&
                        (_liveness.IsOnline((RoutingTarget)t.Name!) ||
                         (withinGrace &&
                          entries.TryGetValue((RoutingTarget)t.Name!, out var entry) &&
                          entry.State == TargetLivenessState.Unknown)))
            .Select(t => ((RoutingTarget)t.Name!, t.Url!.TrimEnd('/')))
            .ToList();
    }

    private async Task RefreshTargetAsync((RoutingTarget Target, string Url) target, CancellationToken cancellationToken)
    {
        var (routingTarget, url) = target;
        (JsonArray? Array, bool Succeeded) result;
        try
        {
            result = await _catalogClient.FetchArrayBoundedAsync(url, "/api/ps", "models", _perTargetBudget, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The refresh itself was cancelled (shutdown/tick cancel): not a liveness signal,
            // leave the slot exactly as it was.
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fetching /api/ps from {Target} failed during the execution-state refresh.", routingTarget);
            result = (null, false);
        }

        var slot = _slots.GetOrAdd(routingTarget, _ => new TargetExecutionSlot());

        var evicted = new List<string>();
        lock (slot.ModelGate)
        {
            if (result.Succeeded)
            {
                var parsedModels = ParseModels(result.Array!);
                slot.Models = parsedModels;
                slot.Stale = false;
                slot.LastRefreshedAtUtc = DateTimeOffset.UtcNow;

                // Eviction diff (Phase 3, item 2): models seen on the LAST successful refresh
                // but absent from THIS one have been evicted (or never loaded). Their KV cache
                // is gone, so their auto-shrink leases must not survive a reload with a fresh
                // (un-sized) context. Comparison is over normalized names (tag stripped) so a
                // name reported with/without ":latest" still matches.
                var currentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var model in parsedModels)
                {
                    if (!string.IsNullOrWhiteSpace(model.Name))
                    {
                        currentNames.Add(RoutingDecisionService.NormalizeModelName(model.Name));
                    }
                }

                var evictedNames = new List<string>();
                if (slot.LastSeenModelNames.Count > 0)
                {
                    foreach (var previousName in slot.LastSeenModelNames)
                    {
                        if (!currentNames.Contains(previousName))
                        {
                            evictedNames.Add(previousName);
                        }
                    }
                }

                slot.LastSeenModelNames = currentNames;
                slot.LastEvictedModelNames = evictedNames;
                evicted = evictedNames;
            }
            else
            {
                // Stale-while-revalidate: keep the previous model data, mark the slot stale so
                // consumers can discount it. In-flight counts are always fresh regardless.
                slot.Stale = true;
            }
        }

        if (result.Succeeded && evicted.Count > 0 && _contextShrinkManager is { } shrinkManager)
        {
            // Outside the gate (the reset APIs are idempotent and non-blocking). The list was
            // captured under the gate above, so it is exactly the diff of THIS refresh; a
            // concurrent refresh replaces the slot's diff but can only evict names already
            // covered by its own diff (its reset runs with its own captured list).
            foreach (var name in evicted)
            {
                shrinkManager.ResetModelLease(routingTarget, name);
            }
        }
    }

    /// <summary>
    /// Returns the model names (normalized, tag-stripped) that were loaded on the target at the
    /// LAST successful refresh but are absent from the CURRENT one - i.e. the models evicted by
    /// the most recent successful refresh (empty when the target has never been successfully
    /// refreshed or nothing was evicted). Each returned name also had its auto-shrink lease
    /// reset by the refresh (when the <see cref="IContextAutoShrinkManager"/> was injected).
    /// </summary>
    public IReadOnlyList<string> GetEvictedModelNames(RoutingTarget target)
    {
        if (_slots.TryGetValue(target, out var slot))
        {
            lock (slot.ModelGate)
            {
                return new List<string>(slot.LastEvictedModelNames);
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Fail-soft <c>JsonNode</c> parsing of a /api/ps payload: <c>name</c> (falling back to
    /// <c>model</c>) is the model name, <c>size_vram</c> is parsed as long (missing/invalid → 0),
    /// and <c>num_ctx</c> becomes the best-effort <see cref="TargetLoadedModel.ContextLength"/>
    /// only when present and numeric (Ollama does not expose it today; later phases may).
    /// </summary>
    internal static List<TargetLoadedModel> ParseModels(JsonArray models)
    {
        var parsed = new List<TargetLoadedModel>(models.Count);
        foreach (var node in models)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            var name = obj["name"]?.GetValue<string>() ?? obj["model"]?.GetValue<string>() ?? string.Empty;
            var sizeVram = 0L;
            if (obj["size_vram"] is JsonValue vramValue && vramValue.TryGetValue(out long? vram) && vram.HasValue)
            {
                sizeVram = vram.Value;
            }

            int? contextLength = null;
            if (obj["num_ctx"] is JsonValue ctxValue && ctxValue.TryGetValue(out int? ctx) && ctx.HasValue)
            {
                contextLength = ctx.Value;
            }

            parsed.Add(new TargetLoadedModel(name, sizeVram, contextLength));
        }

        return parsed;
    }

    // Builds the immutable snapshot for one target: slots for unseen (but configured)
    // targets are filled in with an empty, stale state (MinValue timestamp) so callers
    // always get an entry.
    private TargetExecutionState BuildState(RoutingTarget target)
    {
        if (_slots.TryGetValue(target, out var slot))
        {
            return BuildStateFromSlot(target, slot);
        }

        return new TargetExecutionState(
            target,
            0,
            (IReadOnlyList<TargetLoadedModel>)Array.Empty<TargetLoadedModel>(),
            0L,
            DateTimeOffset.MinValue,
            true);
    }

    private static TargetExecutionState BuildStateFromSlot(RoutingTarget target, TargetExecutionSlot slot)
    {
        // Copy the model list and compute the aggregate under the model gate (the copied list
        // and its aggregate are taken together, so they are mutually consistent).
        List<TargetLoadedModel> models;
        long totalVram;
        DateTimeOffset lastRefreshedAt;
        bool stale;
        lock (slot.ModelGate)
        {
            models = new List<TargetLoadedModel>(slot.Models);
            totalVram = 0;
            foreach (var m in models)
            {
                totalVram += m.SizeVramBytes;
            }

            lastRefreshedAt = slot.LastRefreshedAtUtc;
            stale = slot.Stale;
        }

        // Read the counter outside the gate (lock-free; never lock-protected).
        var inFlight = Interlocked.CompareExchange(ref slot.InFlight, 0, 0);

        return new TargetExecutionState(target, inFlight, models, totalVram, lastRefreshedAt, stale);
    }

    // Same 10 s startup grace window as the middleware / fleet aggregation.
    private bool IsWithinStartupGrace()
    {
        var start = _liveness.ServiceStartTimeUtc;
        return start != DateTimeOffset.MinValue &&
               DateTimeOffset.UtcNow - start < StartupGracePeriod;
    }

    /// <summary>
    /// Mutable per-target execution state. <see cref="ModelGate"/> guards only the model-data
    /// fields (list, timestamp, stale flag, seen/evicted name sets); the <see cref="InFlight"/>
    /// counter is manipulated exclusively with <see cref="Interlocked"/> and is never under the
    /// gate, so counters cannot block on (or be blocked by) snapshot/refresh work.
    /// </summary>
    private sealed class TargetExecutionSlot
    {
        public object ModelGate { get; } = new();

        public List<TargetLoadedModel> Models { get; set; } = [];
        public DateTimeOffset LastRefreshedAtUtc { get; set; } = DateTimeOffset.MinValue;
        public bool Stale { get; set; } = true;

        // Normalized (tag-stripped) model names from the LAST SUCCESSFUL refresh (empty before
        // the first success), plus the names evicted by that last successful refresh.
        public HashSet<string> LastSeenModelNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> LastEvictedModelNames { get; set; } = Array.Empty<string>();

        // In-flight counter, manipulated with Interlocked only (floor-at-0 on decrement).
        public int InFlight;
    }
}
