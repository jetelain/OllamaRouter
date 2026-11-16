using OllamaRouter.Tests.TestHelpers;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ModelUninstall;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Endpoint tests for the model uninstall API (task #126): the dedicated LITERAL route
/// <c>POST /api/router/models/uninstall</c> (coexisting with the <c>models</c> catch-alls —
/// the preload sniff must keep working), the body-only 400 contract, the 404 model/target
/// validation with ZERO daemon delete calls, the 503 defensive-service shape and the
/// source-generated 200 response shape (per-target fail-soft rows in request order).
/// The REAL ModelUninstallService runs on top of a strict-mocked catalog client.
/// </summary>
public class RouterModelUninstallTests
{
    private const string DesktopUrl = "http://127.0.0.1:11435";
    private const string ServerUrl = "http://192.168.1.10:11434";
    private const string Model = "llama3:8b";

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(WebApplication app, HttpClient client, Mock<IOllamaModelCatalogClient> catalog)
        {
            App = app;
            Client = client;
            Catalog = catalog;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public Mock<IOllamaModelCatalogClient> Catalog { get; }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static JsonArray TagsBody(params string[] models)
    {
        var arr = new JsonArray();
        foreach (var name in models)
        {
            arr.Add(new JsonObject { ["name"] = name, ["model"] = name, ["size"] = 1_000L });
        }

        return arr;
    }

    private static OllamaRouterOptions TwoTargetOptions() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "Desktop", Url = DesktopUrl },
            new OllamaTargetOptions { Name = "Server", Url = ServerUrl }
        ],
        Models = new Dictionary<string, ModelThresholds>(),
        CloudOverflow = new CloudOverflowOptions { Enabled = false }
    };

    private static async Task<Harness> CreateTestAppAsync(
        string? fleetModel = Model, bool registerService = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var catalog = new Mock<IOllamaModelCatalogClient>(MockBehavior.Strict);
        // Both daemons expose the fleet model (the degraded per-target catalog enumeration
        // of the shared GetFleetModelNamesAsync helper rides the real cache over this mock).
        foreach (var url in new[] { DesktopUrl, ServerUrl })
        {
            catalog
                .Setup(c => c.GetTagsWithDigestAsync(url, It.IsAny<CancellationToken>()))
                .ReturnsAsync((fleetModel is null ? new JsonArray() : TagsBody(fleetModel), $"digest-{url}"));
        }

        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(new TestOptionsMonitor<OllamaRouterOptions>(TwoTargetOptions()));
        builder.Services.AddSingleton(catalog.Object);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IModelCatalogCacheService>(sp =>
            new ModelCatalogCacheService(sp.GetRequiredService<IOllamaModelCatalogClient>(), sp.GetRequiredService<IMemoryCache>()));

        if (registerService)
        {
            // The production DI shape (defensive nullable-tolerant registration).
            builder.Services.AddSingleton<IModelUninstallService>(sp => new ModelUninstallService(
                sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
                sp.GetRequiredService<IOllamaModelCatalogClient>(),
                sp.GetService<IModelCatalogCacheService>(),
                sp.GetService<IFleetAggregationService>(),
                sp.GetService<ITargetAvailabilityService>(),
                NullLogger<ModelUninstallService>.Instance));
        }

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        return new Harness(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, catalog);
    }

    private static async Task<HttpResponseMessage> PostUninstall(HttpClient client, string? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/router/models/uninstall");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static JsonNode ReadJson(HttpResponseMessage response)
    {
        var body = response.Content.ReadAsStringAsync().Result;
        return JsonNode.Parse(body)!;
    }

    // ------------------------------------------------------------------
    //  200 happy + fail-soft (AC7)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostUninstall_HappyPath_Returns200WithExactCamelCaseWireShape()
    {
        await using var harness = await CreateTestAppAsync();
        harness.Catalog
            .Setup(c => c.DeleteModelAsync(DesktopUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));
        harness.Catalog
            .Setup(c => c.DeleteModelAsync(ServerUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));

        var response = await PostUninstall(harness.Client, $$"""{"model":"{{Model}}","targets":["Desktop","Server"]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ReadJson(response).AsObject()!;
        Assert.Equal(Model, body["model"]!.GetValue<string>());
        var targets = body["targets"]!.AsArray();
        Assert.Equal(2, targets.Count);
        Assert.Equal("Desktop", targets[0]!["target"]!.GetValue<string>());
        Assert.True((bool)targets[0]!["success"]!);
        Assert.Null(targets[0]!["error"]); // WhenWritingNull: omitted on success rows
        Assert.Equal("Server", targets[1]!["target"]!.GetValue<string>());
        Assert.True((bool)targets[1]!["success"]!);
    }

    [Fact]
    public async Task PostUninstall_MixedResults_FailSoft200WithPerTargetError()
    {
        await using var harness = await CreateTestAppAsync();
        harness.Catalog
            .Setup(c => c.DeleteModelAsync(DesktopUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(false, "http 404: model 'llama3:8b' not found"));
        harness.Catalog
            .Setup(c => c.DeleteModelAsync(ServerUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));

        var response = await PostUninstall(harness.Client, $$"""{"model":"{{Model}}","targets":["Desktop","Server"]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // partial failure is still 200
        var body = ReadJson(response).AsObject()!;
        var targets = body["targets"]!.AsArray();
        Assert.False((bool)targets[0]!["success"]!);
        Assert.Equal("http 404: model 'llama3:8b' not found", targets[0]!["error"]!.GetValue<string>());
        Assert.True((bool)targets[1]!["success"]!);
    }

    [Fact]
    public async Task PostUninstall_TargetNames_MatchedCaseInsensitively_CanonicalNamesEchoed()
    {
        await using var harness = await CreateTestAppAsync();
        harness.Catalog
            .Setup(c => c.DeleteModelAsync(DesktopUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));

        var response = await PostUninstall(harness.Client, $$"""{"model":"{{Model}}","targets":["desktop"]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ReadJson(response).AsObject()!;
        var row = body["targets"]!.AsArray()![0]!.AsObject()!;
        Assert.Equal("Desktop", row["target"]!.GetValue<string>());
        Assert.True((bool)row["success"]!);
    }

    // ------------------------------------------------------------------
    //  404s — with ZERO daemon delete calls (AC8)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostUninstall_UnknownModel_Returns404_WithoutAnyDaemonCall()
    {
        await using var harness = await CreateTestAppAsync();
        // Strict catalog mock: NO DeleteModelAsync scripted — any daemon call throws.

        var response = await PostUninstall(harness.Client, """{"model":"ghost:1b","targets":["Desktop"]}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("model not found: ghost:1b", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostUninstall_UnknownTarget_Returns404_WithoutAnyDaemonCall()
    {
        await using var harness = await CreateTestAppAsync();
        // Strict catalog mock: NO DeleteModelAsync scripted — a valid model with an unknown
        // second target must still issue ZERO delete calls (all names validated first).
        harness.Catalog
            .Setup(c => c.DeleteModelAsync(DesktopUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));

        var response = await PostUninstall(harness.Client, $$"""{"model":"{{Model}}","targets":["Desktop","Bogus"]}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("target not found: Bogus", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostUninstall_CloudPseudoTarget_Returns404()
    {
        await using var harness = await CreateTestAppAsync();

        var response = await PostUninstall(harness.Client, $$"""{"model":"{{Model}}","targets":["Cloud"]}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("target not found: Cloud", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    //  400 body contract (AC8)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)] // no body at all
    [InlineData("")] // empty body
    [InlineData("{not json")] // malformed JSON
    [InlineData("{}")] // both fields absent
    [InlineData("""{"model":"  ","targets":["Desktop"]}""")] // blank model
    [InlineData("""{"model":"llama3:8b","targets":[]}""")] // empty targets
    [InlineData("""{"model":"llama3:8b","targets":["  "]}""")] // blank target name
    [InlineData("""{"targets":["Desktop"]}""")] // no model field
    public async Task PostUninstall_InvalidBody_Returns400(string? body)
    {
        await using var harness = await CreateTestAppAsync();

        var response = await PostUninstall(harness.Client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "request body with a 'model' name and a non-empty 'targets' array is required",
            ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    //  503 defensive service (minimal host)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostUninstall_ServiceUnregistered_Returns503()
    {
        await using var harness = await CreateTestAppAsync(registerService: false);

        var response = await PostUninstall(harness.Client, $$"""{"model":"{{Model}}","targets":["Desktop"]}""");

        Assert.Equal((HttpStatusCode)503, response.StatusCode);
        Assert.Equal("model uninstall service is not available", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    //  Registration-order sanity: the literal route and the preload catch-all coexist
    // ------------------------------------------------------------------

    [Fact]
    public async Task RoutePrecedence_UninstallLiteralHitsDedicatedHandler_WhilePreloadSuffixStillSniffs()
    {
        await using var harness = await CreateTestAppAsync();
        harness.Catalog
            .Setup(c => c.DeleteModelAsync(DesktopUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));

        // 1. The literal route resolves to the uninstall handler (200, the uninstall shape).
        var uninstall = await PostUninstall(harness.Client, $$"""{"model":"{{Model}}","targets":["Desktop"]}""");
        Assert.Equal(HttpStatusCode.OK, uninstall.StatusCode);
        Assert.Equal(Model, ReadJson(uninstall).AsObject()!["model"]!.GetValue<string>());

        // 2. The POST catch-all's /preload suffix sniff still works on the same host (the
        // model resolves and the selection runs to a 503 — no capability metadata is
        // discoverable on the strict mock — proving the literal route did not swallow it).
        var preload = await harness.Client.PostAsync($"/api/router/models/{Uri.EscapeDataString(Model + "/preload")}", content: null);
        Assert.Equal((HttpStatusCode)503, preload.StatusCode);
        Assert.Contains("no capable target", ReadJson(preload).AsObject()!["error"]!.GetValue<string>());

        // 3. A POST without the /preload suffix keeps its historical 404 invalid-path shape
        // (the catch-all's handler, not the literal uninstall route).
        var bare = await harness.Client.PostAsync("/api/router/models/some-model", content: null);
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        Assert.Contains("invalid preload path", ReadJson(bare).AsObject()!["error"]!.GetValue<string>());
    }
}
