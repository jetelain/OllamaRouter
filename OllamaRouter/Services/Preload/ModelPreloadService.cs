using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints.RouterApi.Shared;
using OllamaRouter.Options;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Preload;

/// <summary>
/// Default <see cref="IModelPreloadService"/> implementation (a registered singleton; the
/// endpoints resolve it defensively so minimal test hosts keep working). Selection is a pure,
/// preview-style reuse of the capability machinery (the same per-target path as
/// <c>GET /api/router/models/{name}</c>: catalog presence → digest-keyed /api/show metadata →
/// <see cref="ModelCapabilityEngine.DeriveCapability"/>) — no routing decision, no scoring
/// pipeline, no lease mutation, and the daemon is called directly by the catalog client only
/// on the POST path.
/// </summary>
public sealed class ModelPreloadService : IModelPreloadService
{
    /// <summary>
    /// The "large intent" bar (tokens): at/above this context intent, the ranking prefers
    /// server/cloud-class nodes over desktop-class among idle candidates (key 2); below it the
    /// server-class key is a no-op (least-busy / declaration order decide).
    /// </summary>
    public const int LargeIntentThresholdTokens = 65_536;

    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly IOllamaModelCatalogClient _catalogClient;
    private readonly IModelCatalogCacheService? _catalogCache;
    private readonly ModelCapabilityEngine? _capabilityEngine;
    private readonly ITargetLivenessService? _liveness;
    private readonly ITargetAvailabilityService? _availability;
    private readonly ITargetExecutionTracker? _executionTracker;
    private readonly IFleetHardwareService? _fleetHardware;
    private readonly ILogger<ModelPreloadService> _logger;

    public ModelPreloadService(
        IOptionsMonitor<OllamaRouterOptions> options,
        IOllamaModelCatalogClient catalogClient,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetLivenessService? liveness,
        ITargetAvailabilityService? availability,
        ITargetExecutionTracker? executionTracker,
        IFleetHardwareService? fleetHardware,
        ILogger<ModelPreloadService> logger)
    {
        _options = options;
        _catalogClient = catalogClient;
        _catalogCache = catalogCache;
        _capabilityEngine = capabilityEngine;
        _liveness = liveness;
        _availability = availability;
        _executionTracker = executionTracker;
        _fleetHardware = fleetHardware;
        _logger = logger;
    }

    public Task<ModelPreloadResult?> PreloadAsync(
        string modelName,
        int? keepAliveSeconds,
        int? contextIntentTokens,
        string? pinnedTarget,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(modelName, keepAliveSeconds, contextIntentTokens, pinnedTarget, executeLoad: true, cancellationToken);

    public Task<ModelPreloadResult?> PreviewAsync(
        string modelName,
        int? keepAliveSeconds,
        int? contextIntentTokens,
        string? pinnedTarget,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(modelName, keepAliveSeconds, contextIntentTokens, pinnedTarget, executeLoad: false, cancellationToken);

    // ------------------------------------------------------------------
    //  Core: selection (shared by POST and preview) + optional execution
    // ------------------------------------------------------------------

    private async Task<ModelPreloadResult?> ExecuteAsync(
        string modelName,
        int? keepAliveSeconds,
        int? contextIntentTokens,
        string? pinnedTarget,
        bool executeLoad,
        CancellationToken cancellationToken)
    {
        var selection = await SelectCoreAsync(modelName, contextIntentTokens, pinnedTarget, cancellationToken).ConfigureAwait(false);

        // 404 shape: the model (or the explicit pin) is unknown on the fleet.
        if (selection.Error is not null)
        {
            _logger.LogInformation("Preload of '{Model}' rejected: {Error}", modelName, selection.Error);
            return new ModelPreloadResult(
                Model: selection.ResolvedModel ?? modelName,
                SelectedTarget: null,
                AppliedKeepAliveSeconds: keepAliveSeconds ?? 0,
                AppliedNumCtx: null,
                ContextIntentTokens: selection.Intent,
                LoadAccepted: null,
                ResidencyConfirmed: null,
                PreloadDurationMs: null,
                Candidates: selection.Candidates,
                Reason: null,
                Error: selection.Error);
        }

        if (selection.Selected is null)
        {
            // 503 shape: nothing qualified; the candidates carry the exact skip reasons.
            var noEligibleReason = $"no eligible target for model {selection.ResolvedModel} (intent {selection.Intent} tokens)";
            _logger.LogWarning("Preload of '{Model}' found no eligible target (intent {Intent} tokens): {Reasons}",
                selection.ResolvedModel, selection.Intent,
                string.Join("; ", selection.Candidates.Where(c => c.SkipReason is not null).Select(c => $"{c.Target}: {c.SkipReason}")));
            return new ModelPreloadResult(
                Model: selection.ResolvedModel!,
                SelectedTarget: null,
                AppliedKeepAliveSeconds: keepAliveSeconds ?? 0,
                AppliedNumCtx: null,
                ContextIntentTokens: selection.Intent,
                LoadAccepted: null,
                ResidencyConfirmed: null,
                PreloadDurationMs: null,
                Candidates: selection.Candidates,
                Reason: noEligibleReason,
                Error: null);
        }

        var selected = selection.Selected!;
        var resolvedName = selection.ResolvedModel!;
        var intent = selection.Intent!.Value;
        var appliedKeepAlive = keepAliveSeconds ?? selected.GetEffectivePreloadKeepAliveSeconds(_options.CurrentValue, _fleetHardware);
        var appliedNumCtx = Math.Min(intent, selection.SelectedCeiling!.Value);

        bool? loadAccepted = null;
        bool? residencyConfirmed = null;
        long? durationMs = null;
        if (executeLoad)
        {
            var started = Environment.TickCount64;
            var outcome = await _catalogClient.PreloadModelAsync(selected.Url, resolvedName, appliedKeepAlive, appliedNumCtx, cancellationToken).ConfigureAwait(false);
            durationMs = Environment.TickCount64 - started;
            loadAccepted = outcome.Accepted;
            if (outcome.Error is not null)
            {
                _logger.LogWarning("Preload load call to '{Target}' for '{Model}' not accepted: {Error}", selected.Name, resolvedName, outcome.Error);
            }

            // Bounded, best-effort residency confirmation (the same 4 s client bound; fail-soft
            // list → false). A failed confirmation reports false but never fails the request —
            // the load call itself is the contract.
            var running = await _catalogClient.GetRunningModelNamesAsync(selected.Url, cancellationToken).ConfigureAwait(false);
            var normalized = RoutingDecisionService.NormalizeModelName(resolvedName);
            residencyConfirmed = running.Any(name =>
                string.Equals(name, resolvedName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase));
        }

        var winner = selection.Candidates
            .FirstOrDefault(c => string.Equals(c.Target, selected.Name, StringComparison.OrdinalIgnoreCase));
        var reason = $"selected {selected.Name}: in-flight {winner.InFlightRequests}, context ceiling {winner.ContextCeiling} covers intent {intent} tokens, keep_alive {appliedKeepAlive}";
        _logger.LogInformation(
            "Preload '{Model}' on '{Target}' (keep_alive {KeepAlive}, num_ctx {NumCtx}): accepted={Accepted}, residency={Residency}, {Duration} ms",
            resolvedName, selected.Name, appliedKeepAlive, appliedNumCtx, loadAccepted, residencyConfirmed, durationMs);

        return new ModelPreloadResult(
            Model: resolvedName,
            SelectedTarget: selected.Name,
            AppliedKeepAliveSeconds: appliedKeepAlive,
            AppliedNumCtx: appliedNumCtx,
            ContextIntentTokens: intent,
            LoadAccepted: loadAccepted,
            ResidencyConfirmed: residencyConfirmed,
            PreloadDurationMs: durationMs,
            Candidates: selection.Candidates,
            Reason: reason,
            Error: null);
    }

    private sealed record Selection(
        string? ResolvedModel,
        string? Error,
        OllamaTargetOptions? Selected,
        int? Intent,
        int? SelectedCeiling,
        IReadOnlyList<PreloadCandidateEvaluation> Candidates);

    /// <summary>
    /// One internal per-target gate result before ranking.
    /// </summary>
    private sealed class EvaluatedCandidate
    {
        public required OllamaTargetOptions Target { get; init; }
        public required int DeclarationIndex { get; init; }
        public bool Eligible { get; set; }
        public string? SkipReason { get; set; }
        public int InFlightRequests { get; init; }
        public bool IsResident { get; init; }
        public int ContextCeiling { get; set; } = ModelCapabilityEngine.OllamaDefaultContext;
        public bool ServerClass { get; init; }
        public ModelMetadata? Metadata { get; set; }
    }

    private async Task<Selection> SelectCoreAsync(
        string modelName,
        int? contextIntentTokens,
        string? pinnedTarget,
        CancellationToken cancellationToken)
    {
        var effectiveTargets = _options.CurrentValue.GetEffectiveTargets();

        // ---- 1. Model name resolution (the exact /api/router/models/{name} rules): the fleet
        // catalog (first-seen order + cloud syntheses when Cloud overflow is on), then the
        // tagless-prefix fallback, then — when namespace filtering is active — the direct
        // per-target catalog bypass. The service has no HttpContext: the aggregation consumer
        // tolerates null (passing null skips the fan-out fast path; the per-target catalog
        // branch still enumerates every target AND the cloud syntheses). Cloud-synthesis-only
        // models resolve here but then fail the per-target model-presence gate (the Cloud
        // pseudo-target is never a preload candidate) → 503 with the skip reasons.
        var names = await RouterApiSharedHelpers.GetFleetModelNamesAsync(
            _options.CurrentValue, _availability, fleetAggregation: null, _catalogCache, context: null, cancellationToken).ConfigureAwait(false);
        var resolved = RouterApiSharedHelpers.ResolveModelName(modelName, names);
        if (resolved is null && _options.CurrentValue.FilterNamespacedModels)
        {
            resolved = await RouterApiSharedHelpers.ResolveModelOnFleetAsync(
                _catalogCache, effectiveTargets, modelName, cancellationToken).ConfigureAwait(false);
        }

        if (resolved is null)
        {
            return new Selection(null, "model not found", null, contextIntentTokens, null, []);
        }

        // ---- 2. Optional pin: overrides SELECTION (not the safety gates) — a pinned-but-
        // ineligible target produces a single-candidate 503, never a silent bypass.
        IReadOnlyList<OllamaTargetOptions> toEvaluate;
        if (!string.IsNullOrWhiteSpace(pinnedTarget))
        {
            var pinned = effectiveTargets.FirstOrDefault(t =>
                string.Equals(t.Name, pinnedTarget, StringComparison.OrdinalIgnoreCase));
            if (pinned is null)
            {
                return new Selection(resolved, $"target not found: {pinnedTarget}", null, contextIntentTokens, null, []);
            }

            toEvaluate = [pinned];
        }
        else
        {
            toEvaluate = effectiveTargets;
        }

        // ---- 3. Pass A: the ordered per-target gates (routable → present → discoverable) plus
        // the tracker snapshot / server-class flags, in declaration order.
        var evaluated = new List<EvaluatedCandidate>(toEvaluate.Count);
        var declarationIndexOf = effectiveTargets
            .Select((t, index) => (Target: t, Index: index))
            .ToDictionary(p => (object)p.Target, p => p.Index, ReferenceEqualityComparer.Instance);

        foreach (var target in toEvaluate)
        {
            var (inFlight, isResident) = GetTrackerSnapshot(target, resolved);
            var entry = new EvaluatedCandidate
            {
                Target = target,
                DeclarationIndex = declarationIndexOf.TryGetValue((object)target, out var index) ? index : 0,
                InFlightRequests = inFlight,
                IsResident = isResident,
                ServerClass = IsServerClass(target)
            };

            var (routable, routableSkip) = IsRoutableForPreload(target);
            if (!routable)
            {
                entry.SkipReason = routableSkip;
                evaluated.Add(entry);
                continue;
            }

            if (!await IsModelPresentAsync(target, resolved, cancellationToken).ConfigureAwait(false))
            {
                entry.SkipReason = "model not present in target catalog";
                evaluated.Add(entry);
                continue;
            }

            var metadata = await GetTargetMetadataAsync(target, resolved, cancellationToken).ConfigureAwait(false);
            if (metadata is null || _capabilityEngine is null)
            {
                // Undiscoverable capability (no catalog digest / no capability engine): excluded.
                // The permissive routing fallback is a ROUTING concern, not a preload one —
                // preloading onto a node where the ceiling cannot be computed is out of contract.
                entry.SkipReason = "capability undiscoverable";
                evaluated.Add(entry);
                continue;
            }

            entry.Metadata = metadata;
            evaluated.Add(entry);
        }

        // Primary fleet metadata (the first hoster with model_info; the same rule as
        // BuildModelSummaryAsync) for the per-target metadata merge.
        ModelMetadata? primaryMetadata = null;
        foreach (var entry in evaluated)
        {
            if (entry.Metadata is { } meta)
            {
                if (primaryMetadata is null || (!primaryMetadata.HasModelInfo && meta.HasModelInfo))
                {
                    primaryMetadata = meta;
                }
            }
        }

        // ---- 4. Pass B: capability derivation + hardware gate + the effective ceiling
        // (MoE extensible capacity / dense CMax — the exact BuildModelSummaryAsync rule), then
        // recommendedContextWindow (max ceiling over the hardware-capable hosters; the Ollama
        // default sentinel when none) and the active intent (explicit request value or the
        // recommended-window soft fallback).
        var normalizedName = RoutingDecisionService.NormalizeModelName(resolved);
        var pinnedArchitecture = _options.CurrentValue.Models.TryGetValue(normalizedName, out var thresholds) ? thresholds.Architecture : null;

        var recommended = ModelCapabilityEngine.OllamaDefaultContext;
        foreach (var entry in evaluated)
        {
            if (entry.Metadata is not { } meta)
            {
                continue;
            }

            var capability = _capabilityEngine!.DeriveCapability(
                RouterApiSharedHelpers.MergeMetadata(meta, primaryMetadata),
                entry.Target.Name!, entry.Target, pinnedArchitecture);
            if (!capability.HardwareCapable)
            {
                entry.SkipReason = capability.Architecture == ModelArchitectureKind.LargeDense
                    ? ModelCapabilityEngine.LargeDenseVetoReason
                    : "hardware incapable";
                continue;
            }

            entry.ContextCeiling = capability.Architecture == ModelArchitectureKind.Moe
                ? ModelCapabilityEngine.GetMoeExtensibleCapacity(capability, thresholds?.MaxContextTokens)
                : capability.CMax;
            recommended = Math.Max(recommended, entry.ContextCeiling);
        }

        var intent = contextIntentTokens ?? recommended;
        // Explicit request intents are gated WITH the reasoning-fit margin (intent + margin ≤
        // ceiling, via EvaluateClientContext with no client upper bound). The recommended-
        // window SOFT fallback (zero-parameter requests) is the fleet's own best ceiling by
        // construction, so it is gated as an upper-bound preference (clientRequestedNumCtx =
        // intent → effective requirement = intent): otherwise the +margin gate would exclude
        // every target and the parameterless preload could never 200 (AC1).
        var softIntent = contextIntentTokens is null;

        foreach (var entry in evaluated)
        {
            if (entry.Metadata is null || entry.SkipReason is not null)
            {
                continue;
            }

            var qualified = ModelCapabilityEngine.EvaluateClientContext(
                intent,
                softIntent ? intent : null,
                entry.ContextCeiling,
                _options.CurrentValue.ContextFitMarginTokens).Qualified;
            if (!qualified)
            {
                entry.SkipReason = $"context ceiling {entry.ContextCeiling} below intent {intent}";
            }
            else
            {
                entry.Eligible = true;
            }
        }

        // ---- 5. Rank the eligible candidates (stable): idle first (the core busy-bypass
        // requirement), then server/cloud-class first for large intents, then least in-flight,
        // then declaration order (mirrors the pipeline's deterministic tie-break).
        var largeIntent = intent >= LargeIntentThresholdTokens;
        var selected = evaluated
            .Where(e => e.Eligible)
            .OrderBy(e => e.InFlightRequests == 0 ? 0 : 1)
            .ThenBy(e => largeIntent && e.ServerClass ? 0 : 1)
            .ThenBy(e => e.InFlightRequests)
            .ThenBy(e => e.DeclarationIndex)
            .FirstOrDefault();

        var candidates = evaluated
            .OrderBy(e => e.DeclarationIndex)
            .Select(e => new PreloadCandidateEvaluation(
                Target: e.Target.Name!,
                Eligible: e.Eligible,
                SkipReason: e.SkipReason,
                InFlightRequests: e.InFlightRequests,
                IsResident: e.IsResident,
                ContextCeiling: e.ContextCeiling,
                ServerClass: e.ServerClass,
                Busy: e.InFlightRequests > 0))
            .ToList();

        return new Selection(
            ResolvedModel: resolved,
            Error: null,
            Selected: selected?.Target,
            Intent: intent,
            SelectedCeiling: selected?.ContextCeiling,
            Candidates: candidates);
    }

    // ------------------------------------------------------------------
    //  Gate / snapshot helpers (preview-style; no inference, no lease mutation)
    // ------------------------------------------------------------------

    /// <summary>
    /// The shared routability predicate, null-guarded: <see cref="TargetRoutability.IsRoutable"/>
    /// dereferences BOTH service parameters, so when exactly one is unregistered the target is
    /// skipped (the helper cannot be called safely); when BOTH are absent (minimal host) the
    /// check degrades to the fail-open catalog-fallback posture (enabled + non-blank URL).
    /// </summary>
    private (bool Routable, string? SkipReason) IsRoutableForPreload(OllamaTargetOptions target)
    {
        if (_liveness is null && _availability is null)
        {
            return target.Enabled && !string.IsNullOrWhiteSpace(target.Url)
                ? (true, null)
                : (false, "not routable (disabled/offline)");
        }

        if (_liveness is null || _availability is null)
        {
            return (false, "not routable (disabled/offline)");
        }

        try
        {
            return TargetRoutability.IsRoutable(target, _availability, _liveness)
                ? (true, null)
                : (false, "not routable (disabled/offline)");
        }
        catch
        {
            return (false, "not routable (disabled/offline)");
        }
    }

    /// <summary>Catalog presence on one target (fail-soft: unreachable → false).</summary>
    private async Task<bool> IsModelPresentAsync(OllamaTargetOptions target, string modelName, CancellationToken cancellationToken)
    {
        if (_catalogCache is null || string.IsNullOrWhiteSpace(target.Url))
        {
            return false;
        }

        try
        {
            return await _catalogCache.ModelExistsAsync(target.Url, modelName, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Digest-keyed per-target metadata (the GetTargetMetadataAsync path of the model-detail
    /// pipeline, presence already established by the caller). Returns null when there is no
    /// catalog digest (undiscoverable) or the discovery throws.
    /// </summary>
    private async Task<ModelMetadata?> GetTargetMetadataAsync(OllamaTargetOptions target, string modelName, CancellationToken cancellationToken)
    {
        if (_catalogCache is null || _capabilityEngine is null || string.IsNullOrWhiteSpace(target.Url))
        {
            return null;
        }

        try
        {
            var digest = await _catalogCache.GetCurrentDigestAsync(target.Url, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(digest))
            {
                return null;
            }

            return await _capabilityEngine.GetMetadataAsync(target.Url, modelName, digest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// In-memory tracker snapshot for one target (null tracker ⇒ 0 in-flight / not resident):
    /// the resident check uses the exact raw-or-<see cref="RoutingDecisionService.NormalizeModelName(string)"/>
    /// name rule of the model-detail matrix.
    /// </summary>
    private (int InFlight, bool IsResident) GetTrackerSnapshot(OllamaTargetOptions target, string model)
    {
        TargetExecutionState? state = null;
        try
        {
            state = _executionTracker?.GetState(new RoutingTarget(target.Name!));
        }
        catch
        {
            state = null;
        }

        var inFlight = state?.InFlightRequests ?? 0;
        var normalized = RoutingDecisionService.NormalizeModelName(model);
        var isResident = state?.LoadedModels.Any(m =>
            string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Name, normalized, StringComparison.OrdinalIgnoreCase)) ?? false;
        return (inFlight, isResident);
    }

    /// <summary>Server-class (the large-intent ranking preference): cloud-class OR VRAM ≥ the server threshold.</summary>
    private bool IsServerClass(OllamaTargetOptions target)
    {
        var nodeClass = PreloadKeepAlivePolicy.GetPreloadNodeClass(target, target.GetEffectiveMaxVramMB(_fleetHardware));
        return nodeClass is PreloadNodeClass.Server or PreloadNodeClass.Cloud;
    }
}
