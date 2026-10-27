using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Tests for the fleet VRAM reclaim endpoints <c>POST /api/router/vram/reclaim</c>
/// (canonical) and <c>POST /api/router/fleet/unload</c> (exact alias): fleet-wide or
/// single-target model unloading (fail-soft), optional administrative target disabling,
/// KV-cache lease reset, and the structured per-target response.
/// </summary>
public class RouterVramReclaimTests
{
    private const string DesktopUrl = "http://127.0.0.1:11501";
    private const string ServerUrl = "http://127.0.0.1:11502";

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

    private static async Task<Harness> CreateTestAppAsync(
        OllamaRouterOptions? options = null,
        Mock<IContextAutoShrinkManager>? shrinkManager = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var optionsMock = new Mock<IOptions<OllamaRouterOptions>>();
        optionsMock.Setup(o => o.Value).Returns(options ?? TwoTargetFleet());

        var availability = new Mock<ITargetAvailabilityService>();
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient
            .Setup(c => c.StopRunningModelsAsync(DesktopUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "qwen2.5-coder:32b" });
        catalogClient
            .Setup(c => c.StopRunningModelsAsync(ServerUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "qwen3.8-27b" });

        builder.Services.AddSingleton(optionsMock.Object);
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
}
