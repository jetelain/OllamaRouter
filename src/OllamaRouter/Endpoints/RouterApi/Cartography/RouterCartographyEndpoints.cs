using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using static OllamaRouter.Endpoints.RouterApi.Shared.RouterApiSharedHelpers;

namespace OllamaRouter.Endpoints.RouterApi.Cartography;

/// <summary>
/// The fleet cartography endpoint of the OllamaRouter programmatic management API:
/// <list type="bullet">
/// <item><c>GET /api/router/cartography</c> — the fleet cartography: in one call, every fleet
/// node (hardware summary, liveness, context lease allocation, VRAM totals, its currently
/// loaded processes with the live /api/ps quantization) plus every fleet model with its
/// catalog-sourced quantization and per-node deployment matrix (present / capable / context
/// ceiling / resident / loaded footprint), plus a flat list of all loaded model processes.
/// Reads in-memory tracker/liveness snapshots and the 30 s single-flight capability cache;
/// it NEVER triggers a fresh /api/ps refresh.</item>
/// </list>
/// </summary>

public static class RouterCartographyEndpoints
{
    /// <summary>
    /// Maps <c>GET /api/router/cartography</c>.
    /// </summary>
    public static WebApplication MapRouterCartographyEndpoints(this WebApplication app)
    {
        // Fleet cartography (task #41): the model-to-node deployment map — every node's
        // hardware/context/VRAM/loaded processes, every model's catalog-sourced quantization
        // and per-target deployment matrix, and a flat list of all loaded processes. Reads
        // in-memory snapshots + the 30 s capability cache; never calls /api/ps refresh.
        app.MapGet("/api/router/cartography", async (
            IOptionsMonitor<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var response = await BuildCartographyResponseAsync(options, serviceProvider, context, cancellationToken);
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterCartographyResponse);
        })
        .WithName("GetRouterCartography")
        .WithTags("Model Catalog & Cartography")
        .WithSummary("Fleet cartography: the model-to-node deployment map.")
        .WithDescription("In one call: every fleet node (hardware summary, liveness, context lease allocation, VRAM totals, its "
            + "currently loaded processes with the live /api/ps quantization — the Cloud pseudo-target row is appended last), "
            + "every fleet model with its catalog-sourced quantization and per-node deployment matrix (present / capable / "
            + "context ceiling / resident / loaded footprint), and a flat list of all loaded model processes. Reads in-memory "
            + "tracker/liveness snapshots and the 30 s single-flight capability cache; it NEVER triggers a fresh /api/ps refresh. "
            + "Read-only — no key required in default mode.");
        return app;
    }

    /// <summary>
    /// One cartography deployment row per (model, target) pair: the exact same qualification
    /// as the <c>GET /api/router/models/{name}</c> target matrix (present/capable/
    /// contextCeiling/vetoReason/isResident/inFlightRequests — same name-matching rule) plus
    /// the catalog-sourced <see cref="CartographyDeploymentRow.Quantization"/> (that target's
    /// metadata quantization, falling back to the primary fleet value) and
    /// <see cref="CartographyDeploymentRow.LoadedSizeVramBytes"/> (the tracker's exact
    /// <c>size_vram</c> of the resident process, null when not resident).
    /// </summary>
    private static async Task<CartographyDeploymentRow> BuildDeploymentRowAsync(
        string name,
        OllamaTargetOptions target,
        ModelMetadata? primaryMetadata,
        string? pinnedArchitecture,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetExecutionTracker? executionTracker,
        CancellationToken cancellationToken)
    {
        var qualification = await ComputeTargetQualificationAsync(
            name, target, primaryMetadata, pinnedArchitecture, catalogCache, capabilityEngine, executionTracker, cancellationToken)
            .ConfigureAwait(false);

        // The resident process the isResident flag matched (same ordinal-ignore-case raw or
        // NormalizeModelName rule); its size_vram is the exact loaded footprint.
        TargetExecutionState? executionState = null;
        var routingTarget = (RoutingTarget)target.Name!;
        try
        {
            executionState = executionTracker?.GetState(routingTarget);
        }
        catch
        {
            executionState = null;
        }

        long? loadedSizeVramBytes = null;
        if (qualification.IsResident && executionState is { } state)
        {
            var resident = state.LoadedModels.FirstOrDefault(m =>
                string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, RoutingDecisionService.NormalizeModelName(name), StringComparison.OrdinalIgnoreCase));
            if (resident is { } model)
            {
                loadedSizeVramBytes = model.SizeVramBytes;
            }
        }

        return new CartographyDeploymentRow(
            Target: qualification.Target,
            Present: qualification.Present,
            Capable: qualification.Capable,
            VetoReason: qualification.VetoReason,
            ContextCeiling: qualification.ContextCeiling,
            IsResident: qualification.IsResident,
            LoadedSizeVramBytes: loadedSizeVramBytes,
            InFlightRequests: qualification.InFlightRequests,
            Quantization: qualification.TargetMetadata?.QuantizationLevel);
    }

    // ------------------------------------------------------------------
    //  GET /api/router/cartography
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the fleet cartography: <c>nodes[]</c> from the in-memory liveness /
    /// hardware /<see cref="ITargetExecutionTracker"/> snapshots (the Cloud pseudo-target is
    /// appended last, mirroring <c>GET /api/router/fleet</c> semantics), <c>models[]</c> from
    /// the merged fleet catalog (+ cloud syntheses) with the shared summary/discovery helpers
    /// (30 s single-flight capability cache), and <c>loadedModels[]</c> as the flat,
    /// non-deduped cartesian of every node's loaded processes. EVERY sub-source is resolved
    /// defensively and degrades fail-soft (a minimal host answers 200 with degraded rows);
    /// the endpoint NEVER calls <see cref="ITargetExecutionTracker.RefreshAsync"/> — it only
    /// reads snapshots, so an unreachable node contributes its last-known data (flagged
    /// <c>stale</c>) and can never hang the request.
    /// </summary>
    private static async Task<RouterCartographyResponse> BuildCartographyResponseAsync(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var opt = options.CurrentValue;
        var effectiveTargets = opt.GetEffectiveTargets();
        var availability = serviceProvider.GetService<ITargetAvailabilityService>();
        var liveness = serviceProvider.GetService<ITargetLivenessService>();
        var livenessEntries = liveness?.GetEntries();
        var executionTracker = serviceProvider.GetService<ITargetExecutionTracker>();
        var leaseSnapshot = serviceProvider.GetService<IContextAutoShrinkManager>()?.GetLeaseSnapshot();
        var modelCatalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        var fleetHardware = serviceProvider.GetService<IFleetHardwareService>();
        var fleetAggregation = serviceProvider.GetService<IFleetAggregationService>();
        var capabilityEngine = serviceProvider.GetService<ModelCapabilityEngine>();

        // ---- nodes[] (effective targets in configuration order) ----
        var nodes = new List<CartographyNodeRow>(effectiveTargets.Count + 1);
        foreach (var target in effectiveTargets)
        {
            var name = target.Name!;
            var routingTarget = (RoutingTarget)name;
            var enabled = availability is not null &&
                availability.GetSnapshot().Flags.TryGetValue(routingTarget, out var targetEnabled)
                ? targetEnabled
                : true;

            // Liveness: the same "Online"/"Offline"/"Probing" vocabulary as /fleet (with the
            // catalog-based fallback when the liveness service is not registered).
            string livenessText;
            if (livenessEntries is { } entries && entries.TryGetValue(routingTarget, out var entry))
            {
                livenessText = entry.State switch
                {
                    TargetLivenessState.Online => "Online",
                    TargetLivenessState.Offline => "Offline",
                    _ => "Probing"
                };
            }
            else
            {
                livenessText = await CatalogHasAnyModelsAsync(modelCatalogCache, target.Url, cancellationToken)
                    ? "Online"
                    : "Offline";
            }

            // Hardware summary from the cached fleet hardware store (fail-soft).
            NodeHardwareProfile? nodeProfile = null;
            try
            {
                nodeProfile = fleetHardware?.GetProfile(name);
            }
            catch
            {
                nodeProfile = null;
            }

            string? gpuName = null;
            string? gpuVendor = null;
            if (nodeProfile?.Gpus.Count > 0)
            {
                gpuName = nodeProfile.Gpus[0].ModelName;
                gpuVendor = nodeProfile.Gpus[0].Vendor;
            }
            else if (nodeProfile is not null && nodeProfile.IsUnifiedMemory)
            {
                gpuVendor = string.Equals(nodeProfile.Architecture, "arm64", StringComparison.OrdinalIgnoreCase)
                    ? "Apple"
                    : "Unified";
                gpuName = $"{nodeProfile.Architecture} Unified Memory";
            }

            // Tracker snapshot ONLY (never RefreshAsync — the 2 s hosted service owns /api/ps).
            TargetExecutionState? executionState = null;
            try
            {
                executionState = executionTracker?.GetState(routingTarget);
            }
            catch
            {
                executionState = null;
            }

            nodes.Add(new CartographyNodeRow(
                Name: name,
                Url: target.Url,
                Location: TargetLocationClassifier.ToWireValue(TargetLocationClassifier.Classify(target)),
                Enabled: enabled,
                Liveness: livenessText,
                Stale: executionState?.Stale ?? false,
                LastRefreshedAtUtc: executionState is not null && executionState.LastRefreshedAtUtc != DateTimeOffset.MinValue
                    ? executionState.LastRefreshedAtUtc
                    : null,
                Hardware: new CartographyNodeHardware(
                    GpuName: gpuName,
                    GpuVendor: gpuVendor,
                    TotalVramMB: target.GetEffectiveMaxVramMB(fleetHardware),
                    IsUnifiedMemory: nodeProfile?.IsUnifiedMemory ?? false,
                    SystemRamMB: nodeProfile?.SystemRamMB is { } ram and > 0 ? ram : null),
                ContextAllocation: FormatContextAllocation(routingTarget, leaseSnapshot),
                InFlightRequests: executionState?.InFlightRequests ?? 0,
                TotalLoadedVramBytes: executionState?.TotalVramBytes ?? 0L,
                LoadedModels: executionState?.LoadedModels.Select(m => new CartographyNodeLoadedModel(
                    Name: m.Name,
                    SizeVramBytes: m.SizeVramBytes,
                    Quantization: m.QuantizationLevel,
                    ContextLength: m.ContextLength)).ToArray() ?? []));
        }

        // The Cloud pseudo-target row (last), mirroring the /fleet cloud semantics: never
        // probed/tracked, "Online" iff cloud is enabled, no hardware, baseline allocation.
        var cloudEnabled = opt.CloudOverflow.Enabled;
        var cloudFlag = availability is not null &&
            availability.GetSnapshot().Flags.TryGetValue(RoutingTarget.Cloud, out var cloudEnabledFlag)
            ? cloudEnabledFlag
            : cloudEnabled;
        nodes.Add(new CartographyNodeRow(
            Name: RoutingTarget.Cloud.Value,
            Url: null,
            Location: TargetLocationClassifier.ToWireValue(TargetLocation.Cloud),
            Enabled: cloudFlag,
            Liveness: cloudEnabled ? "Online" : "Offline",
            Stale: false,
            LastRefreshedAtUtc: null,
            Hardware: null,
            ContextAllocation: "baseline",
            InFlightRequests: 0,
            TotalLoadedVramBytes: 0L,
            LoadedModels: []));

        // ---- models[] (merged fleet catalog + cloud syntheses) ----
        var names = await GetFleetModelNamesAsync(opt, availability, fleetAggregation, modelCatalogCache, context, cancellationToken)
            .ConfigureAwait(false);

        // Names are independent: run the per-model discovery concurrently (the metadata calls
        // hit the 30 s single-flight capability cache; a second call within the window
        // reissues zero /api/show requests).
        var modelRows = await Task.WhenAll(names.Select(async name =>
        {
            var data = await ComputeModelSummaryAsync(
                name, opt, effectiveTargets, modelCatalogCache, capabilityEngine, availability, cancellationToken)
                .ConfigureAwait(false);

            var primaryMetadata = data.PrimaryMetadata;
            var pinnedArchitecture = opt.Models.TryGetValue(
                RoutingDecisionService.NormalizeModelName(name), out var rowThresholds) ? rowThresholds.Architecture : null;

            // One deployment row per effective target (declaration order); the Cloud
            // pseudo-target never appears here (it has no OllamaTargetOptions). A model hosted
            // on NO fleet target (e.g. a cloud-only synthesis — PresentOnFleet is false, the
            // same signal the detail endpoint uses via AnyTargetHasModelAsync) carries an
            // empty deployments[], exactly like /api/router/models/{name}.
            var deployments = !data.PresentOnFleet
                ? []
                : await Task.WhenAll(effectiveTargets.Select(target => BuildDeploymentRowAsync(
                    name, target, primaryMetadata, pinnedArchitecture,
                    modelCatalogCache, capabilityEngine, executionTracker, cancellationToken)))
                    .ConfigureAwait(false);

            return new CartographyModelRow(
                Name: data.Summary.Name,
                Architecture: data.Summary.Architecture,
                ParameterCount: data.Summary.ParameterCount,
                WeightSizeBytes: primaryMetadata?.WeightSizeBytes ?? 0L,
                Quantization: primaryMetadata?.QuantizationLevel,
                NativeContextWindow: data.Summary.NativeContextWindow,
                RecommendedContextWindow: data.Summary.RecommendedContextWindow,
                CloudOverflowAvailable: data.Summary.CloudOverflowAvailable,
                Deployments: deployments);
        })).ConfigureAwait(false);

        // ---- loadedModels[] (flat: target order, then tracker order; no cross-target dedup) ----
        var loadedModels = nodes
            .Where(node => node.LoadedModels.Count > 0)
            .SelectMany(node => node.LoadedModels.Select(m => new CartographyLoadedModelRow(
                Name: m.Name,
                Target: node.Name,
                SizeVramBytes: m.SizeVramBytes,
                Quantization: m.Quantization,
                ContextLength: m.ContextLength,
                ContextAllocation: node.ContextAllocation,
                Liveness: node.Liveness,
                Stale: node.Stale)))
            .ToArray();

        return new RouterCartographyResponse(nodes, modelRows, loadedModels);
    }
}
