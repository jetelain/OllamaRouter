using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Lightweight, programmatic management API for the OllamaRouter gateway:
/// <list type="bullet">
/// <item><c>GET /api/router/info</c> — instantly differentiates OllamaRouter from a
/// standard Ollama daemon (which returns 404 for this route) and reports the router
/// version, mode, fleet summary and active features. Pure in-memory snapshot reads.</item>
/// <item><c>GET /api/router/fleet</c> — the full fleet hardware topology: every configured
/// target (in configuration order) plus the Cloud pseudo-target (last), with real-time
/// liveness state, last-probe latency, detected GPU hardware (local workstation only),
/// in-flight request count, currently loaded model and the active KV-cache context lease.</item>
/// <item><c>GET /api/router/models</c> and <c>GET /api/router/models/{modelName}</c> — the
/// zero-config model capability catalog: per-model architecture classification (Dense/Moe),
/// parameter count, native Modelfile num_ctx and the router-calculated hardware-safe context
/// ceiling (recommendedContextWindow = max CMax over the hardware-capable fleet targets;
/// cloud overflow is never a ceiling contributor, only the cloudOverflowAvailable hint).</item>
/// <item><c>POST /api/router/vram/reclaim</c> and <c>POST /api/router/fleet/unload</c> (exact
/// alias, identical contract) — fleet VRAM reclaim: unloads the running models on one named
/// target (case-insensitive; unknown name → 404) or on ALL effective configured targets
/// (fail-soft per target) and optionally administratively disables the processed target(s);
/// KV-cache leases of every processed target are reset to the baseline bucket.</item>
/// <item><c>POST /api/router/context/evaluate</c> — stateless routing + dynamic context sizing
/// preview: which target would service a request for the given prompt size and which
/// num_ctx bucket would be injected (no LLM inference; only catalog /api/tags + /api/show
/// discovery).</item>
/// </list>
/// The /info and /fleet handlers build exclusively from in-memory snapshots
/// (<c>GetEntries</c>/<c>GetState</c>/<c>GetLeaseSnapshot</c>/availability flags/configuration)
/// — no statistics, no history/7-day aggregation, and no blocking per-target HTTP (only the
/// single awaited local GPU CLI snapshot when a local workstation target is present). All
/// enrichment services are resolved defensively (<see cref="ServiceProvider.GetService(Type)"/>)
/// so a minimal host with only options + availability still serves every endpoint.
/// </summary>
public static class RouterApiEndpointsExtensions
{
    public static WebApplication MapOllamaRouterApiEndpoints(this WebApplication app)
    {
        app.MapGet("/api/router/info", async (
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            var (fleet, features) = await BuildInfoBlocksAsync(options, serviceProvider, cancellationToken);
            return Results.Json(new RouterInfoResponse(GetRouterVersion(), true, fleet, features),
                OllamaRouterJsonSerializerContext.Default.RouterInfoResponse);
        });

        app.MapGet("/api/router/fleet", async (
            IOptions<OllamaRouterOptions> options,
            ITargetAvailabilityService targetAvailability,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            var response = await BuildFleetResponseAsync(options, targetAvailability, serviceProvider, cancellationToken);
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterFleetResponse);
        });

        // Fleet hardware profiling engine: the local node's own profile (cached after the
        // first probe), the full fleet topology (parallel peer probes, fail-soft per node),
        // and the manual profile push (for bare Ollama targets without a router daemon).
        app.MapGet("/api/router/hardware", async (IHardwareProfileProvider provider, CancellationToken cancellationToken) =>
        {
            var profile = await provider.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(profile, OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile);
        });

        app.MapGet("/api/router/fleet/hardware", async (IFleetHardwareService fleetHardware, CancellationToken cancellationToken) =>
        {
            var nodes = await fleetHardware.GetFleetHardwareAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new RouterFleetHardwareResponse(nodes), OllamaRouterJsonSerializerContext.Default.RouterFleetHardwareResponse);
        });

        app.MapPut("/api/router/fleet/hardware/{targetName}", async (HttpRequest request, string targetName, IFleetHardwareService fleetHardware, CancellationToken cancellationToken) =>
        {
            NodeHardwareProfile? profile = null;
            try
            {
                profile = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Malformed JSON body → 400 below.
            }

            if (profile is null || string.IsNullOrWhiteSpace(profile.NodeName))
            {
                return Results.Json(
                    new OllamaErrorResponse("invalid or missing hardware profile body"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var stored = await fleetHardware.UpsertProfileAsync(targetName, profile, cancellationToken).ConfigureAwait(false);
            if (!stored)
            {
                return Results.Json(
                    new OllamaErrorResponse($"target not found: {targetName}"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(profile, OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile);
        });

        app.MapGet("/api/router/models", async (
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var response = await BuildModelsListAsync(options, serviceProvider, context, cancellationToken);
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterModelsResponse);
        });

        app.MapGet("/api/router/models/{modelName}", async (
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            HttpContext context,
            string modelName,
            CancellationToken cancellationToken) =>
        {
            var detail = await BuildModelDetailAsync(options, serviceProvider, context, modelName, cancellationToken);
            if (detail is null)
            {
                return Results.Json(
                    new OllamaErrorResponse("model not found"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(detail, OllamaRouterJsonSerializerContext.Default.RouterModelDetailResponse);
        });

        // Canonical fleet VRAM reclaim + exact alias (identical handler, parameters and
        // response shape). Both paths unload the running models on the selected effective
        // target(s) — or ALL effective targets when no target is specified (fail-soft per
        // target; the Cloud pseudo-target is never part of the effective list).
        app.MapPost("/api/router/vram/reclaim", ReclaimVramHandler);
        app.MapPost("/api/router/fleet/unload", ReclaimVramHandler);

        app.MapPost("/api/router/context/evaluate", async (
            HttpRequest request,
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            RouterContextEvaluateRequest? evaluateRequest = null;
            try
            {
                evaluateRequest = await System.Text.Json.JsonSerializer.DeserializeAsync(
                    request.Body,
                    OllamaRouterJsonSerializerContext.Default.RouterContextEvaluateRequest,
                    cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed JSON body: fall through to the validation errors below.
            }

            if (string.IsNullOrWhiteSpace(evaluateRequest?.Model))
            {
                return Results.Json(
                    new OllamaErrorResponse("model is required"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (evaluateRequest.EstimatedTokens < 1)
            {
                return Results.Json(
                    new OllamaErrorResponse("estimatedTokens must be >= 1"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await EvaluateContextPreviewAsync(options, serviceProvider, evaluateRequest, cancellationToken);
            if (result is null)
            {
                return Results.Json(
                    new OllamaErrorResponse("no available routing target"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Json(result, OllamaRouterJsonSerializerContext.Default.RouterContextEvaluateResponse);
        });

        return app;
    }

    // ------------------------------------------------------------------
    //  POST /api/router/vram/reclaim (+ POST /api/router/fleet/unload alias)
    // ------------------------------------------------------------------

    /// <summary>
    /// Fleet VRAM reclaim handler (canonical path <c>/api/router/vram/reclaim</c>, exact
    /// alias <c>/api/router/fleet/unload</c>): unloads the running models on the selected
    /// effective target (case-insensitive name match; unknown name → 404) or on ALL
    /// effective configured targets (fail-soft per target) when no target is specified.
    /// Parameters are accepted via query string and/or an optional JSON body; body values
    /// win over query values. Optional <c>disableTarget</c> (default false) administratively
    /// disables each processed target via <see cref="ITargetAvailabilityService"/> (persisted).
    /// KV-cache leases of every processed target are reset so the next request starts at
    /// the baseline bucket. Dependency-free beyond options + catalog client (the
    /// availability service and context-shrink manager are resolved defensively).
    /// </summary>
    private static async Task<IResult> ReclaimVramHandler(
        HttpRequest request,
        IOptions<OllamaRouterOptions> options,
        IOllamaModelCatalogClient catalogClient,
        IServiceProvider serviceProvider,
        [FromQuery] string? target,
        [FromQuery] bool? disableTarget,
        CancellationToken cancellationToken)
    {
        // Optional JSON body: a malformed body falls through to the query values (the same
        // pattern as /api/router/context/evaluate), so a bad body never 400s a request the
        // query string already satisfies.
        RouterVramReclaimRequest? body = null;
        try
        {
            body = await System.Text.Json.JsonSerializer.DeserializeAsync(
                request.Body,
                OllamaRouterJsonSerializerContext.Default.RouterVramReclaimRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON body: fall through to the query values.
        }

        // Body values win over query values: an explicit body is authoritative for both fields
        // (a body with absent fields uses the record defaults: target null, disableTarget false);
        // an absent/malformed body falls back entirely to the query values.
        var effectiveTargetName = body is not null
            ? (string.IsNullOrWhiteSpace(body.Target) ? target : body.Target)
            : target;
        var effectiveDisable = body is not null
            ? body.DisableTarget
            : disableTarget ?? false;

        var effectiveTargets = options.Value.GetEffectiveTargets();
        if (effectiveTargets.Count == 0)
        {
            return Results.Json(
                new RouterVramReclaimResponse(
                    Success: false,
                    TotalUnloaded: 0,
                    Targets: [],
                    Message: "no targets configured"),
                OllamaRouterJsonSerializerContext.Default.RouterVramReclaimResponse);
        }

        // Single target: case-insensitive match against the effective fleet (D4: unknown → 404).
        IReadOnlyList<OllamaTargetOptions> selected;
        if (!string.IsNullOrWhiteSpace(effectiveTargetName))
        {
            var match = effectiveTargets.FirstOrDefault(t =>
                string.Equals(t.Name, effectiveTargetName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return Results.Json(
                    new OllamaErrorResponse($"target not found: {effectiveTargetName}"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            selected = [match];
        }
        else
        {
            // Fleet-wide: ALL effective configured targets regardless of liveness/enablement
            // (unreachable daemons fail soft per target via StopRunningModelsAsync).
            selected = effectiveTargets;
        }

        var availability = serviceProvider.GetService<ITargetAvailabilityService>();
        var shrinkManager = serviceProvider.GetService<IContextAutoShrinkManager>();

        // Concurrent per-target unload (fail-soft; results preserve the configured order).
        var results = await Task.WhenAll(selected.Select(t =>
            VramReclaimCore.ReclaimTargetAsync(
                t, effectiveDisable, catalogClient, availability, shrinkManager, cancellationToken)))
            .ConfigureAwait(false);

        return Results.Json(
            new RouterVramReclaimResponse(
                Success: true,
                TotalUnloaded: results.Sum(r => r.UnloadedModels.Count),
                Targets: results),
            OllamaRouterJsonSerializerContext.Default.RouterVramReclaimResponse);
    }

    // ------------------------------------------------------------------
    //  GET /api/router/info
    // ------------------------------------------------------------------

    private static async Task<(RouterFleetSummary Fleet, RouterFeatures Features)> BuildInfoBlocksAsync(
        IOptions<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
        var effectiveTargets = opt.GetEffectiveTargets();
        var liveness = serviceProvider.GetService<ITargetLivenessService>();
        var livenessEntries = liveness?.GetEntries();
        var modelCatalogCache = serviceProvider.GetService<IModelCatalogCacheService>();

        var onlineTargets = 0;
        foreach (var target in effectiveTargets)
        {
            var isOnline = livenessEntries is { } entries && entries.TryGetValue((RoutingTarget)target.Name!, out var entry)
                ? entry.State == TargetLivenessState.Online
                : // No liveness service registered: fall back to the catalog-based check
                  // (the same rule as the monitor's reduced liveness view).
                  await CatalogHasAnyModelsAsync(modelCatalogCache, target.Url, cancellationToken);
            if (isOnline)
            {
                onlineTargets++;
            }
        }

        var cloudEnabled = opt.CloudOverflow.Enabled;
        if (cloudEnabled)
        {
            onlineTargets++;
        }

        var features = new RouterFeatures(
            DynamicContext: opt.DynamicContext.Enabled,
            AutoShrink: opt.DynamicContext.Enabled,
            Failover: opt.MaxConsecutiveFailures > 0,
            AntiThrashingAffinity: true);

        return (new RouterFleetSummary(effectiveTargets.Count + 1, onlineTargets, cloudEnabled), features);
    }

    private static async Task<bool> CatalogHasAnyModelsAsync(
        IModelCatalogCacheService? modelCatalogCache,
        string? url,
        CancellationToken cancellationToken)
    {
        if (modelCatalogCache is null || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            return await modelCatalogCache.HasAnyModelsAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failing catalog check must never break the info payload.
            return false;
        }
    }

    private static string GetRouterVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    // ------------------------------------------------------------------
    //  GET /api/router/fleet
    // ------------------------------------------------------------------

    private static async Task<RouterFleetResponse> BuildFleetResponseAsync(
        IOptions<OllamaRouterOptions> options,
        ITargetAvailabilityService targetAvailability,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
        var effectiveTargets = opt.GetEffectiveTargets();
        var availability = targetAvailability.GetSnapshot();
        var liveness = serviceProvider.GetService<ITargetLivenessService>();
        var livenessEntries = liveness?.GetEntries();
        var executionTracker = serviceProvider.GetService<ITargetExecutionTracker>();
        var leaseSnapshot = serviceProvider.GetService<IContextAutoShrinkManager>()?.GetLeaseSnapshot();
        var gpuVramProvider = serviceProvider.GetService<IGpuVramProvider>();
        var modelCatalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        var hardwareProvider = serviceProvider.GetService<IHardwareProfileProvider>();
        var fleetHardware = serviceProvider.GetService<IFleetHardwareService>();

        // The local workstation target (URL host 127.0.0.1/localhost or name "Local") gets the
        // live local GPU snapshot; only the FIRST such target receives it.
        var localGpuSnapshot = LocalGpuSnapshot.Unknown;
        var localTargetAssigned = false;

        var targets = new List<RouterTargetItem>(effectiveTargets.Count + 1);
        foreach (var target in effectiveTargets)
        {
            var name = target.Name!;
            var isLocalWorkstation = !localTargetAssigned && IsLocalWorkstationTarget(target);
            if (isLocalWorkstation && gpuVramProvider is not null)
            {
                try
                {
                    localGpuSnapshot = await gpuVramProvider.GetLocalGpuSnapshotAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A failing CLI probe must never break the fleet view.
                    localGpuSnapshot = LocalGpuSnapshot.Unknown;
                }
            }
            localTargetAssigned |= isLocalWorkstation;

            var routingTarget = (RoutingTarget)name;
            var enabled = availability.Flags.TryGetValue(routingTarget, out var targetEnabled) ? targetEnabled : true;

            // Liveness: prefer the raw (non-TTL) liveness state machine; fall back to the
            // catalog-based check when the liveness service is not registered.
            string livenessText;
            long? latencyMs = null;
            if (livenessEntries is { } entries && entries.TryGetValue(routingTarget, out var entry))
            {
                livenessText = entry.State switch
                {
                    TargetLivenessState.Online => "Online",
                    TargetLivenessState.Offline => "Offline",
                    _ => "Probing"
                };
                latencyMs = entry.State == TargetLivenessState.Online ? entry.LastProbeLatencyMs : null;
            }
            else
            {
                livenessText = await CatalogHasAnyModelsAsync(modelCatalogCache, target.Url, cancellationToken)
                    ? "Online"
                    : "Offline";
            }

            var executionState = executionTracker?.GetState(routingTarget);
            var activeModel = executionState?.LoadedModels.FirstOrDefault()?.Name;
            var inFlight = executionState?.InFlightRequests ?? 0;

            // speedFactor: always present; null when the effective factor is 1.0 (unset/default),
            // consistent with maxVramMB's nullable semantics.
            var speedFactor = target.GetEffectiveSpeedFactor();

            // Memory-tier enrichment from the fleet hardware store: the local workstation
            // target gets the local profile (cached after the first probe); other targets
            // get their stored profile (manual/peer/persisted). Missing profile → all
            // defaults (fail-soft, additive/backward-compatible fields).
            NodeHardwareProfile? nodeProfile = null;
            try
            {
                if (isLocalWorkstation)
                {
                    nodeProfile = hardwareProvider is not null
                        ? await hardwareProvider.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false)
                        : null;
                }
                else
                {
                    nodeProfile = fleetHardware?.GetProfile(name);
                }
            }
            catch
            {
                nodeProfile = null;
            }

            targets.Add(new RouterTargetItem(
                Name: name,
                Url: target.Url,
                Enabled: enabled,
                Liveness: livenessText,
                LatencyMs: latencyMs,
                Hardware: new RouterTargetHardware(
                    HardwareHint: target.HardwareHint,
                    GpuVendor: isLocalWorkstation ? localGpuSnapshot.Vendor : "Unknown",
                    Devices: isLocalWorkstation ? localGpuSnapshot.Devices : Array.Empty<string>(),
                    MaxVramMB: target.GetEffectiveMaxVramMB(),
                    MaxParallel: target.MaxParallel,
                    SpeedFactor: speedFactor == 1.0 ? null : speedFactor,
                    IsUnifiedMemory: nodeProfile?.IsUnifiedMemory ?? false,
                    SystemRamMB: nodeProfile?.SystemRamMB is { } ram ? ram : null,
                    MemoryType: nodeProfile is null ? null : DescribeMemoryTier(nodeProfile),
                    BandwidthGBs: nodeProfile?.EffectiveBandwidthGBs is { } bw and > 0 ? bw : null),
                Execution: new RouterTargetExecution(
                    InFlightRequests: inFlight,
                    ActiveModel: activeModel,
                    ContextAllocation: FormatContextAllocation(routingTarget, leaseSnapshot))));
        }

        // The Cloud pseudo-target is never probed/tracked: it is "Online" iff cloud is
        // enabled, never has a loaded model or in-flight requests, and never carries a
        // context lease or a latency measurement.
        var cloudEnabled = opt.CloudOverflow.Enabled;
        var cloudFlag = availability.Flags.TryGetValue(RoutingTarget.Cloud, out var cloudEnabledFlag) ? cloudEnabledFlag : cloudEnabled;
        targets.Add(new RouterTargetItem(
            Name: RoutingTarget.Cloud.Value,
            Url: null,
            Enabled: cloudFlag,
            Liveness: cloudEnabled ? "Online" : "Offline",
            LatencyMs: null,
            Hardware: new RouterTargetHardware(
                HardwareHint: "cloud",
                GpuVendor: "Cloud",
                Devices: new[] { "Cloud Overflow" },
                MaxVramMB: null,
                MaxParallel: 10,
                SpeedFactor: null),
            Execution: new RouterTargetExecution(
                InFlightRequests: 0,
                ActiveModel: null,
                ContextAllocation: "baseline")));

        return new RouterFleetResponse(targets);
    }

    /// <summary>
    /// A target IS this workstation iff its configured URL host is <c>127.0.0.1</c>/<c>localhost</c>
    /// (case-insensitive) or its <c>Name</c> equals <c>"Local"</c> (case-insensitive).
    /// </summary>
    private static bool IsLocalWorkstationTarget(OllamaTargetOptions target)
    {
        if (string.Equals(target.Name, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(target.Url) &&
               Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) &&
               (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Human-readable memory tier label for the fleet view: the system RAM tier wins
    /// ("DDR5", "LPDDR5X", "AppleUnified"), falling back to the fastest GPU VRAM tier
    /// ("GDDR6X", "HBM3") and finally "Unknown".
    /// </summary>
    private static string DescribeMemoryTier(NodeHardwareProfile profile)
    {
        // Canonical tier labels (spec examples: "DDR5", "LPDDR5X", "AppleUnified", "GDDR6X").
        var ramLabel = profile.SystemRamType switch
        {
            SystemRamType.Ddr3 => "DDR3",
            SystemRamType.Ddr4 => "DDR4",
            SystemRamType.Ddr5 => "DDR5",
            SystemRamType.LpDdr5X => "LPDDR5X",
            SystemRamType.AppleUnified => "AppleUnified",
            _ => null
        };
        if (ramLabel is not null)
        {
            return ramLabel;
        }

        if (profile.Gpus is { Count: > 0 })
        {
            var types = profile.Gpus.Select(g => g.VramType)
                .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, "Unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
            if (types.Count > 0)
            {
                return string.Join("/", types);
            }
        }

        return "Unknown";
    }

    // Renders the current context allocation for one fleet target from the lease snapshot:
    // any expanded lease -> "<maxBucket>k (expanded)"; a single baseline lease ->
    // "<bucket>k"; no leases at all (or no manager) -> "baseline". Cloud is always "baseline".
    private static string FormatContextAllocation(
        RoutingTarget target,
        IReadOnlyDictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>? leaseSnapshot)
    {
        if (target == RoutingTarget.Cloud || leaseSnapshot is null ||
            !leaseSnapshot.TryGetValue(target, out var leases) || leases.Count == 0)
        {
            return "baseline";
        }

        if (leases.Any(lease => lease.IsExpanded))
        {
            return $"{FormatBucketTokens(leases.Max(lease => lease.ActiveBucket))} (expanded)";
        }

        return FormatBucketTokens(leases[0].ActiveBucket);
    }

    private static string FormatBucketTokens(int tokens)
    {
        if (tokens >= 1_000_000)
        {
            return TrimTrailingZero((tokens / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + "M";
        }
        if (tokens >= 1_000)
        {
            return TrimTrailingZero((tokens / 1_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + "k";
        }

        return tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // "32.0" → "32" (spec string set: "32k", "262.1k", "1.2M"); "65.5" stays "65.5".
    private static string TrimTrailingZero(string value) => value.EndsWith(".0", StringComparison.Ordinal)
        ? value[..^2]
        : value;

    // ------------------------------------------------------------------
    //  GET /api/router/models + GET /api/router/models/{modelName}
    // ------------------------------------------------------------------

    private static async Task<RouterModelsResponse> BuildModelsListAsync(
        IOptions<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
        var effectiveTargets = opt.GetEffectiveTargets();
        var fleetAggregation = serviceProvider.GetService<IFleetAggregationService>();
        var catalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        var capabilityEngine = serviceProvider.GetService<ModelCapabilityEngine>();
        var availability = serviceProvider.GetService<ITargetAvailabilityService>();

        var names = await GetFleetModelNamesAsync(opt, availability, fleetAggregation, catalogCache, context, cancellationToken)
            .ConfigureAwait(false);

        // Names are independent: run the per-model discovery concurrently (the metadata calls
        // hit the 30 s single-flight capability cache).
        var summaries = await Task.WhenAll(names.Select(name => BuildModelSummaryAsync(
            name,
            opt,
            effectiveTargets,
            catalogCache,
            capabilityEngine,
            availability,
            cancellationToken))).ConfigureAwait(false);

        return new RouterModelsResponse(summaries);
    }

    private static async Task<RouterModelDetailResponse?> BuildModelDetailAsync(
        IOptions<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        HttpContext context,
        string modelName,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
        var effectiveTargets = opt.GetEffectiveTargets();
        var fleetAggregation = serviceProvider.GetService<IFleetAggregationService>();
        var catalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        var capabilityEngine = serviceProvider.GetService<ModelCapabilityEngine>();
        var availability = serviceProvider.GetService<ITargetAvailabilityService>();
        var executionTracker = serviceProvider.GetService<ITargetExecutionTracker>();

        var names = await GetFleetModelNamesAsync(opt, availability, fleetAggregation, catalogCache, context, cancellationToken)
            .ConfigureAwait(false);
        var resolved = ResolveModelName(modelName, names);
        if (resolved is null)
        {
            return null;
        }

        var summary = await BuildModelSummaryAsync(
            resolved,
            opt,
            effectiveTargets,
            catalogCache,
            capabilityEngine,
            availability,
            cancellationToken).ConfigureAwait(false);

        // Cloud-only synthesis entry: no fleet target hosts it, so the target matrix is empty
        // (the Cloud pseudo-target is never listed — it has no OllamaTargetOptions).
        if (!await AnyTargetHasModelAsync(catalogCache, effectiveTargets, resolved, cancellationToken).ConfigureAwait(false))
        {
            return new RouterModelDetailResponse(
                summary.Name,
                summary.Architecture,
                summary.ParameterCount,
                summary.NativeContextWindow,
                summary.RecommendedContextWindow,
                []);
        }

        var targets = await BuildTargetCompatibilityMatrixAsync(
            resolved,
            opt,
            effectiveTargets,
            catalogCache,
            capabilityEngine,
            executionTracker,
            cancellationToken).ConfigureAwait(false);

        return new RouterModelDetailResponse(
            summary.Name,
            summary.Architecture,
            summary.ParameterCount,
            summary.NativeContextWindow,
            summary.RecommendedContextWindow,
            targets);
    }

    /// <summary>
    /// Merged fleet model names (first-seen order) plus the cloud synthesis names (appended
    /// when not already present): the unified catalog of local, remote LAN nodes, and cloud
    /// overflow models. The cloud syntheses are included only when the Cloud overflow switch
    /// AND the targets.json Cloud toggle are both on (same rules as the /api/tags endpoint).
    /// </summary>
    private static async Task<List<string>> GetFleetModelNamesAsync(
        OllamaRouterOptions opt,
        ITargetAvailabilityService? availability,
        IFleetAggregationService? fleetAggregation,
        IModelCatalogCacheService? catalogCache,
        HttpContext? context,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();

        if (fleetAggregation is not null)
        {
            try
            {
                var merged = await fleetAggregation.GetTagsFleetAsync(
                    BuildCloudTagSyntheses(opt, availability), context).ConfigureAwait(false);
                foreach (var entry in merged)
                {
                    if (entry?["name"]?.ToString() is { Length: > 0 } name && !ContainsName(names, name))
                    {
                        names.Add(name);
                    }
                }
            }
            catch
            {
                // A failing aggregation must never break the model capability listing.
            }
        }
        else if (catalogCache is not null)
        {
            // Degraded enumeration: no fan-out service, so read each effective target's
            // catalog directly in declaration order.
            foreach (var target in opt.GetEffectiveTargets())
            {
                if (string.IsNullOrWhiteSpace(target.Url))
                {
                    continue;
                }

                try
                {
                    foreach (var name in await catalogCache.RefreshAsync(target.Url, cancellationToken).ConfigureAwait(false))
                    {
                        if (!ContainsName(names, name))
                        {
                            names.Add(name);
                        }
                    }
                }
                catch
                {
                    // Unreachable target contributes nothing.
                }
            }

            foreach (var synthesis in BuildCloudTagSyntheses(opt, availability) ?? [])
            {
                if (synthesis?["name"]?.ToString() is { Length: > 0 } name && !ContainsName(names, name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Cloud synthesis model names (per-model <c>Models[*].CloudModel</c> in configuration
    /// order, then <c>CloudOverflow.DefaultModel</c>): the same rules and JSON shape as the
    /// /api/tags endpoint's syntheses. Returns null when Cloud overflow is unavailable
    /// (static switch off OR the targets.json Cloud toggle off).
    /// </summary>
    private static IReadOnlyList<JsonNode>? BuildCloudTagSyntheses(OllamaRouterOptions options, ITargetAvailabilityService? availability)
    {
        if (!options.CloudOverflow.Enabled ||
            (availability is not null && !availability.IsEnabled(RoutingTarget.Cloud)))
        {
            return null;
        }

        var syntheses = new List<JsonNode>();
        if (options.Models != null)
        {
            foreach (var (_, modelOpts) in options.Models)
            {
                if (!string.IsNullOrWhiteSpace(modelOpts.CloudModel))
                {
                    syntheses.Add(MakeCloudSynthesisNode(modelOpts.CloudModel));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(options.CloudOverflow.DefaultModel))
        {
            syntheses.Add(MakeCloudSynthesisNode(options.CloudOverflow.DefaultModel));
        }

        return syntheses.Count > 0 ? syntheses : null;
    }

    private static JsonNode MakeCloudSynthesisNode(string cloudName) => new JsonObject
    {
        ["name"] = cloudName,
        ["model"] = cloudName,
        ["modified_at"] = DateTime.UtcNow.ToString("o"),
        ["size"] = 0,
        ["details"] = new JsonObject
        {
            ["family"] = "cloud",
            ["parameter_size"] = "cloud",
            ["quantization_level"] = "cloud"
        }
    };

    /// <summary>
    /// Resolves an incoming model name against the fleet catalog: case-insensitive exact
    /// full name (tag included) first, then the tagless-prefix fallback (strip the incoming
    /// name after ':' — or use the whole tagless name — and match any catalog entry sharing
    /// that prefix, normally the ":latest" variant). Cloud synthesis names are catalog
    /// entries like any other.
    /// Returns null (→ 404) when nothing matches.
    /// </summary>
    private static string? ResolveModelName(string requested, IReadOnlyList<string> names)
    {
        var exact = names.FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var colonIndex = requested.IndexOf(':');
        var prefix = colonIndex > 0 ? requested[..colonIndex] : requested;
        if (prefix.Length == 0)
        {
            return null;
        }

        return names.FirstOrDefault(name =>
            name.IndexOf(':') > 0 &&
            string.Equals(name[..name.IndexOf(':')], prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsName(IReadOnlyList<string> names, string candidate) =>
        names.Any(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when any effective target's cached /api/tags catalog contains the model
    /// (case-insensitive name match). Null catalog cache ⇒ false.
    /// </summary>
    private static async Task<bool> AnyTargetHasModelAsync(
        IModelCatalogCacheService? catalogCache,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        string modelName,
        CancellationToken cancellationToken)
    {
        if (catalogCache is null)
        {
            return false;
        }

        foreach (var target in effectiveTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Url))
            {
                continue;
            }

            try
            {
                if (await catalogCache.ModelExistsAsync(target.Url, modelName, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch
            {
                // A failing catalog check must never break the capability report.
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the per-model summary: discovers the metadata on the FIRST effective target
    /// (declaration order) that hosts the model — one /api/show + one /api/tags per model —
    /// reuses that single metadata for every target's pure VRAM-math ceiling derivation, and
    /// computes recommendedContextWindow as the max CMax over the HARDWARE-CAPABLE fleet
    /// targets (cloud overflow is never a ceiling contributor; it is surfaced only via
    /// CloudOverflowAvailable).
    /// </summary>
    private static async Task<RouterModelSummary> BuildModelSummaryAsync(
        string name,
        OllamaRouterOptions opt,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetAvailabilityService? availability,
        CancellationToken cancellationToken)
    {
        var cloudOverflowAvailable = availability is not null &&
            CloudRelayResolver.IsCloudOverflowAvailable(availability, opt, name);

        // No capability stack (minimal host) or the model is on no target: degrade.
        var presentOnFleet = await AnyTargetHasModelAsync(catalogCache, effectiveTargets, name, cancellationToken)
            .ConfigureAwait(false);
        if (catalogCache is null || capabilityEngine is null || !presentOnFleet)
        {
            return BuildDegradedSummary(name, cloudOverflowAvailable,
                cloudOnly: catalogCache is not null && !presentOnFleet);
        }

        var normalizedName = RoutingDecisionService.NormalizeModelName(name);
        var pinnedArchitecture = opt.Models.TryGetValue(normalizedName, out var thresholds) ? thresholds.Architecture : null;

        // Source target: the FIRST effective target (declaration order) whose cached catalog
        // contains the model.
        OllamaTargetOptions? source = null;
        foreach (var target in effectiveTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Url))
            {
                continue;
            }

            try
            {
                if (await catalogCache.ModelExistsAsync(target.Url, name, cancellationToken).ConfigureAwait(false))
                {
                    source = target;
                    break;
                }
            }
            catch
            {
                // Skip the flaky target; the next candidate gets the discovery chance.
            }
        }

        if (source is null)
        {
            return BuildDegradedSummary(name, cloudOverflowAvailable, cloudOnly: false);
        }

        ModelMetadata? metadata = null;
        try
        {
            var digest = await catalogCache.GetCurrentDigestAsync(source.Url, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(digest))
            {
                metadata = await capabilityEngine.GetMetadataAsync(source.Url, name, digest, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A failing discovery must never 500 the endpoint.
            metadata = null;
        }

        if (metadata is null)
        {
            // Per-model discovery failure degrades to the conservative defaults.
            return BuildDegradedSummary(name, cloudOverflowAvailable, cloudOnly: false);
        }

        var (architecture, _) = capabilityEngine.ClassifyArchitecture(metadata, pinnedArchitecture);

        // The same model file yields the same weights/num_ctx across nodes: reuse the single
        // metadata for every target's pure VRAM-math ceiling derivation. recommendedContextWindow
        // (task #26): dense models report the max pre-configured CMax (the optimal bucket is
        // the whole capacity — dense is never expanded); MoE models report the max of the
        // MoE-aware extensible capacity min(native ?? 128k, MaxContextTokens ?? ∞), which is
        // the extensible ceiling beyond the node's pre-configured context.
        var recommended = ModelCapabilityEngine.OllamaDefaultContext;
        foreach (var target in effectiveTargets)
        {
            var capability = capabilityEngine.DeriveCapability(metadata, target.Name!, target, pinnedArchitecture);
            if (capability.HardwareCapable)
            {
                var capacity = capability.Architecture == ModelArchitectureKind.Moe
                    ? ModelCapabilityEngine.GetMoeExtensibleCapacity(capability, thresholds?.MaxContextTokens)
                    : capability.CMax;
                recommended = Math.Max(recommended, capacity);
            }
        }

        return new RouterModelSummary(
            Name: name,
            Architecture: ModelCapabilityEngine.ArchitectureWireName(architecture),
            ParameterCount: metadata.ParameterCount,
            NativeContextWindow: metadata.NativeContextLength is { } nativeLen ? (int)Math.Min(nativeLen, int.MaxValue) : ModelCapabilityEngine.OllamaDefaultContext,
            RecommendedContextWindow: recommended,
            CloudOverflowAvailable: cloudOverflowAvailable);
    }

    private static RouterModelSummary BuildDegradedSummary(string name, bool cloudOverflowAvailable, bool cloudOnly)
    {
        // Cloud-only synthesis entries have no discoverable metadata at all (native +
        // recommended = 0); every other degraded case falls back to the Ollama default.
        var native = cloudOnly ? 0 : ModelCapabilityEngine.OllamaDefaultContext;
        return new RouterModelSummary(
            Name: name,
            Architecture: "Unknown",
            ParameterCount: null,
            NativeContextWindow: native,
            RecommendedContextWindow: native,
            CloudOverflowAvailable: cloudOverflowAvailable);
    }

    /// <summary>
    /// Per-target qualification matrix (one row per effective target, declaration order; the
    /// Cloud pseudo-target is NOT listed — it has no OllamaTargetOptions). Capable =
    /// hardware-capable capability AND model present on that target; the veto reasons are:
    /// model absent → "model not present in target catalog"; LargeDense on a small-class
    /// target → the exact <see cref="ModelCapabilityEngine.LargeDenseVetoReason"/> string;
    /// undiscoverable capability → no veto reason (permissive fallback, matching the routing
    /// pipeline).
    /// </summary>
    private static async Task<IReadOnlyList<RouterModelTargetCompatibility>> BuildTargetCompatibilityMatrixAsync(
        string name,
        OllamaRouterOptions opt,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetExecutionTracker? executionTracker,
        CancellationToken cancellationToken)
    {
        var normalizedName = RoutingDecisionService.NormalizeModelName(name);
        var pinnedArchitecture = opt.Models.TryGetValue(normalizedName, out var thresholds) ? thresholds.Architecture : null;

        // Discover the metadata once (same single-source-target rule as the summary).
        ModelMetadata? metadata = null;
        if (catalogCache is not null && capabilityEngine is not null)
        {
            foreach (var target in effectiveTargets)
            {
                if (string.IsNullOrWhiteSpace(target.Url))
                {
                    continue;
                }

                bool present;
                try
                {
                    present = await catalogCache.ModelExistsAsync(target.Url, name, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    present = false;
                }

                if (!present)
                {
                    continue;
                }

                try
                {
                    var digest = await catalogCache.GetCurrentDigestAsync(target.Url, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(digest))
                    {
                        metadata = await capabilityEngine.GetMetadataAsync(target.Url, name, digest, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    metadata = null;
                }

                break;
            }
        }

        return await Task.WhenAll(effectiveTargets.Select(target =>
            BuildTargetRowAsync(name, target, metadata, pinnedArchitecture, catalogCache, capabilityEngine, executionTracker, cancellationToken)))
            .ConfigureAwait(false);
    }

    private static async Task<RouterModelTargetCompatibility> BuildTargetRowAsync(
        string name,
        OllamaTargetOptions target,
        ModelMetadata? metadata,
        string? pinnedArchitecture,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetExecutionTracker? executionTracker,
        CancellationToken cancellationToken)
    {
        var targetName = target.Name!;
        var routingTarget = (RoutingTarget)targetName;

        bool present = false;
        if (catalogCache is not null && !string.IsNullOrWhiteSpace(target.Url))
        {
            try
            {
                present = await catalogCache.ModelExistsAsync(target.Url, name, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                present = false;
            }
        }

        TargetModelCapability? capability = metadata is not null && capabilityEngine is not null
            ? capabilityEngine.DeriveCapability(metadata, targetName, target, pinnedArchitecture)
            : null;

        var capable = capability is not null && capability.HardwareCapable && present;

        string? vetoReason = null;
        if (!capable)
        {
            if (!present)
            {
                vetoReason = "model not present in target catalog";
            }
            else if (capability is { Architecture: ModelArchitectureKind.LargeDense, HardwareCapable: false })
            {
                vetoReason = ModelCapabilityEngine.LargeDenseVetoReason;
            }
            // Undiscoverable capability: permissive — no veto reason, capable follows the
            // model-presence flag (which is already false here).
        }

        var executionState = executionTracker?.GetState(routingTarget);
        var isResident = executionState?.LoadedModels.Any(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Name, RoutingDecisionService.NormalizeModelName(name), StringComparison.OrdinalIgnoreCase))
            ?? false;
        var inFlight = executionState?.InFlightRequests ?? 0;

        return new RouterModelTargetCompatibility(
            Target: targetName,
            Capable: capable,
            ContextCeiling: capability?.CMax ?? ModelCapabilityEngine.OllamaDefaultContext,
            IsResident: isResident,
            InFlightRequests: inFlight,
            VetoReason: vetoReason);
    }

    // ------------------------------------------------------------------
    //  POST /api/router/context/evaluate
    // ------------------------------------------------------------------

    /// <summary>
    /// Stateless routing + sizing preview: runs the EXACT production routing decision
    /// (including the failure-streak switching) through <see cref="RoutingDecisionService"/>,
    /// then previews the <see cref="DynamicContextSizingEngine"/> bucket that the selected
    /// target would receive for the prompt. No payload rewriting, no lease mutations, no
    /// LLM inference (only the catalog /api/tags + /api/show discovery the pipeline itself
    /// performs). Returns null (→ 503) when the decision pipeline throws
    /// <see cref="NoAvailableTargetException"/> or no decision service is registered.
    /// </summary>
    private static async Task<RouterContextEvaluateResponse?> EvaluateContextPreviewAsync(
        IOptions<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        RouterContextEvaluateRequest request,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
        var decisionService = serviceProvider.GetService<IRoutingDecisionService>() as RoutingDecisionService;
        if (decisionService is null)
        {
            // Minimal host without the decision pipeline: no target can be predicted.
            return null;
        }

        RoutingTarget selected;
        var evaluations = new List<RoutingCandidateEvaluation>();
        try
        {
            selected = await decisionService.DecideWithEvaluationsAsync(
                request.EstimatedTokens,
                request.Model,
                request.ClientRequestedNumCtx,
                reportEvaluation: evaluations.Add,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NoAvailableTargetException)
        {
            return null;
        }

        var affinityScore = evaluations.FirstOrDefault(e => e.Target == selected)?.Score ?? 0;
        var estimatedTokensWithMargin = request.EstimatedTokens + Math.Max(0, opt.ContextFitMarginTokens);

        // Cloud pseudo-target: the preview cannot know a cloud daemon's ceiling; it reports
        // what the client's/static value would flow through (contextMode "expanded").
        if (selected == RoutingTarget.Cloud)
        {
            return new RouterContextEvaluateResponse(
                SelectedTarget: RoutingTarget.Cloud.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "expanded",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        var targetOptions = opt.GetEffectiveTargets()
            .FirstOrDefault(t => string.Equals(t.Name, selected.Value, StringComparison.OrdinalIgnoreCase));
        if (targetOptions is null || string.IsNullOrWhiteSpace(targetOptions.Url))
        {
            // Selected a target with no configured URL: pass-through preview.
            return new RouterContextEvaluateResponse(
                SelectedTarget: selected.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "baseline",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        // Dynamic context disabled: the middleware never rewrites the payload (pass-through).
        if (!opt.DynamicContext.Enabled)
        {
            return new RouterContextEvaluateResponse(
                SelectedTarget: selected.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "baseline",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        var capabilityEngine = serviceProvider.GetService<ModelCapabilityEngine>();
        var catalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        if (capabilityEngine is null || catalogCache is null)
        {
            // No capability stack: pass-through preview.
            return new RouterContextEvaluateResponse(
                SelectedTarget: selected.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "baseline",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        ModelMetadata? metadata = null;
        try
        {
            var digest = await catalogCache.GetCurrentDigestAsync(targetOptions.Url, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(digest))
            {
                metadata = await capabilityEngine.GetMetadataAsync(targetOptions.Url, request.Model, digest, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            metadata = null;
        }

        var normalizedName = RoutingDecisionService.NormalizeModelName(request.Model);
        var pinnedArchitecture = opt.Models.TryGetValue(normalizedName, out var thresholds) ? thresholds.Architecture : null;

        var capability = metadata is not null
            ? capabilityEngine.DeriveCapability(metadata, selected.Value, targetOptions, pinnedArchitecture)
            : null;

        if (capability is null)
        {
            // Undiscoverable capability for the selected fleet target: pass-through preview
            // (the middleware behaves exactly this way — it never rewrites the payload).
            return new RouterContextEvaluateResponse(
                SelectedTarget: selected.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "baseline",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        var sizing = DynamicContextSizingEngine.Decide(
            opt.DynamicContext,
            capability,
            request.EstimatedTokens,
            request.ClientRequestedNumCtx,
            Math.Max(0, opt.ContextFitMarginTokens),
            thresholds?.MaxContextTokens);

        // contextMode vocabulary (task #26, lowercase engine modes): passthrough (dense,
        // untouched — injectedNumCtx reports what would flow through, matching the existing
        // degraded-preview convention), clamped (dense, down-clamped), baseline/expanded
        // (MoE two-bucket ladder).
        var (injectedNumCtx, contextMode) = sizing.Mode switch
        {
            ContextSizingMode.PassThrough => (request.ClientRequestedNumCtx ?? estimatedTokensWithMargin, "passthrough"),
            _ => (sizing.InjectedNumCtx, sizing.Mode.ToString().ToLowerInvariant())
        };

        return new RouterContextEvaluateResponse(
            SelectedTarget: selected.Value,
            InjectedNumCtx: injectedNumCtx,
            ContextMode: contextMode,
            EstimatedTokensWithMargin: estimatedTokensWithMargin,
            TargetMaxContext: sizing.EffectiveCeiling,
            AffinityScore: affinityScore);
    }
}

/// <summary>
/// Response payload for <c>GET /api/router/info</c>: the router version, the static
/// <c>isRouter</c> marker, the fleet summary and the active feature flags.
/// </summary>
public sealed record RouterInfoResponse(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("isRouter")] bool IsRouter,
    [property: JsonPropertyName("fleet")] RouterFleetSummary Fleet,
    [property: JsonPropertyName("features")] RouterFeatures Features);

public sealed record RouterFleetSummary(
    [property: JsonPropertyName("totalTargets")] int TotalTargets,
    [property: JsonPropertyName("onlineTargets")] int OnlineTargets,
    [property: JsonPropertyName("cloudEnabled")] bool CloudEnabled);

public sealed record RouterFeatures(
    [property: JsonPropertyName("dynamicContext")] bool DynamicContext,
    [property: JsonPropertyName("autoShrink")] bool AutoShrink,
    [property: JsonPropertyName("failover")] bool Failover,
    [property: JsonPropertyName("antiThrashingAffinity")] bool AntiThrashingAffinity);

public sealed record RouterFleetResponse(
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterTargetItem> Targets);

public sealed record RouterTargetItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("liveness")] string Liveness,
    [property: JsonPropertyName("latencyMs")] long? LatencyMs,
    [property: JsonPropertyName("hardware")] RouterTargetHardware Hardware,
    [property: JsonPropertyName("execution")] RouterTargetExecution Execution);

public sealed record RouterTargetHardware(
    [property: JsonPropertyName("hardwareHint")] string? HardwareHint,
    [property: JsonPropertyName("gpuVendor")] string GpuVendor,
    [property: JsonPropertyName("devices")] IReadOnlyList<string> Devices,
    [property: JsonPropertyName("maxVramMB")] int? MaxVramMB,
    [property: JsonPropertyName("maxParallel")] int MaxParallel,
    [property: JsonPropertyName("speedFactor")] double? SpeedFactor,
    [property: JsonPropertyName("isUnifiedMemory")] bool IsUnifiedMemory = false,
    [property: JsonPropertyName("systemRamMB")] long? SystemRamMB = null,
    [property: JsonPropertyName("memoryType")] string? MemoryType = null,
    [property: JsonPropertyName("bandwidthGBs")] double? BandwidthGBs = null);

public sealed record RouterTargetExecution(
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("activeModel")] string? ActiveModel,
    [property: JsonPropertyName("contextAllocation")] string ContextAllocation);

/// <summary>
/// Per-model capability summary of <c>GET /api/router/models</c>: the discovered
/// architecture wire name ("Dense"/"Moe"/"Unknown"), the parameter count (null when unknown),
/// the native Modelfile num_ctx (Ollama default when the Modelfile omits it), the
/// router-calculated context recommendation and the cloud overflow hint.
/// <see cref="RecommendedContextWindow"/> is the node's pre-configured capacity (the optimal
/// bucket) for DENSE models — dense contexts are never expanded (clamp-only pass-through);
/// for MOE models it is the max extensible capacity min(native ?? 128k, MaxContextTokens ?? ∞)
/// (the single extensible bucket of the two-bucket ladder).
/// </summary>
public sealed record RouterModelSummary(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("parameterCount")] long? ParameterCount,
    [property: JsonPropertyName("nativeContextWindow")] int NativeContextWindow,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("cloudOverflowAvailable")] bool CloudOverflowAvailable);

public sealed record RouterModelsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<RouterModelSummary> Models);

/// <summary>
/// Detailed capability report of <c>GET /api/router/models/{modelName}</c>: the summary
/// fields plus the per-target qualification matrix (the Cloud pseudo-target is never listed).
/// </summary>
public sealed record RouterModelDetailResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("parameterCount")] long? ParameterCount,
    [property: JsonPropertyName("nativeContextWindow")] int NativeContextWindow,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterModelTargetCompatibility> Targets);

/// <summary>
/// One row of the per-target qualification matrix: whether the target is capable of serving
/// the model (hardware + catalog presence), its context ceiling, the resident (hot) state
/// and the in-flight request count. <see cref="VetoReason"/> is emitted only when
/// <c>capable == false</c> (null is omitted from the JSON).
/// </summary>
public sealed record RouterModelTargetCompatibility(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("capable")] bool Capable,
    [property: JsonPropertyName("contextCeiling")] int ContextCeiling,
    [property: JsonPropertyName("isResident")] bool IsResident,
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("vetoReason")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VetoReason = null);

/// <summary>
/// Request body of <c>POST /api/router/context/evaluate</c>: the model to evaluate, the
/// estimated prompt token count and the client's static <c>options.num_ctx</c> (if any).
/// </summary>
public sealed record RouterContextEvaluateRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("estimatedTokens")] int EstimatedTokens,
    [property: JsonPropertyName("clientRequestedNumCtx")] int? ClientRequestedNumCtx = null);

/// <summary>
/// Preview response of <c>POST /api/router/context/evaluate</c>: the predicted target, the
/// num_ctx the sizing engine would inject, the context mode — one of the four lowercase engine
/// modes (task #26): <c>passthrough</c> (dense, payload untouched; <c>injectedNumCtx</c>
/// reports what would flow through), <c>clamped</c> (dense, oversized client value clamped
/// down to the pre-configured context), <c>baseline</c> / <c>expanded</c> (MoE two-bucket
/// ladder) — the margin-adjusted token estimate, the target's effective capacity (MoE
/// extensible limit / dense pre-configured CMax) and the winner's affinity score.
/// </summary>
public sealed record RouterContextEvaluateResponse(
    [property: JsonPropertyName("selectedTarget")] string SelectedTarget,
    [property: JsonPropertyName("injectedNumCtx")] int InjectedNumCtx,
    [property: JsonPropertyName("contextMode")] string ContextMode,
    [property: JsonPropertyName("estimatedTokensWithMargin")] int EstimatedTokensWithMargin,
    [property: JsonPropertyName("targetMaxContext")] int TargetMaxContext,
    [property: JsonPropertyName("affinityScore")] double AffinityScore);

/// <summary>
/// Optional request body of <c>POST /api/router/vram/reclaim</c> / <c>POST /api/router/fleet/unload</c>:
/// the target name to unload (null/absent = all effective targets) and whether to
/// administratively disable the processed target(s) (default false). The same parameters are
/// also accepted as query string values; body values win when both are present.
/// </summary>
public sealed record RouterVramReclaimRequest(
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("disableTarget")] bool DisableTarget = false);

/// <summary>
/// One row of the <c>targets</c> array of the fleet VRAM reclaim response: the target's
/// canonical configured name, the models unloaded on it (empty when the daemon was
/// unreachable or had no running models) and whether it was administratively disabled.
/// </summary>
public sealed record RouterVramReclaimTargetResult(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("unloadedModels")] IReadOnlyList<string> UnloadedModels,
    [property: JsonPropertyName("disabled")] bool Disabled);

/// <summary>
/// Response payload of <c>POST /api/router/vram/reclaim</c> / <c>POST /api/router/fleet/unload</c>:
/// whether the operation executed against a valid target set (individual unreachable daemons
/// are fail-soft), the total number of unloaded models across the fleet, and the per-target
/// results in configured fleet order. <see cref="Message"/> is omitted (null) on success and
/// set to "no targets configured" when the effective target list is empty.
/// </summary>
public sealed record RouterVramReclaimResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("totalUnloaded")] int TotalUnloaded,
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterVramReclaimTargetResult> Targets,
    [property: JsonPropertyName("message")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);
