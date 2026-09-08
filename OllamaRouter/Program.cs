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

builder.Services.AddSingleton<ITokenEstimator, TiktokenTokenEstimator>();
builder.Services.AddSingleton<IGpuVramProvider, NvidiaSmiVramProvider>();
builder.Services.AddTransient<IOllamaModelCatalogClient, OllamaModelCatalogClient>();
builder.Services.AddTransient<IRoutingDecisionService, RoutingDecisionService>();

var app = builder.Build();

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

// 3. YARP CATCH-ALL
app.MapReverseProxy();

// Listen on the standard Ollama port
app.Run("http://localhost:11434");

// Entry point exposed for integration tests (WebApplicationFactory).
public partial class Program;
