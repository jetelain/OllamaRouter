using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Configuration;
using OllamaRouter.Endpoints;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Security;
using OllamaRouter.Serialization;
using OllamaRouter.Tests.Mesh;
using OllamaRouter.Tests.TestHelpers;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Minimal-harness tests for the dynamic fleet configuration / Setup API (task #19):
/// <c>GET /api/router/config</c>, <c>PUT /api/router/config</c>, <c>POST/PUT/DELETE
/// /api/router/targets</c>. The host runs ONLY <c>MapOllamaRouterApiEndpoints</c> with a
/// <see cref="TestOptionsMonitor{TOptions}"/> (so the in-memory application is asserted on the
/// shared instance), Moq'd liveness / catalog services and an explicit
/// <see cref="IRouterConfigPersistence"/> registration pointed at a fresh temp content root
/// (file assertions). The full-pipeline provisioning tests live in
/// <see cref="RouterConfigProvisioningPipelineTests"/>.
/// </summary>
public class RouterConfigEndpointsTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(WebApplication app, HttpClient client, TestOptionsMonitor<OllamaRouterOptions> options, string contentRoot)
        {
            App = app;
            Client = client;
            Options = options;
            ContentRoot = contentRoot;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public TestOptionsMonitor<OllamaRouterOptions> Options { get; }

        public string ContentRoot { get; }

        public string ConfigPath => Path.Combine(ContentRoot, "config", "config.json");

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

    private static async Task<Harness> CreateTestAppAsync(
        OllamaRouterOptions options,
        Mock<ITargetLivenessService>? liveness = null,
        Mock<IModelCatalogCacheService>? catalog = null)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"ollamarouter-setupapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = contentRoot
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var optionsMonitor = new TestOptionsMonitor<OllamaRouterOptions>(options);
        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(optionsMonitor);
        if (liveness is not null)
        {
            builder.Services.AddSingleton(liveness.Object);
        }
        if (catalog is not null)
        {
            builder.Services.AddSingleton(catalog.Object);
        }
        builder.Services.AddSingleton<IRouterConfigPersistence>(sp => new RouterConfigPersistence(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp,
            null));

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(15) };
        return new Harness(app, client, optionsMonitor, contentRoot);
    }

    private static OllamaRouterOptions SingleTargetOptions() => new()
    {
        Targets =
        [
            new OllamaTargetOptions
            {
                Name = "Local",
                Url = "http://127.0.0.1:11435",
                MaxParallel = 1
            }
        ]
    };

    private static async Task<JsonNode?> PostJsonAsync(HttpClient client, string path, string body)
    {
        var response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        return await ParseBodyAsync(response);
    }

    private static async Task<JsonNode?> PutJsonAsync(HttpClient client, string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(request);
        return await ParseBodyAsync(response);
    }

    private static async Task<JsonNode?> ParseBodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.Dispose();
        Assert.True(text.Length > 0, "response body must not be empty");
        return JsonNode.Parse(text);
    }

    private static JsonObject ReadConfigFile(Harness harness)
    {
        Assert.True(File.Exists(harness.ConfigPath), $"config file must exist at {harness.ConfigPath}");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(harness.ConfigPath))!;
    }

    // ------------------------------------------------------------------
    //  GET /api/router/config
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetConfig_ReturnsFullShape()
    {
        var options = SingleTargetOptions();
        options.Targets.Add(new OllamaTargetOptions
        {
            Name = "Server",
            Url = "http://192.168.1.100:11434",
            Enabled = false,
            MaxParallel = 4,
            HardwareHint = "96gb"
        });
        ((OllamaTargetOptions)options.Targets[0]).HardwareHint = "16gb";
        options.CloudOverflow = new CloudOverflowOptions { Enabled = true, DefaultModel = "glm-4.6:cloud", PreferredRelayTarget = "Local" };
        options.DynamicContext = new DynamicContextOptions
        {
            Enabled = true,
            DenseClampEnabled = true,
            AutoShrinkCooldownTurns = 3,
            AutoShrinkIdleTimeoutSeconds = 60
        };
        options.Recording = new RecordingOptions { Enabled = true, OutputDirectory = "rec", MaxFiles = 500 };
        options.MaxConsecutiveFailures = 2;
        options.ConsecutiveFailuresTokenTolerance = 4000;
        options.Pricing = new PricingOptions { PromptPricePerMillion = 0.15, CompletionPricePerMillion = 0.5, Currency = "$" };

        await using var harness = await CreateTestAppAsync(options);

        var response = await harness.Client.GetAsync("/api/router/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        response.Dispose();

        // NO credential fields exist to mask: the response must not carry any key material.
        Assert.DoesNotContain("apiKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasApiKey", raw, StringComparison.OrdinalIgnoreCase);

        var json = (JsonNode.Parse(raw)! as JsonObject)!;
        var targets = (json["targets"]! as JsonArray)!;
        Assert.Equal(2, targets.Count);
        var first = (targets[0] as JsonObject)!;
        Assert.Equal("Local", first["name"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:11435", first["url"]!.GetValue<string>());
        Assert.True(first["enabled"]!.GetValue<bool>());
        Assert.Equal("16gb", first["hardwareHint"]!.GetValue<string>());
        Assert.Equal(1, first["maxParallel"]!.GetValue<int>());
        var second = (targets[1] as JsonObject)!;
        Assert.Equal("Server", second["name"]!.GetValue<string>());
        Assert.False(second["enabled"]!.GetValue<bool>());
        Assert.Equal("96gb", second["hardwareHint"]!.GetValue<string>());
        Assert.Equal(4, second["maxParallel"]!.GetValue<int>());

        var cloud = (json["cloud"]! as JsonObject)!;
        Assert.True(cloud["enabled"]!.GetValue<bool>());
        Assert.Equal("glm-4.6:cloud", cloud["defaultModel"]!.GetValue<string>());
        Assert.Equal("Local", cloud["preferredRelayTarget"]!.GetValue<string>());
        Assert.True(cloud["overflowConfigured"]!.GetValue<bool>(), "overflowConfigured must be true (enabled + defaultModel)");
        // Exactly the four documented fields.
        Assert.Equal(4, cloud.Count);

        var dynamicContext = (json["dynamicContext"]! as JsonObject)!;
        Assert.True(dynamicContext["enabled"]!.GetValue<bool>());
        Assert.True(dynamicContext["denseClampEnabled"]!.GetValue<bool>());
        Assert.Equal(3, dynamicContext["autoShrinkCooldownTurns"]!.GetValue<int>());
        Assert.Equal(60, dynamicContext["autoShrinkIdleTimeoutSeconds"]!.GetValue<int>());

        var recording = (json["recording"]! as JsonObject)!;
        Assert.True(recording["enabled"]!.GetValue<bool>());
        Assert.Equal("rec", recording["outputDirectory"]!.GetValue<string>());
        Assert.Equal(500, recording["maxFiles"]!.GetValue<int>());

        var resilience = (json["resilience"]! as JsonObject)!;
        Assert.Equal(2, resilience["maxConsecutiveFailures"]!.GetValue<int>());
        Assert.Equal(4000, resilience["consecutiveFailuresTokenTolerance"]!.GetValue<int>());

        var pricing = (json["pricing"]! as JsonObject)!;
        Assert.Equal(0.15, pricing["promptPricePerMillion"]!.GetValue<double>());
        Assert.Equal(0.5, pricing["completionPricePerMillion"]!.GetValue<double>());
        Assert.Null(pricing["pricePerMillion"]); // JSON null (member present).
        Assert.Equal("$", pricing["currency"]!.GetValue<string>());

        // apiSecurity section (task #50): the EFFECTIVE settings only — no key material.
        var apiSecurity = (json["apiSecurity"]! as JsonObject)!;
        Assert.Equal(2, apiSecurity.Count);
        Assert.False(apiSecurity["requireKeyForAllEndpoints"]!.GetValue<bool>());
        Assert.Equal(24, apiSecurity["defaultKeyRotationHours"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetConfig_CloudNotConfigured()
    {
        var options = SingleTargetOptions();
        // CloudOverflow.Enabled defaults to true, but no DefaultModel and no per-model CloudModel.
        await using var harness = await CreateTestAppAsync(options);

        var response = await harness.Client.GetAsync("/api/router/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = (JsonNode.Parse(await response.Content.ReadAsStringAsync())! as JsonObject)!;
        response.Dispose();

        var cloud = (json["cloud"]! as JsonObject)!;
        Assert.True(cloud["enabled"]!.GetValue<bool>());
        Assert.Null(cloud["defaultModel"]); // JSON null (member present, not omitted).
        Assert.False(cloud["overflowConfigured"]!.GetValue<bool>(), "overflowConfigured must be false without a cloud model");

        // apiSecurity section (task #50) is present with the defaults even when cloud is unset.
        var apiSecurity = (json["apiSecurity"]! as JsonObject)!;
        Assert.False(apiSecurity["requireKeyForAllEndpoints"]!.GetValue<bool>());
        Assert.Equal(24, apiSecurity["defaultKeyRotationHours"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------
    //  POST /api/router/targets
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostTarget_Valid_201_PersistsAndUpdatesMemory()
    {
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(l => l.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
        {
            [new RoutingTarget("Laptop")] = new TargetLivenessEntry(
                new RoutingTarget("Laptop"), TargetLivenessState.Online, null, DateTimeOffset.UtcNow, 8)
        });
        var catalog = new Mock<IModelCatalogCacheService>();
        catalog.Setup(c => c.RefreshAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "model-a:latest", "model-b:latest", "model-c:latest" });

        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options, liveness, catalog);

        var json = (await PostJsonAsync(
            harness.Client,
            "/api/router/targets",
            """{"name":"Laptop","url":"http://192.168.1.105:11435/","hardwareHint":"8gb","maxParallel":1}""")) as JsonObject;
        Assert.NotNull(json);

        Assert.True(json!["success"]!.GetValue<bool>());
        var target = (json["target"]! as JsonObject)!;
        Assert.Equal("Laptop", target["name"]!.GetValue<string>());
        Assert.Equal("http://192.168.1.105:11435", target["url"]!.GetValue<string>());
        Assert.True(target["enabled"]!.GetValue<bool>());
        Assert.Equal("8gb", target["hardwareHint"]!.GetValue<string>());
        Assert.Equal(1, target["maxParallel"]!.GetValue<int>());
        Assert.True(json["connectionTested"]!.GetValue<bool>());
        Assert.Equal("Online", json["liveness"]!.GetValue<string>());
        Assert.Equal(8, json["latencyMs"]!.GetValue<long>());
        Assert.Equal(3, json["modelsDiscovered"]!.GetValue<int>());
        Assert.False(json.AsObject().ContainsKey("error"));

        // In-memory update: the options instance carries the new target.
        Assert.Equal(2, harness.Options.CurrentValue.Targets.Count);
        var added = Assert.IsType<OllamaTargetOptions>(harness.Options.CurrentValue.Targets[1]);
        Assert.Equal("Laptop", added.Name);
        Assert.Equal("http://192.168.1.105:11435", added.Url);
        Assert.Equal("8gb", added.HardwareHint);
        Assert.True(added.Enabled);

        // Disk update: config/config.json exists and carries the entry under OllamaRouter:Targets.
        var file = ReadConfigFile(harness);
        var targets = (file["OllamaRouter"]!["Targets"]! as JsonArray)!;
        Assert.Equal(2, targets.Count);
        var persisted = (targets[1] as JsonObject)!;
        Assert.Equal("Laptop", persisted["Name"]!.GetValue<string>());
        Assert.Equal("http://192.168.1.105:11435", persisted["Url"]!.GetValue<string>());
        Assert.Equal("8gb", persisted["HardwareHint"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostTarget_PreservesUnmanagedKeysOfExistingTargets()
    {
        var options = SingleTargetOptions();
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        var harness = await CreateTestAppAsync(options, liveness);
        try
        {
            // Seed the user config with an unmanaged per-target key (SpeedFactor).
            Directory.CreateDirectory(Path.GetDirectoryName(harness.ConfigPath)!);
            File.WriteAllText(harness.ConfigPath, """
                { "OllamaRouter": { "Targets": [
                    { "Name": "Local", "Url": "http://127.0.0.1:11435", "Enabled": true, "MaxParallel": 1, "SpeedFactor": 2.0 }
                ] } }
                """);

            var json = (await PostJsonAsync(
                harness.Client,
                "/api/router/targets",
                """{"name":"Laptop","url":"http://192.168.1.105:11435","hardwareHint":null,"maxParallel":1,"testConnectionFirst":false}""")) as JsonObject;
            Assert.NotNull(json);
            Assert.True(json!["success"]!.GetValue<bool>());

            var file = ReadConfigFile(harness);
            var targets = (file["OllamaRouter"]!["Targets"]! as JsonArray)!;
            Assert.Equal(2, targets.Count);
            var local = (targets[0] as JsonObject)!;
            Assert.Equal("Local", local["Name"]!.GetValue<string>());
            Assert.Equal(2.0, local["SpeedFactor"]!.GetValue<double>());
            var laptop = (targets[1] as JsonObject)!;
            Assert.Equal("Laptop", laptop["Name"]!.GetValue<string>());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("""{"name":"local","url":"http://192.168.1.105:11435"}""")]
    public async Task PostTarget_DuplicateName_409(string body)
    {
        var options = SingleTargetOptions(); // already has "Local"
        await using var harness = await CreateTestAppAsync(options);

        var response = await harness.Client.PostAsync("/api/router/targets",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = (JsonNode.Parse(await response.Content.ReadAsStringAsync())! as JsonObject)!;
        response.Dispose();
        Assert.False(json["success"]!.GetValue<bool>());
        Assert.Contains("already exists", json["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"name":"","url":"http://192.168.1.105:11435"}""")]
    [InlineData("""{"name":"cloud","url":"http://192.168.1.105:11435"}""")]
    [InlineData("""{"name":"bad/name","url":"http://192.168.1.105:11435"}""")]
    public async Task PostTarget_InvalidName_400(string body)
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var response = await harness.Client.PostAsync("/api/router/targets",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = (JsonNode.Parse(await response.Content.ReadAsStringAsync())! as JsonObject)!;
        response.Dispose();
        Assert.False(json["success"]!.GetValue<bool>());
        Assert.NotNull(json["error"]);
    }

    [Fact]
    public async Task PostTarget_InvalidUrl_400()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var response = await harness.Client.PostAsync("/api/router/targets",
            new StringContent("""{"name":"Laptop","url":"192.168.1.105:11435"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = (JsonNode.Parse(await response.Content.ReadAsStringAsync())! as JsonObject)!;
        response.Dispose();
        Assert.False(json["success"]!.GetValue<bool>());
        Assert.Contains("url", json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostTarget_MaxParallelBelowOne_400()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var response = await harness.Client.PostAsync("/api/router/targets",
            new StringContent("""{"name":"Laptop","url":"http://192.168.1.105:11435","maxParallel":0}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = (JsonNode.Parse(await response.Content.ReadAsStringAsync())! as JsonObject)!;
        response.Dispose();
        Assert.False(json["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PostTarget_ProbeFails_422_NotPersisted()
    {
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Offline);
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options, liveness);

        var response = await harness.Client.PostAsync("/api/router/targets",
            new StringContent("""{"name":"Laptop","url":"http://192.168.1.105:11435"}""", Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var json = (JsonNode.Parse(await response.Content.ReadAsStringAsync())! as JsonObject)!;
        response.Dispose();
        Assert.False(json["success"]!.GetValue<bool>());
        Assert.Equal("Offline", json["liveness"]!.GetValue<string>());
        Assert.Contains("testConnectionFirst: false", json["error"]!.GetValue<string>());
        Assert.Null(json["target"]);

        // Nothing persisted, nothing applied.
        Assert.Single(harness.Options.CurrentValue.Targets);
        Assert.False(File.Exists(harness.ConfigPath));
    }

    [Fact]
    public async Task PostTarget_TestConnectionFirstFalse_SkipsProbe()
    {
        var options = SingleTargetOptions();
        // NO liveness / catalog services registered: the probe must be skipped, not fatal.
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PostJsonAsync(
            harness.Client,
            "/api/router/targets",
            """{"name":"Laptop","url":"http://192.168.1.105:11435","testConnectionFirst":false}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.True(json!["success"]!.GetValue<bool>());
        Assert.False(json["connectionTested"]!.GetValue<bool>());
        Assert.Equal("Unknown", json["liveness"]!.GetValue<string>());
        Assert.Equal(0, json["modelsDiscovered"]!.GetValue<int>());
        Assert.Equal(2, harness.Options.CurrentValue.Targets.Count);
    }

    // ------------------------------------------------------------------
    //  PUT /api/router/targets/{name}
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutTarget_UpdatesProperties_200()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(
            harness.Client,
            "/api/router/targets/Local",
            """{"url":"http://127.0.0.1:12435","enabled":false,"hardwareHint":"16gb","maxParallel":2}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.True(json!["success"]!.GetValue<bool>());
        var target = (json["target"]! as JsonObject)!;
        Assert.Equal("Local", target["name"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:12435", target["url"]!.GetValue<string>());
        Assert.False(target["enabled"]!.GetValue<bool>());
        Assert.Equal("16gb", target["hardwareHint"]!.GetValue<string>());
        Assert.Equal(2, target["maxParallel"]!.GetValue<int>());

        var current = (OllamaTargetOptions)harness.Options.CurrentValue.Targets[0];
        Assert.Equal("http://127.0.0.1:12435", current.Url);
        Assert.False(current.Enabled);
        Assert.Equal("16gb", current.HardwareHint);
        Assert.Equal(2, current.MaxParallel);

        var file = ReadConfigFile(harness);
        var persisted = ((file["OllamaRouter"]!["Targets"]! as JsonArray)![0] as JsonObject)!;
        Assert.Equal("http://127.0.0.1:12435", persisted["Url"]!.GetValue<string>());
        Assert.False(persisted["Enabled"]!.GetValue<bool>());
        Assert.Equal(2, persisted["MaxParallel"]!.GetValue<int>());
    }

    [Fact]
    public async Task PutTarget_ClearHardwareHintWithEmptyString()
    {
        var options = SingleTargetOptions();
        ((OllamaTargetOptions)options.Targets[0]).HardwareHint = "8gb";
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(
            harness.Client,
            "/api/router/targets/Local",
            """{"hardwareHint":""}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.True(json!["success"]!.GetValue<bool>());
        Assert.Null(((OllamaTargetOptions)harness.Options.CurrentValue.Targets[0]).HardwareHint);

        // The persisted target carries the cleared hint as JSON null (binds back to null on a
        // restart — the hint is really cleared, not just hidden in memory).
        var file = ReadConfigFile(harness);
        var persisted = ((file["OllamaRouter"]!["Targets"]! as JsonArray)![0] as JsonObject)!;
        Assert.Null(persisted["HardwareHint"]);
    }

    [Fact]
    public async Task PutTarget_UnknownName_404()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(harness.Client, "/api/router/targets/Ghost", """{"enabled":false}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.False(json!["success"]!.GetValue<bool>());
        Assert.Contains("not found", json["message"]!.GetValue<string>());
        Assert.Single(harness.Options.CurrentValue.Targets);
    }

    [Fact]
    public async Task PutTarget_EmptyBody_400()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(harness.Client, "/api/router/targets/Local", "{}")) as JsonObject;
        Assert.NotNull(json);
        Assert.False(json!["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PutTarget_InvalidUrl_400()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(harness.Client, "/api/router/targets/Local", """{"url":"not a url"}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.False(json!["success"]!.GetValue<bool>());
        Assert.Contains("url", json["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    //  DELETE /api/router/targets/{name}
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteTarget_RemovesFromMemoryAndDisk_200()
    {
        var options = SingleTargetOptions();
        options.Targets.Add(new OllamaTargetOptions { Name = "Laptop", Url = "http://192.168.1.105:11435", MaxParallel = 1 });
        await using var harness = await CreateTestAppAsync(options);

        var json = (await ParseBodyAsync(await harness.Client.DeleteAsync("/api/router/targets/Laptop"))) as JsonObject;
        Assert.NotNull(json);
        Assert.True(json!["success"]!.GetValue<bool>());

        Assert.Single(harness.Options.CurrentValue.Targets);
        Assert.Equal("Local", harness.Options.CurrentValue.Targets[0].Name);

        var file = ReadConfigFile(harness);
        var targets = (file["OllamaRouter"]!["Targets"]! as JsonArray)!;
        Assert.Single(targets);
        Assert.Equal("Local", (targets[0]!.AsObject())["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteTarget_UnknownName_404()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var json = (await ParseBodyAsync(await harness.Client.DeleteAsync("/api/router/targets/Ghost"))) as JsonObject;
        Assert.NotNull(json);
        Assert.False(json!["success"]!.GetValue<bool>());
        Assert.Contains("not found", json["message"]!.GetValue<string>());
        Assert.Single(harness.Options.CurrentValue.Targets);
    }

    // ------------------------------------------------------------------
    //  PUT /api/router/config
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutConfig_FullUpdate_PersistsAndApplies()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(
            harness.Client,
            "/api/router/config",
            """
            {
              "cloud": { "enabled": false, "defaultModel": "glm-4.6:cloud", "preferredRelayTarget": "Server" },
              "dynamicContext": { "enabled": true, "denseClampEnabled": false, "autoShrinkCooldownTurns": 5, "autoShrinkIdleTimeoutSeconds": 120 },
              "recording": { "enabled": true, "outputDirectory": "rec-dir", "maxFiles": 25 },
              "resilience": { "maxConsecutiveFailures": 7, "consecutiveFailuresTokenTolerance": 9000 },
              "pricing": { "promptPricePerMillion": 0.2, "completionPricePerMillion": 0.8, "currency": "€" }
            }
            """)) as JsonObject;
        Assert.NotNull(json);
        Assert.True(json!["success"]!.GetValue<bool>());
        var echoed = (json["config"]! as JsonObject)!;
        Assert.Equal(7, ((echoed["resilience"]! as JsonObject)!)["maxConsecutiveFailures"]!.GetValue<int>());
        Assert.Equal("€", ((echoed["pricing"]! as JsonObject)!)["currency"]!.GetValue<string>());

        // Live in-memory application (no restart).
        var current = harness.Options.CurrentValue;
        Assert.False(current.CloudOverflow.Enabled);
        Assert.Equal("glm-4.6:cloud", current.CloudOverflow.DefaultModel);
        Assert.Equal("Server", current.CloudOverflow.PreferredRelayTarget);
        Assert.True(current.DynamicContext.Enabled);
        Assert.False(current.DynamicContext.DenseClampEnabled);
        Assert.Equal(5, current.DynamicContext.AutoShrinkCooldownTurns);
        Assert.Equal(120, current.DynamicContext.AutoShrinkIdleTimeoutSeconds);
        Assert.True(current.Recording!.Enabled);
        Assert.Equal("rec-dir", current.Recording.OutputDirectory);
        Assert.Equal(25, current.Recording.MaxFiles);
        Assert.Equal(7, current.MaxConsecutiveFailures);
        Assert.Equal(9000, current.ConsecutiveFailuresTokenTolerance);
        Assert.Equal("€", current.Pricing!.Currency);
        Assert.Equal(0.2, current.Pricing.PromptPricePerMillion);

        // Disk: only the provided sections were written, in the documented PascalCase shape.
        var file = ReadConfigFile(harness);
        var router = (JsonObject)file["OllamaRouter"]!;
        Assert.False(((JsonObject)router["CloudOverflow"]!)["Enabled"]!.GetValue<bool>());
        Assert.Equal("Server", ((JsonObject)router["CloudOverflow"]!)["PreferredRelayTarget"]!.GetValue<string>());
        Assert.True(((JsonObject)router["DynamicContext"]!)["Enabled"]!.GetValue<bool>());
        Assert.Equal(5, ((JsonObject)router["DynamicContext"]!)["AutoShrinkCooldownTurns"]!.GetValue<int>());
        Assert.Equal(25, ((JsonObject)router["Recording"]!)["MaxFiles"]!.GetValue<int>());
        Assert.Equal(7, router["MaxConsecutiveFailures"]!.GetValue<int>());
        Assert.Equal(9000, router["ConsecutiveFailuresTokenTolerance"]!.GetValue<int>());
        Assert.Equal("€", ((JsonObject)router["Pricing"]!)["Currency"]!.GetValue<string>());
        Assert.Equal(0.2, ((JsonObject)router["Pricing"]!)["PromptPricePerMillion"]!.GetValue<double>());
        // Target membership was NOT part of this update and no targets were seeded.
        Assert.False(router.AsObject().ContainsKey("Targets"));
    }

    [Fact]
    public async Task PutConfig_PartialUpdate_TouchesOnlyProvidedSections()
    {
        var options = SingleTargetOptions();
        options.Pricing = new PricingOptions { PromptPricePerMillion = 0.1, CompletionPricePerMillion = 0.2, Currency = "$" };
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(
            harness.Client,
            "/api/router/config",
            """{"resilience": { "maxConsecutiveFailures": 5, "consecutiveFailuresTokenTolerance": 1000 }}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.True(json!["success"]!.GetValue<bool>());

        // Applied section changed; untouched section kept.
        Assert.Equal(5, harness.Options.CurrentValue.MaxConsecutiveFailures);
        Assert.Equal(1000, harness.Options.CurrentValue.ConsecutiveFailuresTokenTolerance);
        var pricing = harness.Options.CurrentValue.Pricing!;
        Assert.Equal(0.1, pricing.PromptPricePerMillion);
        Assert.Equal("$", pricing.Currency);

        // Disk: no pricing section written.
        var file = ReadConfigFile(harness);
        var router = (JsonObject)file["OllamaRouter"]!;
        Assert.False(router.AsObject().ContainsKey("Pricing"));
        Assert.Equal(5, router["MaxConsecutiveFailures"]!.GetValue<int>());
    }

    [Fact]
    public async Task PutConfig_Recording_PreservesTokenMetricsCsv()
    {
        var options = SingleTargetOptions();
        options.Recording = new RecordingOptions
        {
            Enabled = false,
            MaxFiles = 500,
            TokenMetricsCsv = new TokenMetricsCsvOptions { Enabled = true, MaxFiles = 77 }
        };
        await using var harness = await CreateTestAppAsync(options);
        // Seed the file with an on-disk TokenMetricsCsv section (the preservation target).
        Directory.CreateDirectory(Path.GetDirectoryName(harness.ConfigPath)!);
        File.WriteAllText(harness.ConfigPath, """
            { "OllamaRouter": { "Recording": { "TokenMetricsCsv": { "Enabled": true, "MaxFiles": 77 } } } }
            """);

        var json = (await PutJsonAsync(
            harness.Client,
            "/api/router/config",
            """{"recording": { "enabled": true, "outputDirectory": "rec", "maxFiles": 10 }}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.True(json!["success"]!.GetValue<bool>());

        // In memory: the CSV sub-config survived.
        var recording = harness.Options.CurrentValue.Recording!;
        Assert.True(recording.Enabled);
        Assert.Equal(10, recording.MaxFiles);
        var csv = recording.TokenMetricsCsv;
        Assert.NotNull(csv);
        Assert.True(csv.Enabled, "TokenMetricsCsv must be preserved in memory");
        Assert.Equal(77, csv.MaxFiles);

        // On disk: the CSV section was neither clobbered nor dropped.
        var file = ReadConfigFile(harness);
        var fileRecording = (JsonObject)file["OllamaRouter"]!["Recording"]!;
        Assert.True(fileRecording["Enabled"]!.GetValue<bool>());
        Assert.Equal(10, fileRecording["MaxFiles"]!.GetValue<int>());
        var fileCsv = (JsonObject)fileRecording["TokenMetricsCsv"]!;
        Assert.True(fileCsv["Enabled"]!.GetValue<bool>());
        Assert.Equal(77, fileCsv["MaxFiles"]!.GetValue<int>());
    }

    [Fact]
    public async Task PutConfig_CloudEmptyStrings_ClearToDefaults()
    {
        // Regression (task #19, spec §1): cloud.defaultModel / preferredRelayTarget "" CLEARS —
        // defaultModel → null, preferredRelayTarget → "Local" — in memory AND in the persisted
        // file (the file must carry the canonical forms, so a restart cannot drift the values).
        var options = SingleTargetOptions();
        options.CloudOverflow = new CloudOverflowOptions
        {
            Enabled = true,
            DefaultModel = "old-model:cloud",
            PreferredRelayTarget = "Server"
        };
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(
            harness.Client,
            "/api/router/config",
            """{"cloud": { "enabled": true, "defaultModel": "", "preferredRelayTarget": "" }}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.True(json!["success"]!.GetValue<bool>());

        // Live apply: "" clears.
        var current = harness.Options.CurrentValue;
        Assert.True(current.CloudOverflow.Enabled);
        Assert.Null(current.CloudOverflow.DefaultModel);
        Assert.Equal("Local", current.CloudOverflow.PreferredRelayTarget);

        // Echo: the response config reports the cleared values.
        var echoedCloud = (json["config"]! as JsonObject)!["cloud"]! as JsonObject;
        Assert.Null(echoedCloud!["defaultModel"]);
        Assert.Equal("Local", echoedCloud["preferredRelayTarget"]!.GetValue<string>());

        // Disk: the canonical cleared forms (JSON null / "Local").
        var file = ReadConfigFile(harness);
        var cloudNode = (JsonObject)file["OllamaRouter"]!["CloudOverflow"]!;
        Assert.True(cloudNode.ContainsKey("DefaultModel"));
        Assert.Null(cloudNode["DefaultModel"]);
        Assert.Equal("Local", cloudNode["PreferredRelayTarget"]!.GetValue<string>());
    }

    [Fact]
    public async Task PutConfig_EmptyBody_400()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(harness.Client, "/api/router/config", "{}")) as JsonObject;
        Assert.NotNull(json);
        Assert.False(json!["success"]!.GetValue<bool>());
        Assert.Contains("no configuration sections", json["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"pricing": { "promptPricePerMillion": -0.1, "currency": "$" }}""")]
    [InlineData("""{"recording": { "enabled": true, "maxFiles": -5 }}""")]
    [InlineData("""{"resilience": { "maxConsecutiveFailures": -1, "consecutiveFailuresTokenTolerance": 4000 }}""")]
    [InlineData("""{"pricing": { "promptPricePerMillion": 0.1, "currency": "" }}""")]
    public async Task PutConfig_NegativeValues_400(string body)
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        var json = (await PutJsonAsync(harness.Client, "/api/router/config", body)) as JsonObject;
        Assert.NotNull(json);
        Assert.False(json!["success"]!.GetValue<bool>());
        Assert.Null(json["config"]);
        // No memory change on validation failure.
        Assert.Equal(3, harness.Options.CurrentValue.MaxConsecutiveFailures);
    }

    [Fact]
    public async Task PutConfig_FileWriteFailure_500_NoMemoryChange()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        // Make the "config" path a FILE so Directory.CreateDirectory("config") fails.
        File.WriteAllText(Path.Combine(harness.ContentRoot, "config"), "not a directory");

        var json = (await PutJsonAsync(
            harness.Client,
            "/api/router/config",
            """{"resilience": { "maxConsecutiveFailures": 9, "consecutiveFailuresTokenTolerance": 4000 }}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.False(json!["success"]!.GetValue<bool>());
        Assert.NotNull(json["message"]);

        // Memory is untouched: a 500 never diverges memory and disk.
        Assert.Equal(3, harness.Options.CurrentValue.MaxConsecutiveFailures);
    }

    [Fact]
    public async Task Persist_CorruptExistingFile_500_NotClobbered()
    {
        var options = SingleTargetOptions();
        await using var harness = await CreateTestAppAsync(options);

        const string Corrupt = "this is { not valid json";
        Directory.CreateDirectory(Path.Combine(harness.ContentRoot, "config"));
        File.WriteAllText(harness.ConfigPath, Corrupt);

        var json = (await PutJsonAsync(
            harness.Client,
            "/api/router/config",
            """{"resilience": { "maxConsecutiveFailures": 9, "consecutiveFailuresTokenTolerance": 4000 }}""")) as JsonObject;
        Assert.NotNull(json);
        Assert.False(json!["success"]!.GetValue<bool>());

        Assert.Equal(Corrupt, File.ReadAllText(harness.ConfigPath));
        Assert.Equal(3, harness.Options.CurrentValue.MaxConsecutiveFailures);
    }
}

/// <summary>
/// Full-pipeline provisioning tests (task #19): the PRODUCTION pipeline
/// (<c>AddOllamaRouter</c> + <c>UseOllamaRouterPipeline</c>) against in-process
/// <see cref="FakeOllamaDaemon"/> instances with a temp content root (so the Setup API writes
/// <c>config/config.json</c> into the sandbox, never the workspace). Proves the acceptance
/// criterion "a runtime-added target is immediately routable on the inference dispatch path
/// without a restart" and the restart-free config/target semantics end to end.
/// </summary>
public class RouterConfigProvisioningPipelineTests
{
    private const string Model = "tiny-3b:latest";

    private static readonly IReadOnlyList<DaemonModel> Models =
    [
        new DaemonModel
        {
            Name = Model,
            SizeBytes = 2L * 1024 * 1024 * 1024,
            ParameterCount = 3_000_000_000, // SmallDense: never VRAM-vetoed, capable on any node.
            ModelfileNumCtx = 16_384, // pre-configured context > prompt + 8192 margin.
            Family = "llama"
        }
    ];

    private sealed class PipelineHarness : IAsyncDisposable
    {
        public PipelineHarness(WebApplication app, HttpClient client, OllamaRouterOptions options, IServiceProvider services, string contentRoot)
        {
            App = app;
            Client = client;
            Options = options;
            Services = services;
            ContentRoot = contentRoot;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public OllamaRouterOptions Options { get; }

        public IServiceProvider Services { get; }

        public string ContentRoot { get; }

        public string ConfigPath => Path.Combine(ContentRoot, "config", "config.json");

        public ITargetLivenessService Liveness => Services.GetRequiredService<ITargetLivenessService>();

        /// <summary>
        /// The API key store (task #50): the harness resolves it from the production DI graph;
        /// the default key is re-read per AUTHENTICATED call (the documented re-read-after-401
        /// pattern — a rotation would otherwise invalidate a cached key mid-test).
        /// </summary>
        public IApiKeyStore Keys => Services.GetRequiredService<IApiKeyStore>();

        /// <summary>
        /// Sends a request with the current default key stamped as
        /// <c>Authorization: Bearer &lt;key&gt;</c> (task #50): required for the default
        /// config-affecting protected routes (POST/PUT/DELETE /api/router/targets, GET/PUT
        /// /api/router/config). Read routes and POST /targets stay open and need no key.
        /// </summary>
        public Task<HttpResponseMessage> SendAuthedAsync(HttpMethod method, string path, HttpContent? content = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Keys.GetDefaultKey());
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> PostAuthedAsync(string path, HttpContent content) =>
            SendAuthedAsync(HttpMethod.Post, path, content);

        public Task<HttpResponseMessage> PutAuthedAsync(string path, HttpContent content) =>
            SendAuthedAsync(HttpMethod.Put, path, content);

        public Task<HttpResponseMessage> DeleteAuthedAsync(string path) =>
            SendAuthedAsync(HttpMethod.Delete, path);

        public Task<HttpResponseMessage> GetAuthedAsync(string path) =>
            SendAuthedAsync(HttpMethod.Get, path);

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

        public static async Task<PipelineHarness> StartAsync(IReadOnlyList<OllamaTargetOptions> initialTargets)
        {
            var root = Path.Combine(Path.GetTempPath(), $"ollamarouter-provisioning-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var stateDirectory = Path.Combine(root, "state");

            var options = new OllamaRouterOptions
            {
                BindAddress = "http://127.0.0.1:0",
                Targets = initialTargets.ToList(),
                TerminalMonitoring = new TerminalMonitoringOptions { Enabled = false }
            };

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = root
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddOllamaRouter(
                options,
                stateDirectory: stateDirectory,
                enableTui: false,
                reprobeInterval: TimeSpan.FromSeconds(1),
                onlineCacheTtl: TimeSpan.FromSeconds(5),
                probeTimeout: TimeSpan.FromSeconds(3));

            var app = builder.Build();
            app.UseOllamaRouterPipeline();
            await app.StartAsync();

            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(60) };
            return new PipelineHarness(app, client, options, app.Services, root);
        }
    }

    private static OllamaTargetOptions TargetFor(FakeOllamaDaemon daemon) => new()
    {
        Name = daemon.Name,
        Url = daemon.Url,
        MaxParallel = 1
    };

    private static async Task<bool> WaitTargetOnlineAsync(PipelineHarness harness, string name, TimeSpan timeout)
    {
        var target = new RoutingTarget(name);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await harness.Liveness.EnsureKnownAsync(target, CancellationToken.None);
                if (state == TargetLivenessState.Online)
                {
                    return true;
                }
            }
            catch
            {
                // Probe hiccup: keep polling.
            }

            await Task.Delay(50, CancellationToken.None);
        }

        return false;
    }

    /// <summary>
    /// Sends a pinned streaming <c>POST /api/chat</c> for the shared fixture model. Mirrors the
    /// mesh suite's convention (<see cref="MeshTestHelpers.SendChatAsync"/> with
    /// <c>returnOnHeaders</c> + <see cref="MeshTestHelpers.DrainTolerantAsync"/>): the served
    /// target is asserted via the telemetry header (read at headers) and the daemon-side
    /// observation plane, and the streamed body is drained TOLERANTLY — a loopback
    /// EOF/transport hiccup after the committed chunks must never mask the routing assertion.
    /// Returns (status, served-target telemetry header).
    /// </summary>
    private static async Task<(HttpStatusCode Status, string? Served)> PinnedChatAsync(PipelineHarness harness, string? pin)
    {
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = MeshTestHelpers.BuildPrompt(16) }
            }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (pin is not null)
        {
            request.Headers.Add(OllamaReverseProxyConfig.PublicTargetHeader, pin);
        }

        using var response = await harness.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        var status = response.StatusCode;
        string? served = null;
        if (response.Headers.TryGetValues(OllamaReverseProxyConfig.PublicTargetHeader, out var servedValues))
        {
            served = servedValues.FirstOrDefault();
        }

        var (body, _) = await MeshTestHelpers.DrainTolerantAsync(response);
        Assert.True(
            body.Contains("\"done\":true", StringComparison.Ordinal) || body.Length == 0,
            $"unexpected mid-chat failure payload: {body}");
        return (status, served);
    }

    [Fact]
    public async Task PostTarget_ThenPinnedCompletion_RoutesToNewTarget_WithoutRestart()
    {
        await using var alpha = new FakeOllamaDaemon("alpha", Models, capacity: 1);
        await using var bravo = new FakeOllamaDaemon("bravo", Models, capacity: 1);
        await Task.WhenAll(alpha.StartAsync(), bravo.StartAsync());

        // The router starts with daemon A ONLY.
        await using var harness = await PipelineHarness.StartAsync([TargetFor(alpha)]);
        Assert.True(await WaitTargetOnlineAsync(harness, "alpha", TimeSpan.FromSeconds(10)), "alpha must come online");

        // Add daemon B's node through the Setup API (live liveness probe against the real
        // daemon). POST /api/router/targets is in the default key-protected set (task #50):
        // the request carries the default key stamped by the harness helper.
        var addBody = new JsonObject { ["name"] = "bravo", ["url"] = bravo.Url, ["maxParallel"] = 1 };
        using var addResponse = await harness.PostAuthedAsync(
            "/api/router/targets",
            new StringContent(addBody.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);
        var addJson = (JsonNode.Parse(await addResponse.Content.ReadAsStringAsync())! as JsonObject)!;
        Assert.True(addJson["success"]!.GetValue<bool>());
        Assert.Equal("Online", addJson["liveness"]!.GetValue<string>());
        Assert.True(addJson["modelsDiscovered"]!.GetValue<int>() >= 1, "the catalog seed must discover the daemon's models");
        Assert.Equal(2, harness.Options.Targets.Count);

        // A PINNED completion to the just-added target is served by bravo WITHOUT a restart
        // (the inference dispatch path resolves the fleet live per request — AC §1).
        var (status, served) = await PinnedChatAsync(harness, "bravo");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("bravo", served);
        Assert.True(await MeshTestHelpers.WaitForChatModelAsync(bravo, Model, TimeSpan.FromSeconds(5)),
            "daemon bravo must have received the pinned chat");
        Assert.Empty(alpha.ChatHits());
    }

    [Fact]
    public async Task PutTarget_Disabled_TakesEffectInstantly()
    {
        await using var alpha = new FakeOllamaDaemon("alpha", Models, capacity: 1);
        await using var bravo = new FakeOllamaDaemon("bravo", Models, capacity: 1);
        await Task.WhenAll(alpha.StartAsync(), bravo.StartAsync());

        await using var harness = await PipelineHarness.StartAsync([TargetFor(alpha), TargetFor(bravo)]);
        Assert.True(await WaitTargetOnlineAsync(harness, "alpha", TimeSpan.FromSeconds(10)), "alpha must come online");
        Assert.True(await WaitTargetOnlineAsync(harness, "bravo", TimeSpan.FromSeconds(10)), "bravo must come online");

        // Pinned chat to alpha: served by alpha.
        var (firstStatus, firstServed) = await PinnedChatAsync(harness, "alpha");
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.Equal("alpha", firstServed);
        var alphaHitsBefore = alpha.ChatHits().Count;
        Assert.True(alphaHitsBefore >= 1);

        // Administratively disable alpha through the Setup API (no restart). PUT
        // /api/router/targets/{name} is key-protected (task #50): the key is stamped.
        using var putResponse = await harness.PutAuthedAsync(
            "/api/router/targets/alpha",
            new StringContent("""{"enabled": false}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        Assert.False(((OllamaTargetOptions)harness.Options.Targets[0]).Enabled, "the live options instance must reflect the disable");

        // The next pinned chat to alpha falls back to the remaining eligible fleet (bravo).
        var (secondStatus, secondServed) = await PinnedChatAsync(harness, "alpha");
        Assert.Equal(HttpStatusCode.OK, secondStatus);
        Assert.Equal("bravo", secondServed);
        Assert.Equal(alphaHitsBefore, alpha.ChatHits().Count);
        Assert.True(await MeshTestHelpers.WaitForChatModelAsync(bravo, Model, TimeSpan.FromSeconds(5)),
            "bravo must have received the failed-over chat");
    }

    [Fact]
    public async Task PutConfig_ResilienceLive_NoRestart()
    {
        await using var alpha = new FakeOllamaDaemon("alpha", Models, capacity: 1);
        await alpha.StartAsync();

        await using var harness = await PipelineHarness.StartAsync([TargetFor(alpha)]);

        using var putResponse = await harness.PutAuthedAsync(
            "/api/router/config",
            new StringContent(
                """{"resilience": { "maxConsecutiveFailures": 7, "consecutiveFailuresTokenTolerance": 4000 }}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        // The shared options instance reads the new value immediately (no daemon restart).
        Assert.Equal(7, harness.Options.MaxConsecutiveFailures);

        // And the GET endpoint reports it (GET /api/router/config is key-protected — the key
        // is stamped; an unauthenticated GET would 401, see the middleware tests).
        using var getResponse = await harness.GetAuthedAsync("/api/router/config");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var json = (JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())! as JsonObject)!;
        Assert.Equal(7, ((json["resilience"]! as JsonObject)!)["maxConsecutiveFailures"]!.GetValue<int>());

        // And the on-disk user configuration layer carries it.
        var file = (JsonObject)JsonNode.Parse(File.ReadAllText(harness.ConfigPath))!;
        Assert.Equal(7, ((JsonObject)file["OllamaRouter"]!)["MaxConsecutiveFailures"]!.GetValue<int>());
    }

    [Fact]
    public async Task DeleteTarget_RemovesFromFleet()
    {
        await using var alpha = new FakeOllamaDaemon("alpha", Models, capacity: 1);
        await using var bravo = new FakeOllamaDaemon("bravo", Models, capacity: 1);
        await Task.WhenAll(alpha.StartAsync(), bravo.StartAsync());

        // The router starts with alpha only; bravo is added (and later removed) via the API.
        await using var harness = await PipelineHarness.StartAsync([TargetFor(alpha)]);
        Assert.True(await WaitTargetOnlineAsync(harness, "alpha", TimeSpan.FromSeconds(10)), "alpha must come online");

        // Add bravo through the Setup API, then remove it again (key-protected routes carry
        // the default key, task #50).
        using var addResponse = await harness.PostAuthedAsync(
            "/api/router/targets",
            new StringContent(
                new JsonObject { ["name"] = "bravo", ["url"] = bravo.Url, ["testConnectionFirst"] = false }.ToJsonString(),
                Encoding.UTF8, "application/json"));
        var addBody = await addResponse.Content.ReadAsStringAsync();
        Assert.True(addResponse.IsSuccessStatusCode, $"target add failed: {(int)addResponse.StatusCode} {addBody}");
        Assert.Equal(2, harness.Options.Targets.Count);

        using var deleteResponse = await harness.DeleteAuthedAsync("/api/router/targets/bravo");
        var deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        Assert.True(deleteResponse.IsSuccessStatusCode, $"target delete failed: {(int)deleteResponse.StatusCode} {deleteBody}");

        // GET config excludes the removed target (memory + disk) — key-protected GET.
        using var getResponse = await harness.GetAuthedAsync("/api/router/config");
        var json = (JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())! as JsonObject)!;
        var names = ((json["targets"]! as JsonArray)!).Select(t => ((JsonObject)t!)["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("bravo", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("alpha", names, StringComparer.OrdinalIgnoreCase);

        // A pinned completion to the removed name falls back WITHOUT a crash.
        var (status, _) = await PinnedChatAsync(harness, "bravo");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(await MeshTestHelpers.WaitForChatModelAsync(alpha, Model, TimeSpan.FromSeconds(5)),
            "alpha must have served the fallback chat");
    }

    [Fact]
    public async Task Persistence_RoundTripsAsConfigurationSource()
    {
        await using var alpha = new FakeOllamaDaemon("alpha", Models, capacity: 1);
        await using var bravo = new FakeOllamaDaemon("bravo", Models, capacity: 1);
        await Task.WhenAll(alpha.StartAsync(), bravo.StartAsync());

        await using var harness = await PipelineHarness.StartAsync([TargetFor(alpha)]);

        // Mutate the configuration through the Setup API (key-protected routes carry the
        // default key, task #50).
        using var addResponse = await harness.PostAuthedAsync(
            "/api/router/targets",
            new StringContent(
                new JsonObject
                {
                    ["name"] = "bravo",
                    ["url"] = bravo.Url,
                    ["hardwareHint"] = "96gb",
                    ["maxParallel"] = 2,
                    ["testConnectionFirst"] = false
                }.ToJsonString(),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);
        using var putResponse = await harness.PutAuthedAsync(
            "/api/router/config",
            new StringContent(
                """{"resilience": { "maxConsecutiveFailures": 7, "consecutiveFailuresTokenTolerance": 4000 }}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        // Bind a FRESH options instance from the on-disk user layer only: the state must survive
        // a (hypothetical) process restart through the standard configuration binding path.
        var bound = new ConfigurationBuilder()
            .AddJsonFile(harness.ConfigPath, optional: false)
            .Build()
            .GetSection(OllamaRouterOptions.SectionName)
            .Get<OllamaRouterOptions>();
        Assert.NotNull(bound);
        var boundTargets = bound!.GetEffectiveTargets();
        Assert.Equal(2, boundTargets.Count);
        Assert.Equal("alpha", boundTargets[0].Name);
        Assert.Equal(alpha.Url, boundTargets[0].Url);
        Assert.Equal("bravo", boundTargets[1].Name);
        Assert.Equal(bravo.Url, boundTargets[1].Url);
        Assert.True(boundTargets[1].Enabled);
        Assert.Equal(2, boundTargets[1].MaxParallel);
        Assert.Equal("96gb", boundTargets[1].HardwareHint);
        Assert.Equal(7, bound.MaxConsecutiveFailures);
        Assert.Equal(4000, bound.ConsecutiveFailuresTokenTolerance);
    }
}
