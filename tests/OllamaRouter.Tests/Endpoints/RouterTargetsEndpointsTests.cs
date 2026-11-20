using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Configuration;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using OllamaRouter.Tests.TestHelpers;
using Pmad.LocalApi.Security;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Host tests for the task #95 failure-streak reset admin endpoints:
/// <c>POST /api/router/targets/{name}/reset-streak</c> (200 / 404 unknown-or-Cloud / 500
/// no-streak-service, 401 without a key in default mode) and
/// <c>POST /api/router/fleet/reset-streaks</c> (200 with the per-target
/// previousStreak/reset summary + resetCount, 500 no-streak-service, 401 keyless). The hosts
/// run the production <see cref="ApiKeyAuthMiddleware"/> + a REAL
/// <see cref="ApiKeyStore"/> (temp content root, <see cref="PlainKeyProtector"/>) and the REAL
/// <see cref="TargetFailureStreakService"/> singleton (exactly like production — the same
/// singleton the routing middleware gates on). The whole class runs on exactly THREE
/// long-lived shared hosts (cloud-enabled + service, cloud-disabled + service, no service),
/// lease-acquired per test via reference counting — xUnit executes a class's tests
/// sequentially, so the leases are race-free, and each test re-seeds the streak state it
/// needs (leaving the hosts clean on exit).
/// </summary>
public class RouterTargetsEndpointsTests
{
    private enum HostKind
    {
        CloudOff,
        CloudOn,
        NoService
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            WebApplication app,
            HttpClient client,
            IApiKeyStore keys,
            ITargetFailureStreakService? streakService,
            string root)
        {
            App = app;
            Client = client;
            Keys = keys;
            StreakService = streakService;
            Root = root;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public IApiKeyStore Keys { get; }
        public ITargetFailureStreakService? StreakService { get; }
        public string Root { get; }

        public string Key => Keys.GetDefaultKey();

        public Task<HttpResponseMessage> PostAsync(string path, string? key = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (key is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await App.StopAsync();
                await App.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(Root))
                    {
                        Directory.Delete(Root, recursive: true);
                    }
                }
                catch
                {
                    // Temp cleanup must never mask the test outcome.
                }
            }
        }
    }

    /// <summary>
    /// Reference-counted lease on one of the three shared hosts: the host starts on the
    /// first acquire, stops on the LAST release of the whole class, and is re-created on a
    /// later test (disposal nulls the cell). xUnit executes a class's tests sequentially,
    /// so the plain fields are race-free by construction.
    /// </summary>
    private sealed class Lease : IAsyncDisposable
    {
        public Lease(HostKind kind, Harness harness)
        {
            Kind = kind;
            Harness = harness;
        }

        public HostKind Kind { get; }
        public Harness Harness { get; }

        public ValueTask DisposeAsync() => SharedHosts.ReleaseAsync(Kind);
    }

    private static class SharedHosts
    {
        private static Harness? _cloudOff;
        private static int _cloudOffRefs;
        private static Harness? _cloudOn;
        private static int _cloudOnRefs;
        private static Harness? _noService;
        private static int _noServiceRefs;

        public static async Task<Lease> AcquireAsync(HostKind kind)
        {
            switch (kind)
            {
                case HostKind.CloudOff:
                    _cloudOffRefs++;
                    _cloudOff ??= await StartAsync(kind);
                    return new Lease(HostKind.CloudOff, _cloudOff);
                case HostKind.CloudOn:
                    _cloudOnRefs++;
                    _cloudOn ??= await StartAsync(kind);
                    return new Lease(HostKind.CloudOn, _cloudOn);
                default:
                    _noServiceRefs++;
                    _noService ??= await StartAsync(kind);
                    return new Lease(HostKind.NoService, _noService);
            }
        }

        public static async ValueTask ReleaseAsync(HostKind kind)
        {
            switch (kind)
            {
                case HostKind.CloudOff:
                    if (--_cloudOffRefs == 0)
                    {
                        await _cloudOff!.DisposeAsync();
                        _cloudOff = null;
                    }

                    break;
                case HostKind.CloudOn:
                    if (--_cloudOnRefs == 0)
                    {
                        await _cloudOn!.DisposeAsync();
                        _cloudOn = null;
                    }

                    break;
                default:
                    if (--_noServiceRefs == 0)
                    {
                        await _noService!.DisposeAsync();
                        _noService = null;
                    }

                    break;
            }
        }

        private static async Task<Harness> StartAsync(HostKind kind)
        {
            var options = kind switch
            {
                HostKind.CloudOn => TwoTargetOptions(cloudEnabled: true),
                _ => TwoTargetOptions(cloudEnabled: false)
            };

            var root = Path.Combine(Path.GetTempPath(), $"ollamarouter-reset-streak-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = root
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.ConfigureHttpJsonOptions(opt =>
                opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default));

            var optionsMonitor = new TestOptionsMonitor<OllamaRouterOptions>(options);
            builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(optionsMonitor);

            // The REAL key store on the temp content root (config/default-key.dat is generated
            // there; PlainKeyProtector keeps the envelope plain on any OS — no DPAPI). Task
            // #115: the package store takes the explicit config directory
            // (<ContentRootPath>/config, the same path the production DI resolves).
            builder.Services.AddSingleton<IApiKeyStore>(sp =>
                new ApiKeyStore(Path.Combine(root, "config"), new PlainKeyProtector()));

            // The REAL streak service (the same singleton lifetime production uses via
            // AddSingleton in AddOllamaRouterCore) — omitted entirely on the degraded host.
            if (kind != HostKind.NoService)
            {
                builder.Services.AddSingleton<ITargetFailureStreakService, TargetFailureStreakService>();
            }

            var app = builder.Build();
            app.UseMiddleware<ApiKeyAuthMiddleware>();
            app.MapRouterTargetsEndpoints();
            await app.StartAsync();

            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(15) };
            return new Harness(
                app,
                client,
                app.Services.GetRequiredService<IApiKeyStore>(),
                app.Services.GetService<ITargetFailureStreakService>(),
                root);
        }
    }

    private static OllamaRouterOptions TwoTargetOptions(bool cloudEnabled) => new()
    {
        MaxConsecutiveFailures = 3,
        FailureStreakCooldownSeconds = 60,
        CloudOverflow = { Enabled = cloudEnabled },
        Targets =
        [
            new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.100:11434", MaxParallel = 1 },
            new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", MaxParallel = 1 }
        ]
    };

    private static void TripTarget(ITargetFailureStreakService streakService, RoutingTarget target, int failures = 4)
    {
        for (var i = 0; i < failures; i++)
        {
            streakService.RecordFailure(target);
        }
    }

    /// <summary>
    /// Zeroes every tracked streak on the host so shared-host tests stay independent of
    /// test order and leave no residue for the next test.
    /// </summary>
    private static void CleanStreaks(Harness harness, bool includeCloud)
    {
        var streakService = harness.StreakService;
        if (streakService is null)
        {
            return;
        }

        streakService.ResetStreak(new RoutingTarget("Server"));
        streakService.ResetStreak(new RoutingTarget("Local"));
        if (includeCloud)
        {
            streakService.ResetStreak(RoutingTarget.Cloud);
        }
    }

    // ------------------------------------------------------------------
    //  POST /api/router/targets/{name}/reset-streak
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResetStreak_TrippedTarget_Returns200_ZeroStreak_Closed_AndIsImmediatelyRoutable()
    {
        await using var lease = await SharedHosts.AcquireAsync(HostKind.CloudOff);
        var harness = lease.Harness;
        var streakService = harness.StreakService!;
        var cooldown = TimeSpan.FromSeconds(60);
        CleanStreaks(harness, includeCloud: false);

        // Trip the circuit: 4 consecutive failures exceed the threshold of 3 → Open ("Cooldown").
        TripTarget(streakService, new RoutingTarget("Server"));
        Assert.Equal(FailureStreakCircuitState.Open, streakService.GetCircuitState(
            new RoutingTarget("Server"), 3, cooldown));

        using var response = await harness.PostAsync("/api/router/targets/Server/reset-streak", harness.Key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.True(body["success"]!.GetValue<bool>());
        Assert.Equal("Server", body["target"]!.GetValue<string>());
        Assert.Equal(0, body["streak"]!.GetValue<int>());
        Assert.Equal("Closed", body["status"]!.GetValue<string>());
        Assert.Null(body["error"]);

        // The shared singleton is reset: streak 0, last failure cleared, circuit Closed —
        // the target is eligible on the very next routing decision (no restart, no config).
        Assert.Equal(0, streakService.GetStreak(new RoutingTarget("Server")));
        Assert.Null(streakService.GetLastFailureUtc(new RoutingTarget("Server")));
        Assert.Equal(FailureStreakCircuitState.Closed, streakService.GetCircuitState(
            new RoutingTarget("Server"), 3, cooldown));
        Assert.False(streakService.ShouldSkip(new RoutingTarget("Server"), 3, cooldown));
    }

    [Fact]
    public async Task ResetStreak_BelowThresholdStreak_Returns200_IsIdempotent_AndCaseInsensitive()
    {
        await using var lease = await SharedHosts.AcquireAsync(HostKind.CloudOff);
        var harness = lease.Harness;
        var streakService = harness.StreakService!;
        CleanStreaks(harness, includeCloud: false);

        // Below the threshold (streak 2 <= 3): still Closed, but the streak is still reset.
        TripTarget(streakService, new RoutingTarget("Local"), failures: 2);

        using (var first = await harness.PostAsync("/api/router/targets/Local/reset-streak", harness.Key))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var body = (JsonObject)JsonNode.Parse(await first.Content.ReadAsStringAsync())!;
            Assert.True(body["success"]!.GetValue<bool>());
            Assert.Equal(0, body["streak"]!.GetValue<int>());
            Assert.Equal("Closed", body["status"]!.GetValue<string>());
        }

        // Idempotent: a second call on the already-clean target still succeeds.
        using (var second = await harness.PostAsync("/api/router/targets/Local/reset-streak", harness.Key))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var body = (JsonObject)JsonNode.Parse(await second.Content.ReadAsStringAsync())!;
            Assert.True(body["success"]!.GetValue<bool>());
            Assert.Equal(0, body["streak"]!.GetValue<int>());
        }

        // Case-insensitive name lookup (any casing of the effective target resolves).
        TripTarget(streakService, new RoutingTarget("Server"));
        using (var lower = await harness.PostAsync("/api/router/targets/server/reset-streak", harness.Key))
        {
            Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        }

        Assert.Equal(0, streakService.GetStreak(new RoutingTarget("Server")));
        TripTarget(streakService, new RoutingTarget("Server"));
        using (var upper = await harness.PostAsync("/api/router/targets/SERVER/reset-streak", harness.Key))
        {
            Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        }

        Assert.Equal(0, streakService.GetStreak(new RoutingTarget("Server")));
    }

    [Fact]
    public async Task ResetStreak_UnknownTarget_Returns404_WithError_AndDoesNotTouchOtherTargets()
    {
        await using var lease = await SharedHosts.AcquireAsync(HostKind.CloudOff);
        var harness = lease.Harness;
        var streakService = harness.StreakService!;
        CleanStreaks(harness, includeCloud: false);
        TripTarget(streakService, new RoutingTarget("Server"));

        foreach (var name in new[] { "Nope", "Cloud" }) // Cloud: not in GetEffectiveTargets()
        {
            using var response = await harness.PostAsync($"/api/router/targets/{name}/reset-streak", harness.Key);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            Assert.False(body["success"]!.GetValue<bool>());
            Assert.Equal(name, body["target"]!.GetValue<string>());
            Assert.Equal($"target not found: {name}", body["error"]!.GetValue<string>());
        }

        // The 404s leave the OTHER targets untouched.
        Assert.Equal(4, streakService.GetStreak(new RoutingTarget("Server")));
        CleanStreaks(harness, includeCloud: false);
    }

    [Fact]
    public async Task ResetStreak_NoStreakService_UnknownTargetIs404_BeforeThe500_KnownTargetIs500()
    {
        await using var lease = await SharedHosts.AcquireAsync(HostKind.NoService);
        var harness = lease.Harness;
        Assert.Null(harness.StreakService);

        // 404 BEFORE 500: the membership check wins even in a minimal host without the
        // streak service.
        using (var notFound = await harness.PostAsync("/api/router/targets/Nope/reset-streak", harness.Key))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            var body = (JsonObject)JsonNode.Parse(await notFound.Content.ReadAsStringAsync())!;
            Assert.False(body["success"]!.GetValue<bool>());
            Assert.Equal("target not found: Nope", body["error"]!.GetValue<string>());
        }

        // ...and a KNOWN target on the same host is the 500 degraded shape.
        using (var serverError = await harness.PostAsync("/api/router/targets/Server/reset-streak", harness.Key))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, serverError.StatusCode);
            var body = (JsonObject)JsonNode.Parse(await serverError.Content.ReadAsStringAsync())!;

            Assert.False(body["success"]!.GetValue<bool>());
            Assert.Equal("Server", body["target"]!.GetValue<string>());
            Assert.Equal("failure-streak tracking is not available in this host", body["error"]!.GetValue<string>());
            Assert.Null(body["streak"]);
            Assert.Null(body["status"]);
        }
    }

    [Fact]
    public async Task ResetStreak_WithoutKey_Returns401_WithBearerChallenge_AndDoesNotReset()
    {
        await using var lease = await SharedHosts.AcquireAsync(HostKind.CloudOff);
        var harness = lease.Harness;
        CleanStreaks(harness, includeCloud: false);
        TripTarget(harness.StreakService!, new RoutingTarget("Server"));

        using var response = await harness.PostAsync("/api/router/targets/Server/reset-streak");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate?.FirstOrDefault()?.Scheme);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.False(string.IsNullOrWhiteSpace(body["error"]?.ToString()));

        // The rejected request must not have reset the streak.
        Assert.Equal(4, harness.StreakService!.GetStreak(new RoutingTarget("Server")));
        CleanStreaks(harness, includeCloud: false);
    }

    // ------------------------------------------------------------------
    //  POST /api/router/fleet/reset-streaks
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResetFleetStreaks_MixedFleet_ReportsPerTargetSummary_AndResetsEverything()
    {
        await using var lease = await SharedHosts.AcquireAsync(HostKind.CloudOn);
        var harness = lease.Harness;
        var streakService = harness.StreakService!;
        CleanStreaks(harness, includeCloud: true);

        TripTarget(streakService, new RoutingTarget("Server")); // tripped (4 > 3)
        TripTarget(streakService, new RoutingTarget("Local"), failures: 2); // below threshold
        TripTarget(streakService, RoutingTarget.Cloud, failures: 5); // cloud overflow relay failures

        using var response = await harness.PostAsync("/api/router/fleet/reset-streaks", harness.Key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.True(body["success"]!.GetValue<bool>());
        Assert.Equal(3, body["resetCount"]!.GetValue<int>());
        Assert.Null(body["error"]);

        var targets = (JsonArray)body["targets"]!;
        // Effective targets in configuration order, then the Cloud pseudo-target (enabled).
        Assert.Equal(3, targets.Count);
        Assert.Equal("Server", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
        Assert.Equal(4, ((JsonObject)targets[0]!)["previousStreak"]!.GetValue<int>());
        Assert.True(((JsonObject)targets[0]!)["reset"]!.GetValue<bool>());
        Assert.Equal("Local", ((JsonObject)targets[1]!)["target"]!.GetValue<string>());
        Assert.Equal(2, ((JsonObject)targets[1]!)["previousStreak"]!.GetValue<int>());
        Assert.True(((JsonObject)targets[1]!)["reset"]!.GetValue<bool>());
        Assert.Equal("Cloud", ((JsonObject)targets[2]!)["target"]!.GetValue<string>());
        Assert.Equal(5, ((JsonObject)targets[2]!)["previousStreak"]!.GetValue<int>());
        Assert.True(((JsonObject)targets[2]!)["reset"]!.GetValue<bool>());

        // All streaks are zero afterwards (every circuit Closed ⇒ routable on the next
        // routing decision).
        Assert.Equal(0, streakService.GetStreak(new RoutingTarget("Server")));
        Assert.Equal(0, streakService.GetStreak(new RoutingTarget("Local")));
        Assert.Equal(0, streakService.GetStreak(RoutingTarget.Cloud));

        // Idempotent: the second call reports resetCount 0 and per-target reset:false.
        using (var second = await harness.PostAsync("/api/router/fleet/reset-streaks", harness.Key))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var secondBody = (JsonObject)JsonNode.Parse(await second.Content.ReadAsStringAsync())!;
            Assert.True(secondBody["success"]!.GetValue<bool>());
            Assert.Equal(0, secondBody["resetCount"]!.GetValue<int>());
            foreach (var row in (JsonArray)secondBody["targets"]!)
            {
                Assert.False(((JsonObject)row!)["reset"]!.GetValue<bool>());
                Assert.Equal(0, ((JsonObject)row!)["previousStreak"]!.GetValue<int>());
            }
        }
    }

    [Fact]
    public async Task ResetFleetStreaks_CleanFleet_NoCloudEnabled_ReportsZeroResets_AndOmitsTheCloudRow()
    {
        await using var lease = await SharedHosts.AcquireAsync(HostKind.CloudOff);
        var harness = lease.Harness;
        CleanStreaks(harness, includeCloud: false);

        using var response = await harness.PostAsync("/api/router/fleet/reset-streaks", harness.Key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.True(body["success"]!.GetValue<bool>());
        Assert.Equal(0, body["resetCount"]!.GetValue<int>());
        var targets = (JsonArray)body["targets"]!;
        // Exactly the effective targets (configuration order); Cloud disabled → no row.
        Assert.Equal(2, targets.Count);
        Assert.Equal("Server", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
        Assert.Equal(0, ((JsonObject)targets[0]!)["previousStreak"]!.GetValue<int>());
        Assert.False(((JsonObject)targets[0]!)["reset"]!.GetValue<bool>());
        Assert.Equal("Local", ((JsonObject)targets[1]!)["target"]!.GetValue<string>());
        Assert.False(((JsonObject)targets[1]!)["reset"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ResetFleetStreaks_NoStreakService_Returns500_WithoutTargets_AndKeylessIs401()
    {
        // The degraded host: 500 with the no-targets error shape...
        await using var noService = await SharedHosts.AcquireAsync(HostKind.NoService);
        using (var serverError = await noService.Harness.PostAsync("/api/router/fleet/reset-streaks", noService.Harness.Key))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, serverError.StatusCode);
            var body = (JsonObject)JsonNode.Parse(await serverError.Content.ReadAsStringAsync())!;

            Assert.False(body["success"]!.GetValue<bool>());
            Assert.Equal("failure-streak tracking is not available in this host", body["error"]!.GetValue<string>());
            Assert.Null(body["targets"]);
        }

        // ...and the default-mode keyless 401 on the fleet endpoint (the single-target 401
        // is covered by ResetStreak_WithoutKey_...).
        await using var withService = await SharedHosts.AcquireAsync(HostKind.CloudOff);
        using (var fleetUnauthorized = await withService.Harness.PostAsync("/api/router/fleet/reset-streaks"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, fleetUnauthorized.StatusCode);
            Assert.Equal("Bearer", fleetUnauthorized.Headers.WwwAuthenticate?.FirstOrDefault()?.Scheme);
        }
    }
}

/// <summary>
/// Task #110 (D1): the Setup-API target mutations (<c>POST/PUT/DELETE /api/router/targets[/{name}]</c>)
/// ride the single write path <c>IRouterConfigPersistence.PersistTargetsAsync</c>, so the OS-keyring
/// synchronization (store / clear / delete of the credential) is asserted here against a host that
/// registers an explicit <see cref="IRouterConfigPersistence"/> (plain protector) + a fake
/// <see cref="ICredentialStore"/> — NO auth middleware (handler-level test), NO real keyring.
/// </summary>
public class RouterTargetsKeyringEndpointsTests
{
    private sealed class KeyringHarness : IAsyncDisposable
    {
        public KeyringHarness(
            WebApplication app,
            HttpClient client,
            TestOptionsMonitor<OllamaRouterOptions> options,
            FakeCredentialStore store,
            string contentRoot)
        {
            App = app;
            Client = client;
            Options = options;
            Store = store;
            ContentRoot = contentRoot;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public TestOptionsMonitor<OllamaRouterOptions> Options { get; }

        public FakeCredentialStore Store { get; }

        public string ContentRoot { get; }

        public string ConfigPath => Path.Combine(ContentRoot, "config", "config.json");

        public Task<HttpResponseMessage> PutAsync(string path, string body) => Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        public Task<HttpResponseMessage> PostAsync(string path, string body) => Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        public Task<HttpResponseMessage> DeleteAsync(string path) =>
            Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, path));

        public async ValueTask DisposeAsync()
        {
            try
            {
                await App.StopAsync();
                await App.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(ContentRoot))
                    {
                        Directory.Delete(ContentRoot, recursive: true);
                    }
                }
                catch
                {
                    // Temp cleanup must never mask the test outcome.
                }
            }
        }
    }

    /// <summary>
    /// Minimal host (mirrors <see cref="RouterConfigEndpointsTests"/>): temp content root +
    /// <see cref="TestOptionsMonitor{TOptions}"/> + explicit <see cref="IRouterConfigPersistence"/>
    /// (PlainKeyProtector + the fake store) + <c>MapRouterTargetsEndpoints</c> only — no auth
    /// middleware, no liveness/catalog services (OpenAI targets use the synthetic catalog;
    /// <c>testConnectionFirst: false</c> skips the probe).
    /// </summary>
    private static async Task<KeyringHarness> CreateKeyringHarnessAsync(
        FakeCredentialStore store,
        OllamaRouterOptions? initialOptions = null,
        string? seedConfigJson = null)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"ollamarouter-keyring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);
        if (seedConfigJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(contentRoot, "config"));
            await File.WriteAllTextAsync(Path.Combine(contentRoot, "config", "config.json"), seedConfigJson);
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = contentRoot
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var optionsMonitor = new TestOptionsMonitor<OllamaRouterOptions>(initialOptions ?? new OllamaRouterOptions());
        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(optionsMonitor);
        builder.Services.AddSingleton<ICredentialStore>(store);
        builder.Services.AddSingleton<IRouterConfigPersistence>(sp => new RouterConfigPersistence(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp,
            null,
            new PlainKeyProtector(),
            store));

        var app = builder.Build();
        app.MapRouterTargetsEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(15) };
        return new KeyringHarness(app, client, optionsMonitor, store, contentRoot);
    }

    private static JsonArray ReadTargetsNode(string configPath)
    {
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(configPath))!;
        return (JsonArray)root["OllamaRouter"]!["Targets"]!;
    }

    private static OllamaTargetOptions GroqOptions(string? apiKey) => new()
    {
        Name = "groq",
        Url = "https://api.groq.com/openai/v1",
        Type = TargetType.OpenAI,
        Provider = "groq",
        ApiKey = apiKey
    };

    [Fact]
    public async Task DeleteTarget_RemovesCredentialFromKeyring()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        store.Seed(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "groq", "sk-groq");
        // config.json already carries the target with its keyring marker (the on-disk
        // existingByName source of the delete plan) and the in-memory options hold it live.
        await using var harness = await CreateKeyringHarnessAsync(
            store,
            new OllamaRouterOptions { Targets = [GroqOptions("sk-groq")] },
            seedConfigJson: """
            {
              "OllamaRouter": {
                "Targets": [
                  { "Name": "groq", "Url": "https://api.groq.com/openai/v1", "Type": "OpenAI", "ApiKey": "keyring:groq" }
                ]
              }
            }
            """);

        using var response = await harness.DeleteAsync("/api/router/targets/groq");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The credential was removed from the (fake) OS keyring...
        Assert.Contains(store.DeleteCalls, c => c is (ICredentialStore.PackageId, ICredentialStore.OpenAiService, "groq"));
        Assert.Null(store.GetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "groq"));

        // ...and the target is gone from config.json.
        Assert.Empty(ReadTargetsNode(harness.ConfigPath).AsEnumerable());
    }

    [Fact]
    public async Task UpdateTarget_ClearsKey_DeletesCredential()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateKeyringHarnessAsync(
            store,
            new OllamaRouterOptions { Targets = [GroqOptions("sk-old")] });

        using var response = await harness.PutAsync("/api/router/targets/groq", "{\"apiKey\": \"\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The cleared key removes the file node AND deletes the keyring entry.
        Assert.Single(store.DeleteCalls);
        Assert.Equal((ICredentialStore.PackageId, ICredentialStore.OpenAiService, "groq"), store.DeleteCalls[0]);
        var targetNode = (JsonObject)ReadTargetsNode(harness.ConfigPath)[0]!;
        Assert.Null(targetNode["ApiKey"]);
    }

    [Fact]
    public async Task UpdateTarget_SetsKey_WritesKeyringAndMarker()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateKeyringHarnessAsync(
            store,
            new OllamaRouterOptions { Targets = [GroqOptions(null)] });

        using var response = await harness.PutAsync("/api/router/targets/groq", "{\"apiKey\": \"sk-new\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The plaintext went to the (fake) keyring; the file holds ONLY the marker.
        Assert.Single(store.SetCalls);
        Assert.Equal((ICredentialStore.PackageId, ICredentialStore.OpenAiService, "groq", "sk-new"), store.SetCalls[0]);
        var fileContent = File.ReadAllText(harness.ConfigPath);
        var targetNode = (JsonObject)ReadTargetsNode(harness.ConfigPath)[0]!;
        Assert.Equal("keyring:groq", targetNode["ApiKey"]!.GetValue<string>());
        Assert.DoesNotContain("sk-new", fileContent);
    }

    [Fact]
    public async Task AddTarget_OpenAiWithKey_StoresCredential()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateKeyringHarnessAsync(store, new OllamaRouterOptions());

        using var response = await harness.PostAsync("/api/router/targets", """
            {
              "name": "groq",
              "url": "https://api.groq.com/openai/v1",
              "type": "openai",
              "apiKey": "sk-new",
              "maxParallel": 2,
              "testConnectionFirst": false
            }
            """);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The credential was stored in the (fake) keyring and the file keeps only the marker.
        Assert.Contains(store.SetCalls, c => c is (ICredentialStore.PackageId, ICredentialStore.OpenAiService, "groq", "sk-new"));
        var fileContent = File.ReadAllText(harness.ConfigPath);
        var targetNode = (JsonObject)ReadTargetsNode(harness.ConfigPath)[0]!;
        Assert.Equal("groq", targetNode["Name"]!.GetValue<string>());
        Assert.Equal("keyring:groq", targetNode["ApiKey"]!.GetValue<string>());
        Assert.DoesNotContain("sk-new", fileContent);
    }

    // ------------------------------------------------------------------
    //  Task #143: the sshUser / sshPort wire fields (add / update / persist / validate)
    // ------------------------------------------------------------------

    private static OllamaTargetOptions SshProbeableTarget() => new()
    {
        Name = "gpu",
        Url = "http://192.168.1.50:11434",
        SshUser = "ops",
        SshPort = 2222
    };

    [Fact]
    public async Task AddTarget_WithSshCredentials_PersistsBoth_AndEchoesThem()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateKeyringHarnessAsync(store);

        using var response = await harness.PostAsync("/api/router/targets", """
            {
              "name": "gpu",
              "url": "http://192.168.1.50:11434",
              "testConnectionFirst": false,
              "sshUser": "ops",
              "sshPort": 2222
            }
            """);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The created-target echo carries the ssh fields...
        var echoed = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ops", echoed!["target"]!["sshUser"]!.GetValue<string>());
        Assert.Equal(2222, echoed["target"]!["sshPort"]!.GetValue<int>());

        // ...and config.json round-trips them.
        var targetNode = (JsonObject)ReadTargetsNode(harness.ConfigPath)[0]!;
        Assert.Equal("ops", targetNode["SshUser"]!.GetValue<string>());
        Assert.Equal(2222, targetNode["SshPort"]!.GetValue<int>());
    }

    [Fact]
    public async Task UpdateTarget_SshCredentials_RoundTripThroughConfigJson()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateKeyringHarnessAsync(
            store, new OllamaRouterOptions { Targets = [SshProbeableTarget()] });

        using var response = await harness.PutAsync("/api/router/targets/gpu", """{ "sshUser": "root", "sshPort": 2223 }""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("root", echoed!["target"]!["sshUser"]!.GetValue<string>());
        Assert.Equal(2223, echoed["target"]!["sshPort"]!.GetValue<int>());

        var targetNode = (JsonObject)ReadTargetsNode(harness.ConfigPath)[0]!;
        Assert.Equal("root", targetNode["SshUser"]!.GetValue<string>());
        Assert.Equal(2223, targetNode["SshPort"]!.GetValue<int>());

        // The boundary values are valid.
        using var edge = await harness.PutAsync("/api/router/targets/gpu", """{ "sshPort": 65535 }""");
        Assert.Equal(HttpStatusCode.OK, edge.StatusCode);
        var edgeNode = (JsonObject)ReadTargetsNode(harness.ConfigPath)[0]!;
        Assert.Equal(65535, edgeNode["SshPort"]!.GetValue<int>());
    }

    [Fact]
    public async Task UpdateTarget_SshUserAbsentKeepsValue_EmptyStringClearsIt()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateKeyringHarnessAsync(
            store, new OllamaRouterOptions { Targets = [SshProbeableTarget()] });

        // null/absent = NO CHANGE (the user survives an unrelated update; the port too).
        using var untouched = await harness.PutAsync("/api/router/targets/gpu", """{ "maxParallel": 2 }""");
        Assert.Equal(HttpStatusCode.OK, untouched.StatusCode);
        var keptNode = (JsonObject)ReadTargetsNode(harness.ConfigPath)[0]!;
        Assert.Equal("ops", keptNode["SshUser"]!.GetValue<string>());
        Assert.Equal(2222, keptNode["SshPort"]!.GetValue<int>());

        // "" CLEARS the user (the key never lingers on disk; the port is untouched —
        // clearing a set port is a config.json edit, the documented powerWatts precedent).
        using var cleared = await harness.PutAsync("/api/router/targets/gpu", """{ "sshUser": "" }""");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var clearedNode = (JsonObject)ReadTargetsNode(harness.ConfigPath)[0]!;
        Assert.Null(clearedNode["SshUser"]);
        Assert.Equal(2222, clearedNode["SshPort"]!.GetValue<int>());
        Assert.Null(JsonNode.Parse(await cleared.Content.ReadAsStringAsync())!["target"]!["sshUser"]);
    }

    [Theory]
    [InlineData("""{ "sshUser": "bad user" }""")] // space
    [InlineData("""{ "sshUser": "bad@user" }""")] // '@' would break the {user}@{host} interpolation
    [InlineData("""{ "sshUser": "bad:user" }""")] // ':' is not a username character
    [InlineData("""{ "sshUser": "user\\name" }""")] // path separator
    [InlineData("""{ "sshUser": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" }""")] // 65 chars > 64
    public async Task UpdateTarget_InvalidSshUser_Returns400_WithoutPersisting(string body)
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateKeyringHarnessAsync(
            store, new OllamaRouterOptions { Targets = [SshProbeableTarget()] });

        using var response = await harness.PutAsync("/api/router/targets/gpu", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("sshUser", node!["message"]!.ToString(), StringComparison.Ordinal);

        // Validation is BEFORE the persist: nothing was written (no file was even created
        // and the live in-memory options keep the stored user).
        Assert.Equal("ops", harness.Options.CurrentValue.Targets.Single(t => t.Name == "gpu").SshUser);
    }

    [Theory]
    [InlineData("""{ "sshPort": 0 }""")]
    [InlineData("""{ "sshPort": 65536 }""")]
    [InlineData("""{ "sshPort": -1 }""")]
    public async Task UpdateTarget_InvalidSshPort_Returns400_WithoutPersisting(string body)
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateKeyringHarnessAsync(
            store, new OllamaRouterOptions { Targets = [SshProbeableTarget()] });

        using var response = await harness.PutAsync("/api/router/targets/gpu", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("sshPort", node!["message"]!.ToString(), StringComparison.Ordinal);

        // Validation is BEFORE the persist: the live in-memory options keep the stored port.
        Assert.Equal(2222, harness.Options.CurrentValue.Targets.Single(t => t.Name == "gpu").SshPort);
    }

    [Fact]
    public async Task AddTarget_InvalidSshCredentials_Return400_NoTargetCreated()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateKeyringHarnessAsync(store);

        using var badUser = await harness.PostAsync("/api/router/targets", """
            { "name": "gpu", "url": "http://192.168.1.50:11434", "testConnectionFirst": false, "sshUser": "bad user" }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, badUser.StatusCode);

        using var badPort = await harness.PostAsync("/api/router/targets", """
            { "name": "gpu", "url": "http://192.168.1.50:11434", "testConnectionFirst": false, "sshPort": 70000 }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, badPort.StatusCode);
        var node = JsonNode.Parse(await badPort.Content.ReadAsStringAsync());
        Assert.Contains("sshPort", node!["error"]!.ToString(), StringComparison.Ordinal);

        // Neither attempt persisted: the live in-memory options remain empty (validation is
        // BEFORE the target lookup/persist, so no file was written either).
        Assert.Empty(harness.Options.CurrentValue.GetEffectiveTargets());
    }
}
