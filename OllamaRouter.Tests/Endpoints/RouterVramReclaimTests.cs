using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.Vram;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Tests.TestHelpers;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Tests for the fleet VRAM reclaim endpoints <c>POST /api/router/vram/reclaim</c>
/// (canonical) and <c>POST /api/router/fleet/unload</c> (exact alias): fleet-wide, single-target,
/// or local-machine (location=local) model unloading (fail-soft), optional administrative target
/// disabling, KV-cache lease reset, and the structured per-target response.
/// </summary>
public class RouterVramReclaimTests
{
    private const string DesktopUrl = "http://127.0.0.1:11501";
    private const string ServerUrl = "http://127.0.0.1:11502";

    // Mixed-location fleet URLs: LoopA (loopback ⇒ Local), OverrideB (non-loopback URL with an
    // explicit Location override ⇒ Local), RemoteC (RFC 1918 ⇒ Remote), CloudD (public host ⇒ Cloud).
    private const string LoopAUrl = "http://127.0.0.1:11511";
    private const string OverrideBUrl = "http://10.0.0.5:11434";
    private const string RemoteCUrl = "http://192.168.1.50:11434";
    private const string CloudDUrl = "http://gpu.example.net:11434";

    private sealed class Harness : IAsyncDisposable
    {
        public WebApplication App { get; }
        public HttpClient Client { get; }
        public Mock<ITargetAvailabilityService> Availability { get; }
        public Mock<IOllamaModelCatalogClient> CatalogClient { get; }

        public Harness(
            WebApplication app,
            HttpClient client,
            Mock<ITargetAvailabilityService> availability,
            Mock<IOllamaModelCatalogClient> catalogClient)
        {
            App = app;
            Client = client;
            Availability = availability;
            CatalogClient = catalogClient;
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static OllamaRouterOptions TwoTargetFleet() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "Desktop", Url = DesktopUrl },
            new OllamaTargetOptions { Name = "Server", Url = ServerUrl }
        ]
    };

    /// <summary>
    /// Mixed-location fleet: LoopA (URL-classified Local), OverrideB (explicit
    /// <see cref="TargetLocation.Local"/> override on a non-loopback URL), RemoteC (Remote),
    /// CloudD (Cloud) — in this configured order.
    /// </summary>
    private static OllamaRouterOptions MixedLocationFleet() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "LoopA", Url = LoopAUrl },
            new OllamaTargetOptions { Name = "OverrideB", Url = OverrideBUrl, Location = TargetLocation.Local },
            new OllamaTargetOptions { Name = "RemoteC", Url = RemoteCUrl },
            new OllamaTargetOptions { Name = "CloudD", Url = CloudDUrl }
        ]
    };

    /// <summary>
    /// Fleet with no Local-classified targets (used for the
    /// "target not found: local" 404 case).
    /// </summary>
    private static OllamaRouterOptions NonLocalFleet() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "RemoteC", Url = RemoteCUrl },
            new OllamaTargetOptions { Name = "CloudD", Url = CloudDUrl }
        ]
    };

    private static void SetupMixedCatalog(Mock<IOllamaModelCatalogClient> catalogClient)
    {
        catalogClient
            .Setup(c => c.StopRunningModelsAsync(LoopAUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "local-model-a" });
        catalogClient
            .Setup(c => c.StopRunningModelsAsync(OverrideBUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "local-model-b" });
        catalogClient
            .Setup(c => c.StopRunningModelsAsync(RemoteCUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "remote-model-c" });
        catalogClient
            .Setup(c => c.StopRunningModelsAsync(CloudDUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "cloud-model-d" });
    }

    private static async Task<Harness> CreateTestAppAsync(
        OllamaRouterOptions? options = null,
        Mock<IContextAutoShrinkManager>? shrinkManager = null,
        Action<Mock<IOllamaModelCatalogClient>>? configureCatalogClient = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var optionsMonitor = new TestOptionsMonitor<OllamaRouterOptions>(options ?? TwoTargetFleet());

        var availability = new Mock<ITargetAvailabilityService>();
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        if (configureCatalogClient is null)
        {
            catalogClient
                .Setup(c => c.StopRunningModelsAsync(DesktopUrl, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { "qwen2.5-coder:32b" });
            catalogClient
                .Setup(c => c.StopRunningModelsAsync(ServerUrl, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { "qwen3.8-27b" });
        }
        else
        {
            configureCatalogClient(catalogClient);
        }

        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(optionsMonitor);
        builder.Services.AddSingleton(availability.Object);
        builder.Services.AddSingleton(catalogClient.Object);
        if (shrinkManager is not null)
        {
            builder.Services.AddSingleton(shrinkManager.Object);
        }

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new Harness(app, client, availability, catalogClient);
    }

    private static JsonObject ParseObject(JsonNode? node)
    {
        Assert.NotNull(node);
        var obj = (JsonObject)node!;
        return obj;
    }

    private static string[] UnloadedModels(JsonObject targetEntry)
    {
        var models = (JsonArray)targetEntry["unloadedModels"]!;
        return models.Select(n => n!.GetValue<string>()).ToArray();
    }

    [Fact]
    public async Task NoArgs_UnloadsAllEffectiveTargets_Concurrently()
    {
        var harness = await CreateTestAppAsync();
        try
        {
            var response = await harness.Client.PostAsync("/api/router/vram/reclaim", new StringContent(""));
            var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("true", json["success"]!.ToString());
            Assert.Equal("2", json["totalUnloaded"]!.ToString());
            Assert.Null(json["message"]);
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(2, targets.Count);
            var desktop = (JsonObject)targets[0]!;
            Assert.Equal("Desktop", desktop["target"]!.GetValue<string>());
            Assert.Equal(new[] { "qwen2.5-coder:32b" }, UnloadedModels(desktop));
            Assert.Equal("false", desktop["disabled"]!.ToString());
            var server = (JsonObject)targets[1]!;
            Assert.Equal("Server", server["target"]!.GetValue<string>());
            Assert.Equal(new[] { "qwen3.8-27b" }, UnloadedModels(server));
            Assert.Equal("false", server["disabled"]!.ToString());

            // One stop call per target URL (concurrent); availability NEVER touched (default false).
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(DesktopUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(ServerUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.Availability.Verify(
                t => t.Update(It.IsAny<IReadOnlyDictionary<string, bool>>()), Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueryTarget_SingleNodeOnly()
    {
        var harness = await CreateTestAppAsync();
        try
        {
            var response = await harness.Client.PostAsync(
                "/api/router/vram/reclaim?target=Desktop", new StringContent(""));
            var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var targets = (JsonArray)json["targets"]!;
            Assert.Single(targets);
            Assert.Equal("Desktop", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
            Assert.Equal("1", json["totalUnloaded"]!.ToString());

            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(DesktopUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(ServerUrl, It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task BodyTarget_OverridesQuery()
    {
        var harness = await CreateTestAppAsync();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/router/vram/reclaim?target=Desktop")
            {
                Content = new StringContent("{\"target\":\"Server\",\"disableTarget\":false}")
            };
            var response = await harness.Client.SendAsync(request);
            var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var targets = (JsonArray)json["targets"]!;
            Assert.Single(targets);
            Assert.Equal("Server", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());

            // The body value wins: only Server is hit, despite the conflicting query value.
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(ServerUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(DesktopUrl, It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task TargetMatch_CaseInsensitive()
    {
        var harness = await CreateTestAppAsync();
        try
        {
            var response = await harness.Client.PostAsync(
                "/api/router/vram/reclaim?target=desktop", new StringContent(""));
            var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var targets = (JsonArray)json["targets"]!;
            Assert.Single(targets);
            // Canonical configured name is reported.
            Assert.Equal("Desktop", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnknownTarget_Returns404()
    {
        var harness = await CreateTestAppAsync();
        try
        {
            var response = await harness.Client.PostAsync(
                "/api/router/vram/reclaim?target=Workstation", new StringContent(""));
            var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("target not found: Workstation", json["error"]!.GetValue<string>());

            // The catalog client is never touched for an unknown target.
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisableTargetTrue_UpdatesAvailability_AndResetsLeases()
    {
        var shrinkManager = new Mock<IContextAutoShrinkManager>();
        var harness = await CreateTestAppAsync(shrinkManager: shrinkManager);
        try
        {
            var response = await harness.Client.PostAsync(
                "/api/router/vram/reclaim?disableTarget=true", new StringContent(""));
            var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal("true", ((JsonObject)targets[0]!)["disabled"]!.ToString());
            Assert.Equal("true", ((JsonObject)targets[1]!)["disabled"]!.ToString());

            // One availability update per target with {name: false}.
            harness.Availability.Verify(
                t => t.Update(It.Is<IReadOnlyDictionary<string, bool>>(d =>
                    d.Count == 1 && d.Any(kv =>
                        string.Equals(kv.Key, "Desktop", StringComparison.OrdinalIgnoreCase) && !kv.Value))),
                Times.Once);
            harness.Availability.Verify(
                t => t.Update(It.Is<IReadOnlyDictionary<string, bool>>(d =>
                    d.Count == 1 && d.Any(kv =>
                        string.Equals(kv.Key, "Server", StringComparison.OrdinalIgnoreCase) && !kv.Value))),
                Times.Once);
            harness.Availability.VerifyNoOtherCalls();

            // KV-cache leases reset for every processed target.
            shrinkManager.Verify(s => s.ResetTargetLeases(new RoutingTarget("Desktop")), Times.Once);
            shrinkManager.Verify(s => s.ResetTargetLeases(new RoutingTarget("Server")), Times.Once);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisableTargetFalse_DoesNotTouchAvailability_ButStillResetsLeases()
    {
        var shrinkManager = new Mock<IContextAutoShrinkManager>();
        var harness = await CreateTestAppAsync(shrinkManager: shrinkManager);
        try
        {
            var response = await harness.Client.PostAsync(
                "/api/router/vram/reclaim?disableTarget=false", new StringContent(""));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            harness.Availability.Verify(
                t => t.Update(It.IsAny<IReadOnlyDictionary<string, bool>>()), Times.Never);

            // Leases are reset on every unload regardless of disableTarget.
            shrinkManager.Verify(s => s.ResetTargetLeases(new RoutingTarget("Desktop")), Times.Once);
            shrinkManager.Verify(s => s.ResetTargetLeases(new RoutingTarget("Server")), Times.Once);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnreachableTarget_FailSoft()
    {
        var harness = await CreateTestAppAsync();
        try
        {
            // Override the Server mock: an unreachable daemon yields an empty list
            // (StopRunningModelsAsync is fail-soft by contract).
            harness.CatalogClient
                .Setup(c => c.StopRunningModelsAsync(ServerUrl, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<string>());

            var response = await harness.Client.PostAsync("/api/router/vram/reclaim", new StringContent(""));
            var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("true", json["success"]!.ToString());
            Assert.Equal("1", json["totalUnloaded"]!.ToString());
            var targets = (JsonArray)json["targets"]!;
            var desktop = (JsonObject)targets[0]!;
            Assert.Equal("Desktop", desktop["target"]!.GetValue<string>());
            Assert.Single(UnloadedModels(desktop));
            var server = (JsonObject)targets[1]!;
            Assert.Equal("Server", server["target"]!.GetValue<string>());
            Assert.Empty(UnloadedModels(server));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task FleetUnload_Alias_BehavesIdentically()
    {
        var harness = await CreateTestAppAsync();
        try
        {
            var response = await harness.Client.PostAsync("/api/router/fleet/unload", new StringContent(""));
            var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("true", json["success"]!.ToString());
            Assert.Equal("2", json["totalUnloaded"]!.ToString());
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(2, targets.Count);
            Assert.Equal("Desktop", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
            Assert.Equal("Server", ((JsonObject)targets[1]!)["target"]!.GetValue<string>());
            Assert.Null(json["message"]);

            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(DesktopUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(ServerUrl, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task NoEffectiveTargets_ReturnsSuccessFalse()
    {
        // Empty Targets list AND no LocalUrl/RemoteUrl: the effective target list is empty.
        var harness = await CreateTestAppAsync(new OllamaRouterOptions());
        try
        {
            var response = await harness.Client.PostAsync("/api/router/vram/reclaim", new StringContent(""));
            var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("false", json["success"]!.ToString());
            Assert.Equal("no targets configured", json["message"]!.GetValue<string>());
            Assert.Equal("0", json["totalUnloaded"]!.ToString());
            Assert.Empty((JsonArray)json["targets"]!);

            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public void ResponseSchema_ExactKeys()
    {
        var response = new RouterVramReclaimResponse(
            Success: true,
            TotalUnloaded: 2,
            Targets:
            [
                new RouterVramReclaimTargetResult(
                    "Desktop", ["qwen2.5-coder:32b"], false),
                new RouterVramReclaimTargetResult(
                    "Server", ["qwen3.8-27b"], false)
            ]);

        var json = System.Text.Json.JsonSerializer.Serialize(
            response, OllamaRouterJsonSerializerContext.Default.RouterVramReclaimResponse);
        var node = (JsonObject)JsonNode.Parse(json)!;

        Assert.Equal(
            new HashSet<string> { "success", "totalUnloaded", "targets" },
            ((IDictionary<string, JsonNode?>)node).Keys.ToHashSet());
        Assert.Equal("true", node["success"]!.ToString());
        Assert.Equal("2", node["totalUnloaded"]!.ToString());

        var first = (JsonObject)node["targets"]![0]!;
        Assert.Equal(
            new HashSet<string> { "target", "unloadedModels", "disabled" },
            ((IDictionary<string, JsonNode?>)first).Keys.ToHashSet());
    }

    // ------------------------------------------------------------------
    //  location=local (local-machine) selection
    // ------------------------------------------------------------------

    private static async Task<(HttpStatusCode Status, JsonObject Json)> SendAsync(
        HttpClient client, string url, string? body = null)
    {
        var response = body is null
            ? await client.PostAsync(url, new StringContent(""))
            : await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body) });
        var json = ParseObject(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        return (response.StatusCode, json);
    }

    [Fact]
    public async Task QueryLocationLocal_SelectsOnlyLocalTargets_InConfiguredOrder()
    {
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=local");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("true", json["success"]!.ToString());
            Assert.Equal("2", json["totalUnloaded"]!.ToString());
            Assert.Null(json["message"]);

            // ONLY the Local-classified targets (URL-loopback + explicit override),
            // in configured order — RemoteC/CloudD never appear.
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(2, targets.Count);
            var loopA = (JsonObject)targets[0]!;
            Assert.Equal("LoopA", loopA["target"]!.GetValue<string>());
            Assert.Equal(new[] { "local-model-a" }, UnloadedModels(loopA));
            var overrideB = (JsonObject)targets[1]!;
            Assert.Equal("OverrideB", overrideB["target"]!.GetValue<string>());
            Assert.Equal(new[] { "local-model-b" }, UnloadedModels(overrideB));

            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(LoopAUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(OverrideBUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(RemoteCUrl, It.IsAny<CancellationToken>()), Times.Never);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(CloudDUrl, It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task BodyLocationLocal_SelectsOnlyLocalTargets()
    {
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim", "{\"location\":\"local\"}");

            Assert.Equal(HttpStatusCode.OK, status);
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(2, targets.Count);
            Assert.Equal("LoopA", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
            Assert.Equal("OverrideB", ((JsonObject)targets[1]!)["target"]!.GetValue<string>());

            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(LoopAUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(OverrideBUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(It.Is<string>(x =>
                    string.Equals(x, RemoteCUrl) || string.Equals(x, CloudDUrl)),
                    It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task BodyLocationUnsupported_WinsOverQuery_Returns400()
    {
        // An explicit non-empty body field is authoritative per field (body wins over query):
        // the unsupported body value must 400 despite the supported query value.
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=local", "{\"location\":\"remote\"}");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("location not supported: remote", json["error"]!.GetValue<string>());

            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task BodyLocationLocal_WinsOverUnsupportedQuery_ReturnsLocalSelection()
    {
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=remote", "{\"location\":\"local\"}");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("2", json["totalUnloaded"]!.ToString());
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(2, targets.Count);
            Assert.Equal("LoopA", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
            Assert.Equal("OverrideB", ((JsonObject)targets[1]!)["target"]!.GetValue<string>());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task MalformedBody_LocationQueryValue_StillUsed()
    {
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            // A malformed JSON body falls through to the query values (the vram-reclaim pattern).
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=local", "{\"location\": ");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("true", json["success"]!.ToString());
            Assert.Equal("2", json["totalUnloaded"]!.ToString());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task TargetName_WinsOverUnsupportedLocation_No400()
    {
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?target=LoopA&location=remote");

            // Named selection is unchanged and wins over ANY location value — no 400.
            Assert.Equal(HttpStatusCode.OK, status);
            var targets = (JsonArray)json["targets"]!;
            Assert.Single(targets);
            Assert.Equal("LoopA", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());

            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(LoopAUrl, It.IsAny<CancellationToken>()), Times.Once);
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(It.Is<string>(x => !string.Equals(x, LoopAUrl)),
                    It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("LOCAL")]
    [InlineData("Local")]
    [InlineData("%20local%20")] // surrounding whitespace (URL-encoded)
    [InlineData("lOcAl")]
    public async Task LocationLocal_CaseAndWhitespaceInsensitive(string encodedLocation)
    {
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, $"/api/router/vram/reclaim?location={encodedLocation}");

            Assert.Equal(HttpStatusCode.OK, status);
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(2, targets.Count);
            Assert.Equal("LoopA", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
            Assert.Equal("OverrideB", ((JsonObject)targets[1]!)["target"]!.GetValue<string>());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task LocationEmptyString_TreatedAsAbsent_FleetWide()
    {
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            // ?location= (empty) is treated as absent → fleet-wide behavior unchanged.
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("true", json["success"]!.ToString());
            Assert.Equal("4", json["totalUnloaded"]!.ToString());
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(4, targets.Count);
            Assert.Equal("LoopA", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
            Assert.Equal("CloudD", ((JsonObject)targets[3]!)["target"]!.GetValue<string>());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task LocationLocal_NoLocalTargets_Returns404()
    {
        var harness = await CreateTestAppAsync(NonLocalFleet());
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=local");

            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("target not found: local", json["error"]!.GetValue<string>());

            // The catalog client is never touched when no Local target matches.
            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task LocationLocal_EmptyFleet_Returns404_NotFailSoft()
    {
        var harness = await CreateTestAppAsync(new OllamaRouterOptions());
        try
        {
            // An empty fleet with explicit location=local 404s (NOT the 200 fail-soft).
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=local");

            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("target not found: local", json["error"]!.GetValue<string>());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("remote", "location not supported: remote")]
    [InlineData("cloud", "location not supported: cloud")]
    [InlineData("Lokal", "location not supported: Lokal")] // typo: trimmed echo
    public async Task LocationUnsupported_Returns400_OllamaErrorResponse(string value, string expectedError)
    {
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, $"/api/router/vram/reclaim?location={value}");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(expectedError, json["error"]!.GetValue<string>());
            Assert.Null(json["success"]);

            harness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task LocationUnsupported_EmptyFleet_StillReturns400()
    {
        // The 400 is validated BEFORE the empty-fleet check.
        var harness = await CreateTestAppAsync(new OllamaRouterOptions());
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=remote");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("location not supported: remote", json["error"]!.GetValue<string>());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task LocationLocal_DisableTargetTrue_DisablesEveryLocalTarget_AndResetsLeases()
    {
        var shrinkManager = new Mock<IContextAutoShrinkManager>();
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), shrinkManager, SetupMixedCatalog);
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=local&disableTarget=true");

            Assert.Equal(HttpStatusCode.OK, status);
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(2, targets.Count);
            Assert.Equal("true", ((JsonObject)targets[0]!)["disabled"]!.ToString());
            Assert.Equal("true", ((JsonObject)targets[1]!)["disabled"]!.ToString());

            // One persisted availability update per processed (Local) target — never for
            // the non-Local targets.
            harness.Availability.Verify(
                t => t.Update(It.Is<IReadOnlyDictionary<string, bool>>(d =>
                    d.Count == 1 && d.Any(kv =>
                        string.Equals(kv.Key, "LoopA", StringComparison.OrdinalIgnoreCase) && !kv.Value))),
                Times.Once);
            harness.Availability.Verify(
                t => t.Update(It.Is<IReadOnlyDictionary<string, bool>>(d =>
                    d.Count == 1 && d.Any(kv =>
                        string.Equals(kv.Key, "OverrideB", StringComparison.OrdinalIgnoreCase) && !kv.Value))),
                Times.Once);
            harness.Availability.VerifyNoOtherCalls();

            // KV-cache leases reset for every processed (Local) target only.
            shrinkManager.Verify(s => s.ResetTargetLeases(new RoutingTarget("LoopA")), Times.Once);
            shrinkManager.Verify(s => s.ResetTargetLeases(new RoutingTarget("OverrideB")), Times.Once);
            shrinkManager.VerifyNoOtherCalls();
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task LocationLocal_UnreachableLocalDaemon_FailSoft()
    {
        var harness = await CreateTestAppAsync(MixedLocationFleet(), configureCatalogClient:
            catalogClient =>
            {
                SetupMixedCatalog(catalogClient);
                // LoopA daemon unreachable: StopRunningModelsAsync is fail-soft ⇒ empty list
                // (overrides the mixed-catalog setup).
                catalogClient
                    .Setup(c => c.StopRunningModelsAsync(LoopAUrl, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<string>());
            });
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?location=local");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("true", json["success"]!.ToString());
            Assert.Equal("1", json["totalUnloaded"]!.ToString());
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(2, targets.Count);
            var loopA = (JsonObject)targets[0]!;
            Assert.Equal("LoopA", loopA["target"]!.GetValue<string>());
            Assert.Empty(UnloadedModels(loopA));
            var overrideB = (JsonObject)targets[1]!;
            Assert.Equal("OverrideB", overrideB["target"]!.GetValue<string>());
            Assert.Equal(new[] { "local-model-b" }, UnloadedModels(overrideB));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task FleetUnload_Alias_LocationParity()
    {
        // location=local → 200 Local-only selection; unsupported location → 400 (alias path).
        var harness = await CreateTestAppAsync(
            MixedLocationFleet(), configureCatalogClient: SetupMixedCatalog);
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/fleet/unload?location=local");

            Assert.Equal(HttpStatusCode.OK, status);
            var targets = (JsonArray)json["targets"]!;
            Assert.Equal(2, targets.Count);
            Assert.Equal("LoopA", ((JsonObject)targets[0]!)["target"]!.GetValue<string>());
            Assert.Equal("OverrideB", ((JsonObject)targets[1]!)["target"]!.GetValue<string>());
            Assert.Null(json["message"]);

            var (badStatus, badJson) = await SendAsync(
                harness.Client, "/api/router/fleet/unload?location=remote");

            Assert.Equal(HttpStatusCode.BadRequest, badStatus);
            Assert.Equal("location not supported: remote", badJson["error"]!.GetValue<string>());
        }
        finally
        {
            await harness.DisposeAsync();
        }

        // No-Local fleet → 404 'target not found: local' (alias path).
        var noLocalHarness = await CreateTestAppAsync(NonLocalFleet());
        try
        {
            var (status, json) = await SendAsync(
                noLocalHarness.Client, "/api/router/fleet/unload?location=local");

            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("target not found: local", json["error"]!.GetValue<string>());

            noLocalHarness.CatalogClient.Verify(
                c => c.StopRunningModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await noLocalHarness.DisposeAsync();
        }
    }

    [Fact]
    public async Task TargetNamePresent_EmptyFleet_LocationIgnored_FailSoftUnchanged()
    {
        // Named selection on an empty fleet keeps the existing 200 fail-soft, even with
        // a present (unsupported) location value.
        var harness = await CreateTestAppAsync(new OllamaRouterOptions());
        try
        {
            var (status, json) = await SendAsync(
                harness.Client, "/api/router/vram/reclaim?target=Desktop&location=remote");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("false", json["success"]!.ToString());
            Assert.Equal("no targets configured", json["message"]!.GetValue<string>());
            Assert.Empty((JsonArray)json["targets"]!);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }
}
