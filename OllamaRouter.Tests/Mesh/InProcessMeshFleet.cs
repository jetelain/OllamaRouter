using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// The shared catalog fixtures (plan §3): a large dense model hosted on the desktop (with a
/// Modelfile-pinned <c>num_ctx</c> of 49,152) and on the server, plus a MoE model hosted on
/// every daemon. The dense model is intentionally ABSENT from the laptop, so the laptop doubles
/// as the catalog-gate / LargeDense-incompatibility fixture (8 GB &lt; 12,000 MB threshold).
/// </summary>
public static class MeshCatalog
{
    public const string DenseModel = "dense-27b:latest";
    public const string MoeModel = "moe-35b:latest";

    private const long DenseParams = 27_800_000_000L; // 27.8B -> LargeDense (>= 14B threshold).
    private const long MoeParams = 35_000_000_000L;
    private const long DenseBytes = 16L * 1024 * 1024 * 1024; // ~16 GB weights.
    private const long MoeBytes = 8L * 1024 * 1024 * 1024;

    public static IReadOnlyList<DaemonModel> DesktopModels() =>
    [
        new DaemonModel
        {
            Name = DenseModel,
            SizeBytes = DenseBytes,
            ParameterCount = DenseParams,
            ExpertCount = 0,
            ModelfileNumCtx = 49_152, // Hierarchy level-1 ceiling for the desktop dense alias.
            Family = "llama"
        },
        new DaemonModel
        {
            Name = MoeModel,
            SizeBytes = MoeBytes,
            ParameterCount = MoeParams,
            ExpertCount = 16, // MoE signal (expert_count > 0).
            Family = "llama"
        }
    ];

    public static IReadOnlyList<DaemonModel> LaptopModels() =>
    [
        new DaemonModel
        {
            Name = MoeModel,
            SizeBytes = MoeBytes,
            ParameterCount = MoeParams,
            ExpertCount = 16,
            Family = "llama"
        }
    ];

    public static IReadOnlyList<DaemonModel> ServerModels() =>
    [
        new DaemonModel
        {
            Name = DenseModel,
            SizeBytes = DenseBytes,
            ParameterCount = DenseParams,
            ExpertCount = 0,
            ModelfileNumCtx = null, // server dense has no Modelfile pin -> 262,144 hardware ceiling.
            Family = "llama"
        },
        new DaemonModel
        {
            Name = MoeModel,
            SizeBytes = MoeBytes,
            ParameterCount = MoeParams,
            ExpertCount = 16,
            Family = "llama"
        }
    ];
}

/// <summary>
/// One in-process router: a real <see cref="WebApplication"/> running the PRODUCTION pipeline
/// (<see cref="OllamaRouterServiceCollectionExtensions.AddOllamaRouter"/> +
/// <see cref="OllamaRouterServiceCollectionExtensions.UseOllamaRouterPipeline"/>) on an ephemeral
/// loopback port, wired to the shared fleet daemons with a temporary state directory and a short
/// injected liveness re-probe interval (so rejoin assertions are fast while still exercising the
/// real 10 s cadence code path proportionally).
/// </summary>
public sealed class MeshRouter : IAsyncDisposable
{
    private MeshRouter(string name, WebApplication app, OllamaRouterOptions options, IServiceProvider services)
    {
        Name = name;
        App = app;
        Options = options;
        Services = services;
        BaseUrl = ResolveBoundUrl(app);
        Client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
    }

    public string Name { get; }

    public WebApplication App { get; }

    public OllamaRouterOptions Options { get; }

    public IServiceProvider Services { get; }

    public string BaseUrl { get; }

    public int Port => new Uri(BaseUrl).Port;

    public HttpClient Client { get; }

    // ---- Typed service handles (plan §3). ----
    public ITargetLivenessService Liveness => Services.GetRequiredService<ITargetLivenessService>();
    public ITargetExecutionTracker ExecutionTracker => Services.GetRequiredService<ITargetExecutionTracker>();
    public ITargetFailureStreakService Streaks => Services.GetRequiredService<ITargetFailureStreakService>();
    public IContextAutoShrinkManager ShrinkManager => Services.GetRequiredService<IContextAutoShrinkManager>();
    public IModelCatalogCacheService Catalog => Services.GetRequiredService<IModelCatalogCacheService>();
    public ITargetAvailabilityService Availability => Services.GetRequiredService<ITargetAvailabilityService>();

    public static async Task<MeshRouter> StartAsync(
        string name,
        IReadOnlyList<FakeOllamaDaemon> daemons,
        bool cloudEnabled,
        TimeSpan? reprobeInterval = null,
        TimeSpan? onlineCacheTtl = null,
        Action<WebApplication>? configurePipeline = null,
        Action<OllamaRouterOptions>? configureOptions = null)
    {
        var targets = daemons
            .Select(d => new OllamaTargetOptions
            {
                Name = d.Name,
                Url = d.Url,
                HardwareHint = d.HardwareHint,
                MaxParallel = d.MaxParallel
            })
            .ToList();

        var options = new OllamaRouterOptions
        {
            BindAddress = "http://127.0.0.1:0",
            Targets = targets,
            MaxConsecutiveFailures = 2,
            TerminalMonitoring = new TerminalMonitoringOptions { Enabled = false }
        };
        options.CloudOverflow.Enabled = cloudEnabled;
        options.DynamicContext.Enabled = true;
        configureOptions?.Invoke(options);
        options.DynamicContext.Normalize();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var stateDirectory = Path.Combine(Path.GetTempPath(), $"ollamarouter-mesh-{Guid.NewGuid():N}");
        builder.Services.AddOllamaRouter(
            options,
            stateDirectory: stateDirectory,
            enableTui: false,
            reprobeInterval: reprobeInterval ?? TimeSpan.FromSeconds(1),
            onlineCacheTtl: onlineCacheTtl ?? TimeSpan.FromSeconds(2));

        var app = builder.Build();
        configurePipeline?.Invoke(app);
        app.UseOllamaRouterPipeline();
        await app.StartAsync();

        return new MeshRouter(name, app, options, app.Services);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
        catch
        {
            // Shutdown must never mask the test outcome.
        }
    }

    private static string ResolveBoundUrl(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            .Addresses;
        return addresses.First(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// A simulated fleet of N <see cref="FakeOllamaDaemon"/> instances (plan §3). Routers are created
/// on demand against the shared daemon set so every test gets an isolated (per-router) routing /
/// liveness / lease / streak graph while the daemons are the single observation plane.
/// </summary>
public sealed class InProcessMeshFleet : IAsyncDisposable
{
    private readonly List<FakeOllamaDaemon> _daemons = new();
    private readonly List<MeshRouter> _routers = new();

    private InProcessMeshFleet()
    {
    }

    public IReadOnlyList<FakeOllamaDaemon> Daemons => _daemons;
    public IReadOnlyList<MeshRouter> Routers => _routers;

    public FakeOllamaDaemon Daemon(string name) =>
        _daemons.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown daemon '{name}'.");

    public static async Task<InProcessMeshFleet> CreateAsync(
        string desktopHardwareHint = "16gb",
        int desktopMaxParallel = 1,
        string laptopHardwareHint = "8gb",
        int laptopMaxParallel = 1,
        string serverHardwareHint = "96gb",
        int serverMaxParallel = 2)
    {
        var fleet = new InProcessMeshFleet();

        var desktop = new FakeOllamaDaemon("desktop", MeshCatalog.DesktopModels(), capacity: desktopMaxParallel);
        desktop.HardwareHint = desktopHardwareHint;
        desktop.MaxParallel = desktopMaxParallel;

        var laptop = new FakeOllamaDaemon("laptop", MeshCatalog.LaptopModels(), capacity: laptopMaxParallel);
        laptop.HardwareHint = laptopHardwareHint;
        laptop.MaxParallel = laptopMaxParallel;

        var server = new FakeOllamaDaemon("server", MeshCatalog.ServerModels(), capacity: serverMaxParallel);
        server.HardwareHint = serverHardwareHint;
        server.MaxParallel = serverMaxParallel;

        fleet._daemons.AddRange([desktop, laptop, server]);
        await Task.WhenAll(fleet._daemons.Select(d => d.StartAsync()));
        return fleet;
    }

    public async Task<MeshRouter> CreateRouterAsync(
        string name,
        bool cloudEnabled = false,
        Action<WebApplication>? configurePipeline = null,
        Action<OllamaRouterOptions>? configureOptions = null)
    {
        var router = await MeshRouter.StartAsync(name, _daemons, cloudEnabled, configurePipeline: configurePipeline, configureOptions: configureOptions);
        _routers.Add(router);
        return router;
    }

    /// <summary>
    /// Resets every daemon's observation plane and fault behavior (clears recorded requests,
    /// loaded models, and chat-mode knobs) so a scenario's own requests are counted from a clean
    /// slate after warm-up/preload. The routers' in-memory capability/liveness caches are left
    /// intact (they are keyed by catalog digest and do not skew the scenario).
    /// </summary>
    public void ResetObservation()
    {
        foreach (var daemon in _daemons)
        {
            daemon.ResetBehavior();
            daemon.ResetRequests();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var router in _routers)
        {
            await router.DisposeAsync();
        }

        _routers.Clear();
        foreach (var daemon in _daemons)
        {
            await daemon.DisposeAsync();
        }

        _daemons.Clear();
    }
}
