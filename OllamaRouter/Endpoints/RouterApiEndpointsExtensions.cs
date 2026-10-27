using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;

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
/// </list>
/// The handlers build exclusively from in-memory snapshots (<c>GetEntries</c>/
/// <c>GetState</c>/<c>GetLeaseSnapshot</c>/availability flags/configuration) — no statistics,
/// no history/7-day aggregation, and no blocking per-target HTTP (only the single awaited
/// local GPU CLI snapshot when a local workstation target is present). All enrichment
/// services are resolved defensively (<see cref="ServiceProvider.GetService(Type)"/>) so a
/// minimal host with only options + availability still serves both endpoints.
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

        return app;
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
                    MaxParallel: target.MaxParallel),
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
                MaxParallel: 10),
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
    [property: JsonPropertyName("maxParallel")] int MaxParallel);

public sealed record RouterTargetExecution(
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("activeModel")] string? ActiveModel,
    [property: JsonPropertyName("contextAllocation")] string ContextAllocation);
