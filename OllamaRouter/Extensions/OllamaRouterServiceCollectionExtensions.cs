using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Configuration;
using OllamaRouter.Endpoints;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Security;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Clients;
using OllamaRouter.Services.Hardware;
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
    /// Registers the complete OllamaRouter service graph, binding <see cref="OllamaRouterOptions"/>
    /// from <paramref name="configuration"/> for LIVE runtime rebinding: every service, the
    /// middleware and the endpoint handlers consume <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c>
    /// (never a static <c>IOptions</c> snapshot), so an on-disk change of the layered user
    /// configuration (<c>config/config.json</c>, <c>reloadOnChange</c>) is re-bound on the next
    /// dispatch / loop iteration without a process restart.
    /// <para>
    /// A one-shot STARTUP snapshot of the same section is still resolved for the registration-time
    /// structural artifacts that cannot (by design) be rebuilt at runtime:
    /// the state-directory files, the YARP route/cluster memory config, the per-target keyed
    /// stores, the <see cref="ITokenEstimator"/> engine singleton and the probe client timeout.
    /// Those remain startup-scoped: changing fleet membership (adding/removing targets), target
    /// <c>Url</c>/<c>RouterUrl</c>, <c>BindAddress</c> or the <c>TokenEstimator</c> engine requires
    /// a process restart (YARP dynamic configuration is a separate feature). EXCEPTION (task
    /// #19): the Setup API (<c>POST/PUT/DELETE /api/router/targets</c>, <c>PUT /api/router/config</c>,
    /// via <c>IRouterConfigPersistence</c>) makes membership + target-Url changes effective without
    /// a restart on the inference (completion) dispatch path (the middleware resolves the
    /// candidate list live from the options per request); YARP passthrough paths keep the startup
    /// route/cluster table until the follow-up "YARP dynamic topology" task.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The layered application configuration (factory
    /// <c>appsettings.json</c> + user <c>config/config.json</c>); the <c>OllamaRouter</c> section is
    /// bound into <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c> and re-normalized on every rebind
    /// by <see cref="OllamaRouterOptionsNormalizer"/>.</param>
    /// <param name="stateDirectory">
    /// Directory for the persisted state files. Defaults to
    /// <c>%LOCALAPPDATA%\OllamaRouter</c> (production); tests pass a temp directory.
    /// </param>
    /// <param name="enableTui">
    /// When false, disables the in-terminal activity monitor (mirrors the <c>--no-tui</c> flag)
    /// via a <c>PostConfigure</c> that is enforced on every bind/reload.
    /// </param>
    /// <param name="reprobeInterval">Optional liveness re-probe interval override (default 10 s).</param>
    /// <param name="onlineCacheTtl">Optional Online-verdict TTL override (default 15 s).</param>
    /// <param name="probeTimeout">Optional liveness probe HTTP client timeout override (default 3.5 s).</param>
    public static IServiceCollection AddOllamaRouter(
        this IServiceCollection services,
        IConfiguration configuration,
        string? stateDirectory = null,
        bool enableTui = true,
        TimeSpan? reprobeInterval = null,
        TimeSpan? onlineCacheTtl = null,
        TimeSpan? probeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Dynamic options infrastructure: IOptionsMonitor re-binds when the reloadOnChange
        // change token fires; the normalizer re-applies the clamps on every bind/rebind.
        services.AddOptions();
        services.Configure<OllamaRouterOptions>(configuration.GetSection(OllamaRouterOptions.SectionName));
        services.AddSingleton<IPostConfigureOptions<OllamaRouterOptions>, OllamaRouterOptionsNormalizer>();

        if (!enableTui)
        {
            services.PostConfigure<OllamaRouterOptions>(opt => opt.TerminalMonitoring.Enabled = false);
        }

        // One-shot startup snapshot for the registration-time structural artifacts (state files,
        // YARP memory config, keyed stores, TokenEstimator engine, probe timeout) - see above.
        var startupOptions = configuration.GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>()
            ?? new OllamaRouterOptions();
        startupOptions.Normalize();
        startupOptions.DynamicContext.Normalize();
        startupOptions.StateDirectory = ResolveEffectiveStateDirectory(startupOptions, stateDirectory);

        return AddOllamaRouterCore(
            services,
            startupOptions,
            stateDirectory,
            reprobeInterval,
            onlineCacheTtl,
            probeTimeout);
    }

    /// <summary>
    /// Registers the complete OllamaRouter service graph for <paramref name="options"/>.
    /// <para>
    /// Backwards-compatible instance overload (unit tests, mesh fleet): <paramref name="options"/>
    /// is registered as a singleton, as the static <c>IOptions&lt;OllamaRouterOptions&gt;</c> snapshot
    /// AND (via <c>TryAddSingleton</c>) as a live-by-reference
    /// <see cref="OllamaRouter.Options.FixedOptionsMonitor{TOptions}"/> so the identical service
    /// graph resolves <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c> in every registration path.
    /// A caller-registered monitor (e.g. the test <c>TestOptionsMonitor&lt;T&gt;</c>) takes precedence
    /// because the monitor is added with <c>TryAddSingleton</c>.
    /// </para>
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
    /// <param name="onlineCacheTtl">Optional Online-verdict TTL override (default 15 s).</param>
    /// <param name="probeTimeout">Optional liveness probe HTTP client timeout override (default 3.5 s).</param>
    public static IServiceCollection AddOllamaRouter(
        this IServiceCollection services,
        OllamaRouterOptions options,
        string? stateDirectory = null,
        bool enableTui = true,
        TimeSpan? reprobeInterval = null,
        TimeSpan? onlineCacheTtl = null,
        TimeSpan? probeTimeout = null)
    {
        options.StateDirectory = ResolveEffectiveStateDirectory(options, stateDirectory);

        // Register the resolved options instance (not config-bound) so both production (where it was
        // already bound + normalized in Program.cs) and tests (constructed directly) share one value.
        services.AddSingleton(options);
        services.AddSingleton<IOptions<OllamaRouterOptions>>(MsOptions.Create(options));

        // Live-by-reference monitor for the same instance: the shared service graph consumes
        // IOptionsMonitor everywhere; a caller-registered monitor (tests) wins via TryAdd.
        services.TryAddSingleton<IOptionsMonitor<OllamaRouterOptions>>(_ => new FixedOptionsMonitor<OllamaRouterOptions>(options));

        if (!enableTui)
        {
            // The raw MsOptions.Create singleton does not run PostConfigure delegates, so the
            // --no-tui switch is applied directly to the shared instance (parity with the
            // configuration overload's PostConfigure, which re-enforces it on every rebind).
            options.TerminalMonitoring.Enabled = false;
        }

        return AddOllamaRouterCore(services, options, stateDirectory, reprobeInterval, onlineCacheTtl, probeTimeout);
    }

    /// <summary>
    /// Resolves the effective on-disk state directory: the explicit override, else the
    /// options-level <c>StateDirectory</c>, else <c>%LOCALAPPDATA%\OllamaRouter\state</c>.
    /// </summary>
    private static string ResolveEffectiveStateDirectory(OllamaRouterOptions options, string? stateDirectory)
        => string.IsNullOrWhiteSpace(stateDirectory)
            ? (string.IsNullOrWhiteSpace(options.StateDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OllamaRouter", "state")
                : options.StateDirectory)
            : stateDirectory;

    /// <summary>
    /// Shared registration core behind both <see cref="AddOllamaRouter"/> overloads.
    /// <paramref name="startupOptions"/> is a one-shot startup snapshot used for the
    /// registration-time structural artifacts only; every service SP factory resolves the live
    /// <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c> instead.
    /// </summary>
    private static IServiceCollection AddOllamaRouterCore(
        IServiceCollection services,
        OllamaRouterOptions startupOptions,
        string? stateDirectory,
        TimeSpan? reprobeInterval,
        TimeSpan? onlineCacheTtl,
        TimeSpan? probeTimeout)
    {
        // startupOptions.StateDirectory was set by the caller overload before the core runs.
        var effectiveStateDirectory = startupOptions.StateDirectory!;

        services.ConfigureHttpJsonOptions(httpJsonOptions =>
        {
            httpJsonOptions.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        // Runtime OpenAPI document for the /api/router/ management API (task #51): the .NET 10
        // built-in generator (first-party Microsoft.AspNetCore.OpenApi package, AOT-safe) serves
        // GET /api/router/openapi.json. Schema resolution reads IOptions<JsonOptions> configured
        // above, so every DTO resolves through the source-generated context (named component
        // schemas, zero drift, no reflection).
        services.AddRouterOpenApi();

        // Code-based YARP configuration: routes/clusters generated from the effective fleet target list.
        var (routes, clusters) = OllamaReverseProxyConfig.Build(startupOptions);
        services.AddReverseProxy().LoadFromMemory(routes, clusters);

        // Middleware-owned dispatch loop: a single HttpForwarder instance (YARP 2.3.0 has no DI-friendly
        // constructor); the dedicated proxy HttpClient + 15 min per-request ForwarderRequestConfig live on
        // the middleware (see OllamaRoutingMiddleware.ProxyClient).
        services.AddHttpForwarder();

        // Per-target failure streak tracker: singleton, thread-safe in-memory counter shared by the
        // routing decision service and the middleware's dispatch loop.
        services.AddSingleton<ITargetFailureStreakService, TargetFailureStreakService>();

        // Dynamic fleet configuration / Setup API (task #19): load-merge-atomic-write persistence
        // of the user config layer (config/config.json) + in-memory application to the shared
        // IOptionsMonitor-bound instance. Resolved defensively by the endpoint handlers
        // (IServiceProvider.GetService) so a minimal host without a host environment still serves
        // every endpoint; the IHostEnvironment lookup happens inside the constructor (bare
        // ServiceCollection hosts fall back to the assembly base directory).
        services.AddSingleton<IRouterConfigPersistence>(sp => new RouterConfigPersistence(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp,
            sp.GetService<ILoggerFactory>()?.CreateLogger<RouterConfigPersistence>()));

        // API key security (task #50): the at-rest protector is platform-selected at DI time
        // (DPAPI-wrapped default key on Windows; plain + 0600 permissions on Unix; unit tests
        // inject a fake protector so real DPAPI is never touched), the key store owns
        // config/default-key.dat + config/api-keys.json (lazy load, atomic writes) and the
        // rotation service auto-rotates the stale default key (class-based AddHostedService —
        // the factory form trips the .NET 10 TryAddEnumerable type-validation, see below).
        services.AddSingleton<IKeyProtector>(_ =>
            OperatingSystem.IsWindows() ? new DpapiKeyProtector() : new PlainKeyProtector());
        services.AddSingleton<IApiKeyStore>(sp => new ApiKeyStore(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp,
            sp.GetRequiredService<IKeyProtector>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<ApiKeyStore>()));
        services.AddHostedService<DefaultKeyRotationHostedService>();

        // Effective fleet targets plus the Cloud pseudo-target; used by the availability and statistics
        // services so state is keyed per target. Invalid Targets entries are skipped with a warning here.
        var startupLogger = LoggerFactory.Create(_ => { }).CreateLogger("OllamaRouter.Startup");
        var fleetTargets = BuildFleetTargets(startupOptions, startupLogger);

        services.AddHttpClient<IOllamaModelCatalogClient, OllamaModelCatalogClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(4);
        });

        // Dedicated liveness probe client: 3.5 s default timeout and a 10 s pooled-connection lifetime so a
        // sleeping-then-woken machine with a changed IP does not keep hitting dead pooled sockets.
        services.AddHttpClient("LivenessProbe", client =>
        {
            client.Timeout = probeTimeout ?? TimeSpan.FromMilliseconds(3500);
        })
        .ConfigurePrimaryHttpMessageHandler(_ =>
        {
            return new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(10) };
        });
        services.AddMemoryCache();

        // Dedicated fleet hardware probe client: 3 s timeout + 10 s pooled-connection lifetime
        // (same shape as the liveness probe client) so an unreachable peer daemon never stalls
        // the fleet hardware endpoint beyond the per-probe budget.
        services.AddHttpClient("HardwareProbe", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(3);
        })
        .ConfigurePrimaryHttpMessageHandler(_ =>
        {
            return new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(10) };
        });

        if (string.Equals(startupOptions.TokenEstimator, "Tiktoken", StringComparison.OrdinalIgnoreCase))
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
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp.GetRequiredService<ILogger<CloudRoutingTargetHandler>>()));
        services.AddTransient<IRoutingDecisionService>(sp => new RoutingDecisionService(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp.GetRequiredService<IModelCatalogCacheService>(),
            sp.GetRequiredService<ITargetLivenessService>(),
            sp.GetRequiredService<ITargetAvailabilityService>(),
            sp.GetRequiredService<ITargetExecutionTracker>(),
            sp.GetRequiredService<ModelCapabilityEngine>(),
            sp.GetRequiredService<CloudRoutingTargetHandler>(),
            sp.GetRequiredService<ILogger<RoutingDecisionService>>(),
            sp.GetRequiredService<ITargetFailureStreakService>(),
            sp.GetRequiredService<ISessionAffinityService>(),
            sp.GetRequiredService<IFleetHardwareService>()));
        // Multi-agent session/agent sticky-routing lease store: thread-safe singleton with a
        // sliding TTL (OllamaRouterOptions.SessionAffinityTtlMinutes, default 30 min) and a
        // bounded lazy sweep — shared by the decision service (Stage-2 bonus + rebinding) and
        // the middleware (header extraction, failover/liveness invalidation).
        services.AddSingleton<ISessionAffinityService, SessionAffinityService>();
        services.AddSingleton<IModelCatalogCacheService, ModelCatalogCacheService>();
        services.AddSingleton<IContextAutoShrinkManager>(sp => new ContextAutoShrinkManager(
            sp.GetRequiredService<ILogger<ContextAutoShrinkManager>>()));
        services.AddSingleton<IFleetAggregationService, FleetAggregationService>();

        // Fleet hardware topology store (singleton + targets-hardware.json persistence + hosted service).
        services.AddSingleton<FleetHardwareService>(sp => new FleetHardwareService(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp.GetRequiredService<IHardwareProfileProvider>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("HardwareProbe"),
            sp.GetRequiredService<ILogger<FleetHardwareService>>(),
            Path.Combine(effectiveStateDirectory, "targets-hardware.json")));
        services.AddSingleton<IFleetHardwareService>(sp => sp.GetRequiredService<FleetHardwareService>());
        services.AddHostedService(sp => sp.GetRequiredService<FleetHardwareService>());
        services.AddSingleton<ModelCapabilityEngine>();
        services.AddSingleton<IOllamaProcessLauncher, OllamaProcessLauncher>();

        // Model pre-load (task #44): the router-owned, stateless/idempotent model warm-up
        // (selection reuses the capability stack; the bounded load call + /api/ps residency
        // confirmation ride the existing IOllamaModelCatalogClient client — no new HttpClient
        // registration). All enrichment dependencies are nullable-tolerant so minimal test
        // hosts keep working (defensive, like every other endpoint in this graph).
        services.AddSingleton<IModelPreloadService>(sp => new ModelPreloadService(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp.GetRequiredService<IOllamaModelCatalogClient>(),
            sp.GetService<IModelCatalogCacheService>(),
            sp.GetService<ModelCapabilityEngine>(),
            sp.GetService<ITargetLivenessService>(),
            sp.GetService<ITargetAvailabilityService>(),
            sp.GetService<ITargetExecutionTracker>(),
            sp.GetService<IFleetHardwareService>(),
            sp.GetRequiredService<ILogger<ModelPreloadService>>()));

        // Micro-benchmark engine (task #28): the candidate catalog is code-defined in v1
        // (a config.json binding hook is out of scope), the "Benchmark" client carries no
        // client-level timeout (pulls are legitimately long; per-request CTS budgets apply)
        // and the runner persists <state>/benchmarks.json + <state>/targets-speed.json next
        // to targets-hardware.json (machine-generated state, never config/).
        services.AddSingleton<IOptions<CandidateModelCatalogOptions>>(
            MsOptions.Create(new CandidateModelCatalogOptions()));
        services.AddHttpClient("Benchmark", client =>
        {
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(_ =>
        {
            return new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(10) };
        });
        services.AddSingleton<IMicroBenchmarkRunner>(sp => new MicroBenchmarkRunner(
            sp.GetRequiredService<IOptions<CandidateModelCatalogOptions>>(),
            sp.GetRequiredService<IFleetHardwareService>(),
            sp.GetRequiredService<ITokenEstimator>(),
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp.GetRequiredService<IOllamaModelCatalogClient>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("Benchmark"),
            // Optional: test hosts without a full application host fall back to a token that
            // is never cancelled (cancellation is an optimization, not a contract).
            sp.GetService<IHostApplicationLifetime>() ?? NullHostApplicationLifetime.Instance,
            sp.GetRequiredService<ILogger<MicroBenchmarkRunner>>(),
            Path.Combine(effectiveStateDirectory, "benchmarks.json"),
            Path.Combine(effectiveStateDirectory, "targets-speed.json")));
        services.AddSingleton<IActivityMonitorService, ActivityMonitorService>();
        services.AddSingleton<IInferenceRequestRecorder, InferenceRequestRecorder>();
        services.AddSingleton<ITokenMetricsRecorder, TokenMetricsRecorder>();

        // OpenAI-compatible dispatch client factory (task #60): pooled per (base address, API key)
        // with the codebase-standard 10 s PooledConnectionLifetime handlers and infinite client
        // timeouts. Deterministically owned by the factory (no AddHttpClient registration — named
        // clients would double-manage the same handler/client lifecycle). Task #61 resolves its
        // per-target IChatClient from this singleton.
        services.AddSingleton<IOpenAiChatClientFactory, OpenAiChatClientFactory>();
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

        // In-terminal activity monitor (registered always; disabled via the option's
        // TerminalMonitoring.Enabled, enforced by the caller overload at registration time).
        services.AddHostedService<TerminalMonitoringHostedService>();

        // Target liveness prober (singleton + hosted service): boot probe + background re-prober.
        // The IHostedService registration uses a DIRECT factory (not AddHostedService(sp => ...))
        // because the factory-form overload trips a .NET 10 TryAddEnumerable type-validation
        // ("indistinguishable from other services registered for IHostedService"); the direct
        // registration is behavior-identical (IHostedService is an IEnumerable aggregate resolved
        // by the host the same way) and works in both the production host and the test host.
        services.AddSingleton<ITargetLivenessService>(sp => new TargetLivenessService(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp.GetRequiredService<IModelCatalogCacheService>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("LivenessProbe"),
            sp.GetRequiredService<ILogger<TargetLivenessService>>(),
            reprobeInterval,
            onlineCacheTtl));
        services.AddSingleton<IHostedService>(sp => (IHostedService)sp.GetRequiredService<ITargetLivenessService>());

        // Multi-target execution state tracker (singleton + hosted refresh timer). The SP factory injects
        // the shared Auto-Shrink Manager so evicted models reset their leases.
        services.AddSingleton<ITargetExecutionTracker>(sp => new TargetExecutionTracker(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp.GetRequiredService<ITargetLivenessService>(),
            sp.GetRequiredService<ITargetAvailabilityService>(),
            sp.GetRequiredService<IOllamaModelCatalogClient>(),
            sp.GetRequiredService<ILogger<TargetExecutionTracker>>(),
            contextShrinkManager: sp.GetRequiredService<IContextAutoShrinkManager>()));
        services.AddHostedService<TargetExecutionTrackerRefreshService>();

        return services;
    }

    /// <summary>
    /// Applies the OllamaRouter request pipeline in the exact production order: the API key
    /// auth middleware FIRST (a 401 short-circuits before everything else; path-based
    /// protection of the config-affecting route set, or of EVERY route when
    /// <c>ApiSecurity.RequireKeyForAllEndpoints</c> is set), then the routing middleware
    /// (before <c>UseRouting</c> so the X-Ollama-Target header is set before route selection), then the
    /// hybrid short-circuit endpoints, the monitor endpoints, and finally the YARP catch-all.
    /// </summary>
    public static WebApplication UseOllamaRouterPipeline(this WebApplication app)
    {
        // 0. API KEY AUTH (task #50): must run FIRST so a 401 short-circuits before header
        //    sanitization/dispatch, endpoint selection and the YARP proxy.
        app.UseMiddleware<ApiKeyAuthMiddleware>();

        // 1. INSPECTION MIDDLEWARE (dynamic routing). Must run before UseRouting: because
        //    WebApplication implicitly inserts UseRouting at the very beginning as soon as any Map*
        //    is called, UseRouting() must be called explicitly here, after the middleware, so the
        //    header is set before route selection happens.
        app.UseMiddleware<OllamaRoutingMiddleware>();
        app.UseRouting();

        // 2. HYBRID ENDPOINTS /api/tags, /api/ps and /v1/models (short-circuit YARP).
        app.MapOllamaRouterEndpoints();

        // 2b. PROGRAMMATIC ROUTER API /api/router/info + /api/router/fleet (lightweight
        //     identity / fleet hardware topology endpoints for external tooling).
        app.MapOllamaRouterApiEndpoints();

        // 2c. RUNTIME OPENAPI DOCUMENT (task #51): the /api/router/ management API description,
        //     generated per request from the endpoint metadata + the source-generated DTO
        //     schemas (always in sync; AOT-safe). Mapped unconditionally (ALL environments —
        //     not Development-gated). The {documentName} route parameter keeps the pattern
        //     single-segment (other *.json names 404) and the route intersects no existing
        //     endpoint (the models/{*...} catch-alls live under the /models/ prefix).
        app.MapOpenApi(RouterOpenApiConventions.DocumentRoute);

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

    /// <summary>
    /// Fallback <see cref="IHostApplicationLifetime"/> for bare ServiceCollection hosts (tests)
    /// that have no application host: the stopping token is never cancelled, which is a
    /// safe degradation (benchmark-run cancellation is an optimization, not a contract).
    /// </summary>
    private sealed class NullHostApplicationLifetime : IHostApplicationLifetime
    {
        public static readonly NullHostApplicationLifetime Instance = new();

        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;

        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;

        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication() { }
    }
}
