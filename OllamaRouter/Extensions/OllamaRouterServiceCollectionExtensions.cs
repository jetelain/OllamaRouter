using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using Yarp.ReverseProxy;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Extensions;

/// <summary>
/// Hosts the full OllamaRouter service registration and request pipeline behind two small
/// extension methods so the production <c>Program.cs</c> and the in-process integration test
/// harness share the exact same wiring (behavior-preserving extraction, Phase 5).
/// <para>
/// <see cref="AddOllamaRouter(IServiceCollection, OllamaRouterOptions, string?, bool, TimeSpan?, TimeSpan?)"/>
/// registers YARP (from <see cref="OllamaReverseProxyConfig.Build"/>), the HTTP forwarder, the
/// routing decision pipeline, the liveness prober, the execution tracker, the model-capability
/// engine and every other singleton that <c>Program.cs</c> used to register inline. The
/// <paramref name="stateDirectory"/> parameter relocates the on-disk state files
/// (<c>targets.json</c>, <c>activity-statistics.json</c>) so test hosts never touch real user
/// state; <paramref name="reprobeInterval"/> and <paramref name="onlineCacheTtl"/> are optional
/// overrides for the liveness prober (tests inject a short re-probe interval for fast rejoin).
/// </para>
/// </summary>
public static class OllamaRouterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the complete OllamaRouter service graph for <paramref name="options"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="options">The resolved router options instance (bound from configuration in production,
    /// constructed directly by tests). Registered as <see cref="IOptions{TOptions}"/>.</param>
    /// <param name="stateDirectory">
    /// Directory for the persisted state files. Defaults to
    /// <c>%LOCALAPPDATA%\OllamaRouter</c> (production); tests pass a temp directory.
    /// </param>
    /// <param name="enableTui">
    /// When false, disables the in-terminal activity monitor (mirrors the <c>--no-tui</c> flag).
    /// </param>
    /// <param name="reprobeInterval">Optional liveness re-probe interval override (default 10 s).</param>
    /// <param name="onlineCacheTtl">Optional Online-verdict TTL override (default 3 s).</param>
    public static IServiceCollection AddOllamaRouter(
        this IServiceCollection services,
        OllamaRouterOptions options,
        string? stateDirectory = null,
        bool enableTui = true,
        TimeSpan? reprobeInterval = null,
        TimeSpan? onlineCacheTtl = null)
    {
        var effectiveStateDirectory = string.IsNullOrWhiteSpace(stateDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OllamaRouter")
            : stateDirectory;

        services.ConfigureHttpJsonOptions(httpJsonOptions =>
        {
            httpJsonOptions.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        // Register the resolved options instance (not config-bound) so both production (where it was
        // already bound + normalized in Program.cs) and tests (constructed directly) share one value.
        services.AddSingleton(options);
        services.AddSingleton<IOptions<OllamaRouterOptions>>(MsOptions.Create(options));

        // Code-based YARP configuration: routes/clusters generated from the effective fleet target list.
        var (routes, clusters) = OllamaReverseProxyConfig.Build(options);
        services.AddReverseProxy().LoadFromMemory(routes, clusters);

        // Middleware-owned dispatch loop: a single HttpForwarder instance (YARP 2.3.0 has no DI-friendly
        // constructor); the dedicated proxy HttpClient + 15 min per-request ForwarderRequestConfig live on
        // the middleware (see OllamaRoutingMiddleware.ProxyClient).
        services.AddHttpForwarder();

        // Per-target failure streak tracker: singleton, thread-safe in-memory counter shared by the
        // routing decision service and the middleware's dispatch loop.
        services.AddSingleton<ITargetFailureStreakService, TargetFailureStreakService>();

        // Effective fleet targets plus the Cloud pseudo-target; used by the availability and statistics
        // services so state is keyed per target. Invalid Targets entries are skipped with a warning here.
        var startupLogger = LoggerFactory.Create(_ => { }).CreateLogger("OllamaRouter.Startup");
        var fleetTargets = BuildFleetTargets(options, startupLogger);

        services.AddHttpClient<IOllamaModelCatalogClient, OllamaModelCatalogClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(4);
        });

        // Dedicated liveness probe client: short 1.5 s timeout and a 10 s pooled-connection lifetime so a
        // sleeping-then-woken machine with a changed IP does not keep hitting dead pooled sockets.
        services.AddHttpClient("LivenessProbe", client =>
        {
            client.Timeout = TimeSpan.FromMilliseconds(1500);
        })
        .ConfigurePrimaryHttpMessageHandler(_ =>
        {
            return new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(10) };
        });
        services.AddMemoryCache();

        if (string.Equals(options.TokenEstimator, "Tiktoken", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITokenEstimator, TiktokenTokenEstimator>();
        }
        else
        {
            services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        }
        services.AddGpuVramProviders();

        // Explicit factory so the Cloud handler resolves IOptions (the Enabled switch / DefaultModel
        // precedence would otherwise be silently ignored at runtime).
        services.AddTransient<CloudRoutingTargetHandler>(sp => new CloudRoutingTargetHandler(
            sp.GetRequiredService<ITargetAvailabilityService>(),
            sp.GetRequiredService<IOptions<OllamaRouterOptions>>(),
            sp.GetRequiredService<ILogger<CloudRoutingTargetHandler>>()));
        services.AddTransient<IRoutingTargetHandler>(sp => sp.GetRequiredService<CloudRoutingTargetHandler>());
        services.AddTransient<IRoutingDecisionService>(sp => new RoutingDecisionService(
            sp.GetRequiredService<IOptions<OllamaRouterOptions>>(),
            sp.GetRequiredService<IModelCatalogCacheService>(),
            sp.GetRequiredService<ITargetLivenessService>(),
            sp.GetRequiredService<ITargetAvailabilityService>(),
            sp.GetRequiredService<ITargetExecutionTracker>(),
            sp.GetRequiredService<ModelCapabilityEngine>(),
            sp.GetRequiredService<CloudRoutingTargetHandler>(),
            sp.GetRequiredService<ILogger<RoutingDecisionService>>(),
            sp.GetRequiredService<ITargetFailureStreakService>()));
        services.AddSingleton<IModelCatalogCacheService, ModelCatalogCacheService>();
        services.AddSingleton<IContextAutoShrinkManager>(sp => new ContextAutoShrinkManager(
            sp.GetRequiredService<ILogger<ContextAutoShrinkManager>>()));
        services.AddSingleton<IFleetAggregationService, FleetAggregationService>();
        services.AddSingleton<ModelCapabilityEngine>();
        services.AddSingleton<IOllamaProcessLauncher, OllamaProcessLauncher>();
        services.AddSingleton<IActivityMonitorService, ActivityMonitorService>();
        services.AddSingleton<IInferenceRequestRecorder, InferenceRequestRecorder>();
        services.AddSingleton<IActivityStatisticsService>(sp =>
            new ActivityStatisticsService(
                Path.Combine(effectiveStateDirectory, "activity-statistics.json"),
                fleetTargets,
                sp.GetRequiredService<IHostApplicationLifetime>(),
                sp.GetRequiredService<ILogger<ActivityStatisticsService>>()));
        services.AddSingleton<ITargetAvailabilityService>(sp =>
            new TargetAvailabilityService(
                Path.Combine(effectiveStateDirectory, "targets.json"),
                fleetTargets,
                sp.GetRequiredService<ILogger<TargetAvailabilityService>>()));

        // In-terminal activity monitor (registered always; disabled when !enableTui via the option).
        if (!enableTui)
        {
            services.PostConfigure<OllamaRouterOptions>(opt => opt.TerminalMonitoring.Enabled = false);
        }
        services.AddHostedService<TerminalMonitoringHostedService>();

        // Target liveness prober (singleton + hosted service): boot probe + background re-prober.
        // The IHostedService registration uses a DIRECT factory (not AddHostedService(sp => ...))
        // because the factory-form overload trips a .NET 10 TryAddEnumerable type-validation
        // ("indistinguishable from other services registered for IHostedService"); the direct
        // registration is behavior-identical (IHostedService is an IEnumerable aggregate resolved
        // by the host the same way) and works in both the production host and the test host.
        services.AddSingleton<ITargetLivenessService>(sp => new TargetLivenessService(
            sp.GetRequiredService<IOptions<OllamaRouterOptions>>(),
            sp.GetRequiredService<IModelCatalogCacheService>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("LivenessProbe"),
            sp.GetRequiredService<ILogger<TargetLivenessService>>(),
            reprobeInterval,
            onlineCacheTtl));
        services.AddSingleton<IHostedService>(sp => (IHostedService)sp.GetRequiredService<ITargetLivenessService>());

        // Multi-target execution state tracker (singleton + hosted refresh timer). The SP factory injects
        // the shared Auto-Shrink Manager so evicted models reset their leases.
        services.AddSingleton<ITargetExecutionTracker>(sp => new TargetExecutionTracker(
            sp.GetRequiredService<IOptions<OllamaRouterOptions>>(),
            sp.GetRequiredService<ITargetLivenessService>(),
            sp.GetRequiredService<ITargetAvailabilityService>(),
            sp.GetRequiredService<IOllamaModelCatalogClient>(),
            sp.GetRequiredService<ILogger<TargetExecutionTracker>>(),
            contextShrinkManager: sp.GetRequiredService<IContextAutoShrinkManager>()));
        services.AddHostedService<TargetExecutionTrackerRefreshService>();

        return services;
    }

    /// <summary>
    /// Applies the OllamaRouter request pipeline in the exact production order: the routing middleware
    /// (before <c>UseRouting</c> so the X-Ollama-Target header is set before route selection), then the
    /// hybrid short-circuit endpoints, the monitor endpoints, and finally the YARP catch-all.
    /// </summary>
    public static WebApplication UseOllamaRouterPipeline(this WebApplication app)
    {
        // 1. INSPECTION MIDDLEWARE (dynamic routing). Must run before UseRouting: because
        //    WebApplication implicitly inserts UseRouting at the very beginning as soon as any Map*
        //    is called, UseRouting() must be called explicitly here, after the middleware, so the
        //    header is set before route selection happens.
        app.UseMiddleware<OllamaRoutingMiddleware>();
        app.UseRouting();

        // 2. HYBRID ENDPOINTS /api/tags, /api/ps and /v1/models (short-circuit YARP).
        app.MapOllamaRouterEndpoints();

        // Lightweight activity monitoring UI (no VRAM impact, in-memory state only).
        app.MapOllamaMonitorEndpoints();

        // 3. YARP CATCH-ALL.
        app.MapReverseProxy();

        return app;
    }

    /// <summary>
    /// Resolves the configured fleet targets plus the Cloud pseudo-target (deduplicated,
    /// case-insensitively). Invalid <c>Targets</c> entries are skipped with a warning.
    /// </summary>
    public static IReadOnlyList<RoutingTarget> BuildFleetTargets(OllamaRouterOptions options, ILogger logger)
    {
        var targets = options.GetEffectiveTargets(logger)
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => (RoutingTarget)t.Name!)
            .DistinctBy(t => t.Value ?? "");

        var list = targets.ToList();
        if (!list.Any(t => t == RoutingTarget.Cloud))
        {
            list.Add(RoutingTarget.Cloud);
        }

        return list;
    }
}
