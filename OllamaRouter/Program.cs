using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Serialization;
using OllamaRouter.Services;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "Ollama Router";

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
});

builder.Services.AddOptions<OllamaRouterOptions>()
    .BindConfiguration(OllamaRouterOptions.SectionName);

// Code-based YARP configuration: routes/clusters are generated dynamically from the effective
// fleet target list (synthesized from LocalUrl/RemoteUrl in the legacy configuration).
var ollamaOptions = builder.Configuration.GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>()
    ?? new OllamaRouterOptions();
var (routes, clusters) = OllamaReverseProxyConfig.Build(ollamaOptions);
builder.Services.AddReverseProxy()
    .LoadFromMemory(routes, clusters);

// Effective fleet targets plus the Cloud pseudo-target (always present); used by the
// availability and statistics services so state is keyed per target.
// Invalid Targets entries are skipped with a warning logged here, at startup.
var startupLoggerFactory = LoggerFactory.Create(loggingBuilder =>
    loggingBuilder.AddConfiguration(builder.Configuration.GetSection("Logging")));
var fleetTargets = BuildFleetTargets(ollamaOptions, startupLoggerFactory.CreateLogger("OllamaRouter.Startup"));

builder.Services.AddHttpClient<IOllamaModelCatalogClient, OllamaModelCatalogClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(4);
});

// Dedicated liveness probe client: short 1.5 s timeout so an on-demand probe can never
// block a request longer than that, and a 10 s pooled-connection lifetime so a
// sleeping-then-woken machine with a changed IP does not keep hitting dead pooled sockets.
builder.Services.AddHttpClient("LivenessProbe", client =>
{
    client.Timeout = TimeSpan.FromMilliseconds(1500);
})
.ConfigurePrimaryHttpMessageHandler(_ =>
{
    // A 10 s pooled-connection lifetime so a sleeping-then-woken machine with a
    // changed IP does not keep hitting dead pooled sockets.
    return new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(10) };
});
builder.Services.AddMemoryCache();

if (string.Equals(ollamaOptions.TokenEstimator, "Tiktoken", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ITokenEstimator, TiktokenTokenEstimator>();
}
else
{
    builder.Services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
}
builder.Services.AddSingleton<IGpuVramProvider, NvidiaSmiVramProvider>();
builder.Services.AddTransient<IRoutingTargetHandler, LocalRoutingTargetHandler>();
builder.Services.AddTransient<IRoutingTargetHandler, RemoteRoutingTargetHandler>();
builder.Services.AddTransient<IRoutingTargetHandler, CloudRoutingTargetHandler>();
builder.Services.AddTransient<IRoutingDecisionService, RoutingDecisionService>();
builder.Services.AddSingleton<IModelCatalogCacheService, ModelCatalogCacheService>();
// Model capability discovery engine (Phase 2): inspects /api/tags + /api/show per target,
// caches parsed capabilities keyed by target + model + catalog digest, and derives the
// architecture classification and per-target context ceilings.
builder.Services.AddSingleton<ModelCapabilityEngine>();
builder.Services.AddSingleton<IOllamaProcessLauncher, OllamaProcessLauncher>();
builder.Services.AddSingleton<IActivityMonitorService, ActivityMonitorService>();
// Optional inference request recording (estimated vs actual token counts + full request bodies);
// a no-op when "OllamaRouter:Recording:Enabled" is not set to true.
builder.Services.AddSingleton<IInferenceRequestRecorder, InferenceRequestRecorder>();
builder.Services.AddSingleton<IActivityStatisticsService>(sp =>
    new ActivityStatisticsService(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OllamaRouter",
            "activity-statistics.json"),
        fleetTargets,
        sp.GetRequiredService<IHostApplicationLifetime>(),
        sp.GetRequiredService<ILogger<ActivityStatisticsService>>()));
builder.Services.AddSingleton<ITargetAvailabilityService>(sp =>
    new TargetAvailabilityService(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OllamaRouter",
            "targets.json"),
        fleetTargets,
        sp.GetRequiredService<ILogger<TargetAvailabilityService>>()));

var disableTui = args.Any(a => string.Equals(a, "--no-tui", StringComparison.OrdinalIgnoreCase));
if (disableTui)
{
    ollamaOptions.TerminalMonitoring.Enabled = false;
    builder.Services.PostConfigure<OllamaRouterOptions>(opt => opt.TerminalMonitoring.Enabled = false);
}
builder.Services.AddHostedService<TerminalMonitoringHostedService>();

// Target liveness prober (singleton + hosted service): asynchronous fleet probe at boot
// and a 10 s background re-prober that rejoins sleeping/roaming machines. Named-client
// pattern (not AddHttpClient<T>) because the typed-client registration is transient and
// this must be a singleton; the probe HttpClient from IHttpClientFactory is NOT disposed
// by the service (only its CancellationTokenSource is).
builder.Services.AddSingleton<ITargetLivenessService>(sp => new TargetLivenessService(
    sp.GetRequiredService<IOptions<OllamaRouterOptions>>(),
    sp.GetRequiredService<IModelCatalogCacheService>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("LivenessProbe"),
    sp.GetRequiredService<ILogger<TargetLivenessService>>(),
    TimeSpan.FromSeconds(10)));
builder.Services.AddHostedService(sp => (IHostedService)sp.GetRequiredService<ITargetLivenessService>());

var app = builder.Build();

// Resolves the configured fleet targets plus the Cloud pseudo-target (deduplicated, case-insensitively).
static IReadOnlyList<RoutingTarget> BuildFleetTargets(OllamaRouterOptions options, ILogger logger)
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

// Startup fleet log line + deprecation warnings.
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OllamaRouter.Startup");
    startupLogger.LogInformation("Effective fleet targets: {Targets}", string.Join(", ", fleetTargets.Select(t => t.Value ?? "")));

    if (ollamaOptions.Targets.Count > 0)
    {
        startupLogger.LogWarning(
            "OllamaRouter:Targets is configured: routing decisions currently still operate on the legacy LocalUrl/RemoteUrl two-tier logic (full fleet decision logic is planned for a follow-up release). LocalUrl/RemoteUrl remain the instances used by the routing handlers.");
    }

    var nonDefaultModels = ollamaOptions.Models
        .Where(pair => pair.Value.MaxLocalTokens != ModelThresholds.DefaultMaxLocalTokens ||
                    pair.Value.MinRequiredVramMB != ModelThresholds.DefaultMinRequiredVramMB)
        .Select(pair => pair.Key)
        .ToList();
    if (nonDefaultModels.Count > 0)
    {
        startupLogger.LogWarning(
            "OllamaRouter:Models[{Models}]:MaxLocalTokens/MinRequiredVramMB are deprecated and will be superseded by hardware suitability rules; they are slated for removal after the transition release (the keys keep working in this release).", string.Join(", ", nonDefaultModels));
    }
}

// Ensure local Ollama is running (Windows only).
app.Services.GetRequiredService<IOllamaProcessLauncher>().EnsureRunning();

// 1. INSPECTION MIDDLEWARE (dynamic routing)
// Must run before UseRouting: UseRouting is what selects the YARP endpoint based on the
// X-Ollama-Target header. Because WebApplication implicitly inserts UseRouting at the very
// beginning of the pipeline as soon as any Map* is called (regardless of its position in the
// source code), UseRouting() must be called explicitly here, after our middleware, to ensure
// the header is set before route selection happens.
app.UseMiddleware<OllamaRoutingMiddleware>();
app.UseRouting();

// 2. HYBRID ENDPOINTS /api/tags, /api/ps and /v1/models (short-circuit YARP)
app.MapOllamaRouterEndpoints();

// Lightweight activity monitoring UI (no VRAM impact, in-memory state only).
app.MapOllamaMonitorEndpoints();

// 3. YARP CATCH-ALL
app.MapReverseProxy();

// Print a clickable link to the monitoring UI once the server has started (fallback when TUI is disabled).
app.Lifetime.ApplicationStarted.Register(() =>
{
    var monitorUrl = ollamaOptions.BindAddress.TrimEnd('/').Replace("+", "localhost") + "/monitor";
    if (!ollamaOptions.TerminalMonitoring.Enabled || Console.IsOutputRedirected)
    {
        Console.WriteLine("✅ Ollama Router is running");
        Console.WriteLine();
        Console.WriteLine($"📊 Activity monitor available at: {monitorUrl}");
    }
});

// Listen on the configured address (defaults to the standard Ollama port).
app.Run(ollamaOptions.BindAddress);

// Entry point exposed for integration tests (WebApplicationFactory).
public partial class Program;
