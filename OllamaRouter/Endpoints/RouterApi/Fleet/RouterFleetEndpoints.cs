using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using System.Text.Json;
using static OllamaRouter.Endpoints.RouterApi.Shared.RouterApiSharedHelpers;

namespace OllamaRouter.Endpoints.RouterApi.Fleet;

/// <summary>
/// The fleet topology + hardware profiling endpoints of the OllamaRouter programmatic
/// management API:
/// <list type="bullet">
/// <item><c>GET /api/router/fleet</c> — the full fleet hardware topology: every configured
/// target (in configuration order) plus the Cloud pseudo-target (last), with real-time
/// liveness state, last-probe latency, detected GPU hardware (local workstation only),
/// in-flight request count, currently loaded model and the active KV-cache context lease.</item>
/// </list>
/// </summary>

public static class RouterFleetEndpoints
{
    /// <summary>
    /// Maps the fleet topology endpoints (<c>/api/router/fleet</c>, <c>/api/router/hardware</c>, <c>/api/router/fleet/hardware</c>).
    /// </summary>
    public static WebApplication MapRouterFleetEndpoints(this WebApplication app)
    {
        app.MapGet("/api/router/fleet", async (
            IOptionsMonitor<OllamaRouterOptions> options,
            ITargetAvailabilityService targetAvailability,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            var response = await BuildFleetResponseAsync(options, targetAvailability, serviceProvider, cancellationToken);
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterFleetResponse);
        })
        .WithName("GetRouterFleet")
        .WithTags("Identity & Fleet")
        .WithSummary("Full fleet hardware topology with real-time liveness and execution state.")
        .WithDescription("Every configured target (configuration order) plus the Cloud pseudo-target (last), each with real-time "
            + "liveness state (Online/Offline/Probing), last-probe latency, detected GPU hardware (local workstation only), "
            + "in-flight request count, currently loaded model and the active KV-cache context lease. The Cloud pseudo-target "
            + "is never probed or tracked: 'Online' iff cloud overflow is enabled and it never carries a loaded model, "
            + "in-flight requests, context lease or latency measurement.");
        // Fleet hardware profiling engine: the local node's own profile (cached after the
        // first probe), the full fleet topology (parallel peer probes, fail-soft per node),
        // and the manual profile push (for bare Ollama targets without a router daemon).
        app.MapGet("/api/router/hardware", async (IHardwareProfileProvider provider, CancellationToken cancellationToken) =>
        {
            var profile = await provider.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(profile, OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile);
        })
        .WithName("GetRouterHardware")
        .WithTags("Hardware Profiling")
        .WithSummary("The local node's own hardware profile.")
        .WithDescription("Fleet hardware profiling engine: the local node's own profile (CPU/system-memory/GPU probe, "
            + "cached after the first probe). Read-only — no key required in default mode.");
        app.MapGet("/api/router/fleet/hardware", async (IFleetHardwareService fleetHardware, CancellationToken cancellationToken) =>
        {
            var nodes = await fleetHardware.GetFleetHardwareAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new RouterFleetHardwareResponse(nodes), OllamaRouterJsonSerializerContext.Default.RouterFleetHardwareResponse);
        })
        .WithName("GetRouterFleetHardware")
        .WithTags("Hardware Profiling")
        .WithSummary("Fleet hardware topology of every fleet node.")
        .WithDescription("The full fleet topology: parallel peer probes of the remote router daemons (their own local profile "
            + "endpoints), fail-soft per node (unreachable nodes degrade to their last stored profile or are absent). "
            + "The Cloud pseudo-target is never a fleet hardware node (it has no Ollama daemon to probe). Read-only — no key "
            + "required in default mode.");
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
        })
        .WithName("PutRouterFleetHardwareProfile")
        .WithTags("Hardware Profiling")
        .WithSummary("Manually store a hardware profile for a target (protected: API key required).")
        .WithDescription("Manual profile push for bare Ollama targets without a router daemon. Request body: a NodeHardwareProfile "
            + "JSON with a non-empty 'nodeName' (missing → 400 OllamaErrorResponse); unknown targetName → 404. On success the "
            + "stored profile is echoed (200). Protected by the API-key middleware (default protected set).");
        return app;
    }

    // ------------------------------------------------------------------
    //  GET /api/router/fleet
    // ------------------------------------------------------------------

    private static async Task<RouterFleetResponse> BuildFleetResponseAsync(
        IOptionsMonitor<OllamaRouterOptions> options,
        ITargetAvailabilityService targetAvailability,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var opt = options.CurrentValue;
        var effectiveTargets = opt.GetEffectiveTargets();
        var availability = targetAvailability.GetSnapshot();
        var liveness = serviceProvider.GetService<ITargetLivenessService>();
        var livenessEntries = liveness?.GetEntries();
        var executionTracker = serviceProvider.GetService<ITargetExecutionTracker>();
        var leaseSnapshot = serviceProvider.GetService<IContextAutoShrinkManager>()?.GetLeaseSnapshot();
        var modelCatalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        var fleetHardware = serviceProvider.GetService<IFleetHardwareService>();

        var targets = new List<RouterTargetItem>(effectiveTargets.Count + 1);
        foreach (var target in effectiveTargets)
        {
            var name = target.Name!;
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

            // Hardware details served directly from the cached fleet hardware store
            // (all targets, including local, without initiating redundant local probes).
            NodeHardwareProfile? nodeProfile = null;
            try
            {
                nodeProfile = fleetHardware?.GetProfile(name);
            }
            catch
            {
                nodeProfile = null;
            }

            string gpuVendor = "Unknown";
            string? gpuName = null;
            IReadOnlyList<string> devices = Array.Empty<string>();

            if (nodeProfile is not null)
            {
                if (nodeProfile.Gpus.Count > 0)
                {
                    gpuVendor = nodeProfile.Gpus[0].Vendor;
                    gpuName = nodeProfile.Gpus[0].ModelName;
                    devices = nodeProfile.Gpus.Select(FormatGpuDevice).ToArray();
                }
                else if (nodeProfile.IsUnifiedMemory)
                {
                    gpuVendor = string.Equals(nodeProfile.Architecture, "arm64", StringComparison.OrdinalIgnoreCase)
                        ? "Apple"
                        : "Unified";
                    gpuName = $"{nodeProfile.Architecture} Unified Memory";
                    devices = [nodeProfile.SystemRamMB > 0 ? $"{gpuName} ({nodeProfile.SystemRamMB} MB)" : gpuName];
                }
            }

            targets.Add(new RouterTargetItem(
                Name: name,
                Url: target.Url,
                Enabled: enabled,
                Liveness: livenessText,
                LatencyMs: latencyMs,
                Location: TargetLocationClassifier.ToWireValue(TargetLocationClassifier.Classify(target)),
                Hardware: new RouterTargetHardware(
                    GpuVendor: gpuVendor,
                    Devices: devices,
                    MaxVramMB: target.GetEffectiveMaxVramMB(fleetHardware),
                    MaxParallel: target.MaxParallel,
                    SpeedFactor: speedFactor == 1.0 ? null : speedFactor,
                    IsUnifiedMemory: nodeProfile?.IsUnifiedMemory ?? false,
                    SystemRamMB: nodeProfile?.SystemRamMB is { } ram and > 0 ? ram : null,
                    MemoryType: nodeProfile is null ? null : DescribeMemoryTier(nodeProfile),
                    BandwidthGBs: nodeProfile?.EffectiveBandwidthGBs is { } bw and > 0 ? bw : null,
                    GpuName: gpuName),
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
            Location: TargetLocationClassifier.ToWireValue(TargetLocation.Cloud),
            Hardware: new RouterTargetHardware(
                GpuVendor: "Cloud",
                Devices: new[] { "Cloud Overflow" },
                MaxVramMB: null,
                MaxParallel: 10,
                SpeedFactor: null,
                GpuName: "Cloud Overflow"),
            Execution: new RouterTargetExecution(
                InFlightRequests: 0,
                ActiveModel: null,
                ContextAllocation: "baseline")));

        return new RouterFleetResponse(targets);
    }

    private static string FormatGpuDevice(GpuDeviceProfile gpu)
    {
        var displayName = gpu.ModelName.StartsWith(gpu.Vendor, StringComparison.OrdinalIgnoreCase)
            ? gpu.ModelName
            : $"{gpu.Vendor} {gpu.ModelName}";
        return gpu.VramMB > 0 ? $"{displayName} ({gpu.VramMB} MB)" : displayName;
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
}
