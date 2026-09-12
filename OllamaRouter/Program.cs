using OllamaRouter.Endpoints;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<OllamaRouterOptions>()
    .BindConfiguration(OllamaRouterOptions.SectionName);

// Code-based YARP configuration: only the local/remote addresses come from configuration.
var ollamaOptions = builder.Configuration.GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>()
    ?? new OllamaRouterOptions();
var (routes, clusters) = OllamaReverseProxyConfig.Build(ollamaOptions);
builder.Services.AddReverseProxy()
    .LoadFromMemory(routes, clusters);

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<ITokenEstimator, TiktokenTokenEstimator>();
builder.Services.AddSingleton<IGpuVramProvider, NvidiaSmiVramProvider>();
builder.Services.AddTransient<IOllamaModelCatalogClient, OllamaModelCatalogClient>();
builder.Services.AddTransient<IRoutingDecisionService, RoutingDecisionService>();
builder.Services.AddSingleton<IModelCatalogCacheService, ModelCatalogCacheService>();
builder.Services.AddSingleton<IOllamaProcessLauncher, OllamaProcessLauncher>();
builder.Services.AddSingleton<IActivityMonitorService, ActivityMonitorService>();
builder.Services.AddSingleton<IActivityStatisticsService>(sp =>
    new ActivityStatisticsService(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OllamaRouter",
            "activity-statistics.json"),
        sp.GetRequiredService<IHostApplicationLifetime>(),
        sp.GetRequiredService<ILogger<ActivityStatisticsService>>()));
builder.Services.AddSingleton<ITargetAvailabilityService>(sp =>
    new TargetAvailabilityService(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OllamaRouter",
            "targets.json"),
        sp.GetRequiredService<ILogger<TargetAvailabilityService>>()));

var app = builder.Build();

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

// Print a clickable link to the monitoring UI once the server has started.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var monitorUrl = ollamaOptions.BindAddress.TrimEnd('/').Replace("+", "localhost") + "/monitor";
    Console.WriteLine($"Activity monitor available at: {monitorUrl}");
});

// Listen on the configured address (defaults to the standard Ollama port).
app.Run(ollamaOptions.BindAddress);

// Entry point exposed for integration tests (WebApplicationFactory).
public partial class Program;
