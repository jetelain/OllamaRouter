using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OllamaRouter.Client;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Endpoints.RouterApi.Preload;
using OllamaRouter.Endpoints.RouterApi.Recommendations;
using OllamaRouter.Endpoints.RouterApi.SessionAffinity;
using OllamaRouter.Endpoints.RouterApi.Sessions;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Endpoints.RouterApi.Vram;
using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Recommendations;
using Pmad.LocalApi.Security;

namespace OllamaRouter.Tests.Client;

/// <summary>
/// Unit tests for the shared typed client <see cref="OllamaRouterApiClient"/> (task #98):
/// Bearer auth wiring (explicit key / default-key reader / keyless default mode), the exact
/// wire names of every request body, per-segment catch-all path escaping, the flat
/// <c>POST /targets</c> dictionary, the always-<c>residentModels</c>-array monitor contract,
/// non-2xx error mapping (<see cref="OllamaRouterApiException"/> <c>Status</c> + <c>Error</c>
/// with a 512-char raw-body fallback), the single automatic 401 default-key re-read-and-retry,
/// and a table-driven round-trip of ALL 28 route-table methods against the source-generated
/// <see cref="OllamaRouterClientJsonContext"/>.
/// Self-contained: a fake <see cref="HttpMessageHandler"/> captures method/path/body/headers and
/// serves canned responses; a temp-dir <c>OllamaRouterDefaultKeyReader</c> with
/// <see cref="PlainKeyProtector"/> (identity protect) keeps the key-rotation tests off real DPAPI.
/// </summary>
public sealed class OllamaRouterApiClientTests : IDisposable
{
    private static readonly Uri BaseUrl = new("http://127.0.0.1:9999/");
    private static readonly DateTimeOffset GeneratedAt = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly string RouteTableKey = MakeKey("route-table-key");

    private readonly string _tempRoot;
    private readonly string _configDir;

    public OllamaRouterApiClientTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ollamarouter-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _configDir = Path.Combine(_tempRoot, "config");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp-dir cleanup.
        }
    }

    // ------------------------------------------------------------------ scenarios

    [Fact]
    public async Task GetInfoAsync_WithConfiguredKey_SendsBearerAndParsesResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, RouterInfoJson));
        using var client = CreateClient(handler, apiKey: "explicit-123");

        var info = await client.GetInfoAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/router/info", request.PathAndQuery);
        Assert.Equal("Bearer explicit-123", request.Authorization);
        Assert.Equal("2026.11.0", info.Version);
        Assert.True(info.IsRouter);
        Assert.Equal(3, info.Fleet.TotalTargets);
        Assert.Equal(2, info.Fleet.OnlineTargets);
        Assert.True(info.Fleet.CloudEnabled);
        Assert.True(info.Features.DynamicContext);
        Assert.True(info.Features.Failover);
        Assert.True(info.Features.AntiThrashingAffinity);
        Assert.False(info.Features.AutoShrink);

        // Process telemetry (task #99).
        Assert.Equal(1234, info.ProcessId);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero), info.StartedAtUtc);
        Assert.Equal(3600.5, info.UptimeSeconds);
    }

    [Fact]
    public async Task NoKey_NoReader_SendsRequestWithoutAuthorizationHeader()
    {
        // Reader points at an EMPTY config dir (never created) -> ReadDefaultKey() is null,
        // so the request must go out keyless (default-mode reads are key-optional).
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, RouterInfoJson));
        using var client = CreateClient(handler, keyReader: KeyReader());

        var info = await client.GetInfoAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);
        Assert.Equal("2026.11.0", info.Version);
    }

    [Fact]
    public async Task AddTargetAsync_SerializesExactWireNames_WithJsonContentType_AndParsesCreatedTarget()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.Created, AddTargetResponseJson));
        using var client = CreateClient(handler);

        var response = await client.AddTargetAsync(new AddTargetRequest(
            "Edge", "http://edge:11434", MaxParallel: 2, TestConnectionFirst: true,
            HardwareHint: "RTX 4090", Type: "ollama", Provider: "LocalLab",
            Models: [new TargetModelDto("llama3:8b", ContextLength: 8192, Quantization: "q8_0")],
            PowerWatts: 95.0));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/router/targets", request.PathAndQuery);
        Assert.Equal("application/json", request.ContentType);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(request.Body!), JsonNode.Parse(AddTargetBodyJson)),
            $"unexpected request body: {request.Body}");
        Assert.True(response.Success);
        Assert.True(response.ConnectionTested);
        Assert.Equal("Online", response.Liveness);
        Assert.Equal(2, response.ModelsDiscovered);
        Assert.Equal(12, response.LatencyMs);
        Assert.Equal("Edge", response.Target!.Name);
        Assert.Equal(95.0, response.Target.PowerWatts);
    }

    [Fact]
    public async Task GetModelDetailAsync_EscapesCatchAllPathSegmentsPerSegment()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, ModelDetailJson));
        using var client = CreateClient(handler);

        var detail = await client.GetModelDetailAsync("org/model:tag");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/router/models/org/model%3Atag", request.PathAndQuery);
        Assert.Equal("org/model:tag", detail.Name);
        var veto = Assert.Single(detail.Targets);
        Assert.Equal("insufficient VRAM", veto.VetoReason);
    }

    [Fact]
    public async Task UpdateTargetAvailabilityAsync_SendsFlatDictionaryBody_AndParsesAppliedFlags()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK,
            """{"Local":true,"Remote":false}"""));
        using var client = CreateClient(handler);

        var applied = await client.UpdateTargetAvailabilityAsync(
            new Dictionary<string, bool> { ["Local"] = true, ["Remote"] = false });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/targets", request.PathAndQuery);
        Assert.Equal("application/json", request.ContentType);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(request.Body!), JsonNode.Parse("""{"Local":true,"Remote":false}""")),
            $"unexpected request body: {request.Body}");
        Assert.Equal(2, applied.Count);
        Assert.True(applied["Local"]);
        Assert.False(applied["Remote"]);
    }

    [Fact]
    public async Task GetMonitorStateAsync_ResidentModels_AlwaysArrayOnClientReadPath()
    {
        // The canned monitor payload below mixes BOTH wire forms: an explicit resident-model
        // list on the Desktop row and a JSON `null` on the idle Cloud row. The always-array
        // contract (task #45) must hold on the CLIENT read path: the null is normalized to an
        // empty array, so Fleet[i].ResidentModels is never null after a GetMonitorStateAsync.
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, MonitorStateJson));
        using var client = CreateClient(handler);

        var state = await client.GetMonitorStateAsync();

        Assert.Equal("/monitor/api", Assert.Single(handler.Requests).PathAndQuery);
        Assert.Equal(["llama3:8b", "qwen:2b"], state.Fleet[0].ResidentModels);
        var idleResident = state.Fleet[1].ResidentModels;
        Assert.NotNull(idleResident);
        Assert.Empty(idleResident);
    }

    [Fact]
    public async Task PreloadModelAsync_NullRequest_SendsNoRequestBody()
    {
        // Optional-body endpoints send a JSON body ONLY when the request object is non-null;
        // an absent body is a valid server-side contract (all fields optional).
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, PreloadJson));
        using var client = CreateClient(handler);

        var response = await client.PreloadModelAsync("org/model:tag");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/router/models/org/model%3Atag/preload", request.PathAndQuery);
        Assert.Null(request.Body);
        Assert.Equal("org/model:tag", response.Model);
    }

    [Fact]
    public async Task Non2xx_ParsesErrorField_IntoOllamaRouterApiException()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.NotFound,
            """{"error":"target not found: X"}"""));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<OllamaRouterApiException>(() => client.DeleteTargetAsync("X"));

        Assert.Equal(HttpStatusCode.NotFound, exception.Status);
        Assert.Equal("target not found: X", exception.Error);
    }

    [Fact]
    public async Task UninstallModelAsync_404UnknownModel_SurfacesOllamaRouterApiException()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.NotFound,
            """{"error":"model not found: nope"}"""));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<OllamaRouterApiException>(() =>
            client.UninstallModelAsync("nope", ["Desktop"]));

        Assert.Equal(HttpStatusCode.NotFound, exception.Status);
        Assert.Equal("model not found: nope", exception.Error);
        Assert.Equal("/api/router/models/uninstall", Assert.Single(handler.Requests).PathAndQuery);
    }

    [Fact]
    public async Task Non2xx_NonJsonBody_RawTextTruncatedTo512Chars()
    {
        var raw = "upstream gateway failure: " + new string('x', 700);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(raw, Encoding.UTF8, "text/plain"),
        });
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<OllamaRouterApiException>(() => client.GetInfoAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, exception.Status);
        Assert.NotNull(exception.Error);
        Assert.Equal(512, exception.Error!.Length);
        Assert.Equal(raw[..512], exception.Error);
    }

    [Fact]
    public async Task Unauthorized_ReloadsRotatedKeyFromDisk_RetriesSameRequestOnce_WithNewKey()
    {
        var oldKey = MakeKey("old-default");
        var newKey = MakeKey("new-default");
        WriteKeyFile(oldKey);

        var attempts = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            attempts++;
            if (attempts == 1)
            {
                // Simulate the server rotating the default key while the old request is in flight:
                // the on-disk envelope now holds the NEW key for the automatic re-read.
                WriteKeyFile(newKey);
                return JsonResponse(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""");
            }

            return JsonResponse(HttpStatusCode.OK, RouterInfoJson);
        });
        using var client = CreateClient(handler, keyReader: KeyReader());

        var info = await client.GetInfoAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal($"Bearer {oldKey}", handler.Requests[0].Authorization);
        Assert.Equal($"Bearer {newKey}", handler.Requests[1].Authorization);
        Assert.Equal("/api/router/info", handler.Requests[1].PathAndQuery);
        Assert.Equal("2026.11.0", info.Version);
    }

    [Fact]
    public async Task Unauthorized_ReloadReturnsSameKey_RetriesExactlyOnce_ThenSurfaces401()
    {
        WriteKeyFile(MakeKey("same-key"));
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.Unauthorized,
            """{"error":"unauthorized"}"""));
        using var client = CreateClient(handler, keyReader: KeyReader());

        var exception = await Assert.ThrowsAsync<OllamaRouterApiException>(() => client.GetInfoAsync());

        Assert.Equal(2, handler.Requests.Count); // exactly ONE retry — no retry loop
        Assert.Equal($"Bearer {MakeKey("same-key")}", handler.Requests[1].Authorization);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.Status);
        Assert.Equal("unauthorized", exception.Error);
    }

    [Fact]
    public async Task Unauthorized_ExplicitKeyWithoutReader_NoRetry_Surfaces401AfterSingleAttempt()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.Unauthorized,
            """{"error":"unauthorized"}"""));
        using var client = CreateClient(handler, apiKey: "explicit-123");

        var exception = await Assert.ThrowsAsync<OllamaRouterApiException>(() => client.GetInfoAsync());

        Assert.Single(handler.Requests); // no reader -> no automatic re-read -> no retry
        Assert.Equal("Bearer explicit-123", handler.Requests[0].Authorization);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.Status);
    }

    [Fact]
    public async Task RotateDefaultKeyAsync_PostsToRotateRoute_AndParsesNewKeyAndGeneratedAt()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, ApiKeyRotatedJson));
        using var client = CreateClient(handler);

        var rotated = await client.RotateDefaultKeyAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/router/api-keys/default/rotate", request.PathAndQuery);
        Assert.True(rotated.Success);
        Assert.Equal("bmV3LWRlZmF1bHQta2V5", rotated.Key);
        Assert.Equal(GeneratedAt, rotated.GeneratedAt);
    }

    [Fact]
    public async Task CancelledToken_SurfacesOperationCanceledException_WithoutRetry()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, RouterInfoJson));
        using var client = CreateClient(handler, keyReader: KeyReader());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetInfoAsync(cts.Token));

        // No retry: at most the single (immediately cancelled) attempt reaches the handler.
        Assert.True(handler.Requests.Count <= 1, $"expected no retry, got {handler.Requests.Count} requests");
    }

    // ------------------------------------------------- role recommendations (task #66 wire surface)

    [Fact]
    public async Task GetRecommendationsAsync_Keyless_SendsNoAuthorizationHeader_AndRoundTripsMatrix()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, RecommendationsJson));
        using var client = CreateClient(handler, keyReader: KeyReader());

        var matrix = await client.GetRecommendationsAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/router/recommendations", request.PathAndQuery);
        Assert.Null(request.Authorization); // keyless default mode
        Assert.Equal(GeneratedAt, matrix.GeneratedAt);
        Assert.Equal("llama3:8b", matrix.Roles["basic"].Model);
        Assert.Equal(24.5, matrix.Models["llama3:8b"].Nodes[0].TokensPerSecond);
    }

    [Fact]
    public async Task GetRecommendationsAsync_WithExplicitKey_SendsBearerHeader()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, RecommendationsJson));
        using var client = CreateClient(handler, apiKey: "rec-key");

        await client.GetRecommendationsAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer rec-key", request.Authorization);
    }

    [Fact]
    public async Task GenerateRecommendationsAsync_PostsNoBody_And409SurfacesApiExceptionWithParsedError()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.Conflict, """{"error":"no benchmark results available"}"""));
        using var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<OllamaRouterApiException>(() => client.GenerateRecommendationsAsync());

        Assert.Equal(HttpStatusCode.Conflict, ex.Status);
        Assert.Equal("no benchmark results available", ex.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/router/recommendations/generate", request.PathAndQuery);
        Assert.Null(request.Body); // no request body
    }

    [Fact]
    public async Task ApplyRecommendationsAsync_RoundTripsPerTargetMaxContextCeiling()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, RecommendationsAppliedJson));
        using var client = CreateClient(handler);

        var applied = await client.ApplyRecommendationsAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/router/recommendations/apply", request.PathAndQuery);
        Assert.Null(request.Body);
        Assert.True(applied.Applied);
        var target = Assert.Single(applied.Targets);
        Assert.Equal("Local", target.Target);
        Assert.Equal(8192, target.MaxContextCeiling);
    }

    // ------------------------------------------------- 33-method route-table round-trip

    [Theory]
    [InlineData("GetInfoAsync")]
    [InlineData("GetMonitorStateAsync")]
    [InlineData("UpdateTargetAvailabilityAsync")]
    [InlineData("GetFleetAsync")]
    [InlineData("GetFleetHardwareAsync")]
    [InlineData("UpdateTargetHardwareAsync")]
    [InlineData("GetCartographyAsync")]
    [InlineData("GetModelsAsync")]
    [InlineData("GetModelDetailAsync")]
    [InlineData("PreloadModelAsync")]
    [InlineData("UninstallModelAsync")]
    [InlineData("PreviewPreloadAsync")]
    [InlineData("ReclaimVramAsync")]
    [InlineData("GetConfigAsync")]
    [InlineData("UpdateConfigAsync")]
    [InlineData("AddTargetAsync")]
    [InlineData("UpdateTargetAsync")]
    [InlineData("DeleteTargetAsync")]
    [InlineData("ResetTargetStreakAsync")]
    [InlineData("ResetFleetStreaksAsync")]
    [InlineData("StartBenchmarkAsync")]
    [InlineData("GetBenchmarkResultsAsync")]
    [InlineData("ClearBenchmarkResultsAsync")]
    [InlineData("GetSessionsAsync")]
    [InlineData("GetSessionDetailAsync")]
    [InlineData("ReleaseSessionAffinityAsync")]
    [InlineData("GetApiKeysAsync")]
    [InlineData("CreateApiKeyAsync")]
    [InlineData("DeleteApiKeyAsync")]
    [InlineData("RotateDefaultKeyAsync")]
    [InlineData("ShutdownAsync")]
    [InlineData("GetRecommendationsAsync")]
    [InlineData("GenerateRecommendationsAsync")]
    [InlineData("ApplyRecommendationsAsync")]
    public async Task RouteTable_EveryMethod_RoundTripsRequestSerializationAndResponseParsing(string method)
    {
        var route = RouteCases[method];
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(route.Status, route.ResponseJson));
        WriteKeyFile(RouteTableKey);
        using var client = CreateClient(handler, keyReader: KeyReader());

        var result = await route.Invoke(client);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(route.HttpMethod, request.Method);
        Assert.Equal(route.PathAndQuery, request.PathAndQuery);
        Assert.Equal($"Bearer {RouteTableKey}", request.Authorization);
        if (route.ExpectedBodyJson is not null)
        {
            Assert.Equal("application/json", request.ContentType);
            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(request.Body!), JsonNode.Parse(route.ExpectedBodyJson)),
                $"[{method}] unexpected request body: {request.Body}");
        }
        else
        {
            Assert.Null(request.Body);
        }

        Assert.Equal(route.ExpectedSessionHeader, request.SessionId);
        route.Validate(result);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record RouteCase(
        string HttpMethod,
        string PathAndQuery,
        string? ExpectedBodyJson,
        string? ExpectedSessionHeader,
        HttpStatusCode Status,
        string ResponseJson,
        Func<OllamaRouterApiClient, Task<object?>> Invoke,
        Action<object?> Validate);

    private static readonly IReadOnlyDictionary<string, RouteCase> RouteCases = BuildRouteTable();

    private static Dictionary<string, RouteCase> BuildRouteTable() => new()
    {
        ["GetInfoAsync"] = new(
            "GET", "/api/router/info", null, null, HttpStatusCode.OK, RouterInfoJson,
            async client => await client.GetInfoAsync(),
            result =>
            {
                var r = (RouterInfoResponse)result!;
                Assert.Equal("2026.11.0", r.Version);
                Assert.True(r.IsRouter);
            }),

        ["GetMonitorStateAsync"] = new(
            "GET", "/monitor/api", null, null, HttpStatusCode.OK, MonitorStateJson,
            async client => await client.GetMonitorStateAsync(),
            result =>
            {
                var r = (MonitorStateResponse)result!;
                Assert.Equal(2, r.Fleet.Count);
                var resident = r.Fleet[1].ResidentModels;
                Assert.NotNull(resident);
                Assert.Empty(resident); // always-array read-path contract
                Assert.Empty(r.Statistics.Last7Days);
                Assert.Equal(1.2, r.Cost!.ElectricityCost);
            }),

        ["UpdateTargetAvailabilityAsync"] = new(
            "POST", "/targets", """{"Local":true,"Remote":false,"Cloud":true}""", null, HttpStatusCode.OK,
            """{"Local":true,"Remote":false,"Cloud":true}""",
            async client => await client.UpdateTargetAvailabilityAsync(
                new Dictionary<string, bool> { ["Local"] = true, ["Remote"] = false, ["Cloud"] = true }),
            result =>
            {
                var r = (IReadOnlyDictionary<string, bool>)result!;
                Assert.Equal(3, r.Count);
                Assert.True(r["Local"]);
                Assert.False(r["Remote"]);
                Assert.True(r["Cloud"]);
            }),

        ["GetFleetAsync"] = new(
            "GET", "/api/router/fleet", null, null, HttpStatusCode.OK, FleetJson,
            async client => await client.GetFleetAsync(),
            result =>
            {
                var r = (RouterFleetResponse)result!;
                Assert.Single(r.Targets);
                Assert.Equal(900, r.Targets[0].Preload!.KeepAliveSeconds);
                Assert.Equal(["llama3:8b"], r.Targets[0].Execution!.ResidentModels);
            }),

        ["GetFleetHardwareAsync"] = new(
            "GET", "/api/router/fleet/hardware", null, null, HttpStatusCode.OK, FleetHardwareJson,
            async client => await client.GetFleetHardwareAsync(),
            result =>
            {
                var r = (RouterFleetHardwareResponse)result!;
                Assert.Single(r.Nodes);
                Assert.True(r.Nodes[0].Reachable);
                Assert.Equal(SystemRamType.Ddr5, r.Nodes[0].Profile!.SystemRamType);
            }),

        ["UpdateTargetHardwareAsync"] = new(
            "PUT", "/api/router/fleet/hardware/Desktop", HardwareProfileJson, null, HttpStatusCode.OK,
            HardwareProfileJson,
            async client => await client.UpdateTargetHardwareAsync("Desktop", SharedHardwareProfile),
            result =>
            {
                var r = (NodeHardwareProfile)result!;
                Assert.Equal("desktop", r.NodeName);
                Assert.Equal(24564, r.Gpus[0].VramMB);
            }),

        ["GetCartographyAsync"] = new(
            "GET", "/api/router/cartography", null, null, HttpStatusCode.OK, CartographyJson,
            async client => await client.GetCartographyAsync(),
            result =>
            {
                var r = (RouterCartographyResponse)result!;
                Assert.Single(r.Nodes);
                Assert.Single(r.Models);
                Assert.Equal("q8_0", r.Models[0].Deployments[0].Quantization);
                Assert.Single(r.LoadedModels);
            }),

        ["GetModelsAsync"] = new(
            "GET", "/api/router/models", null, null, HttpStatusCode.OK, ModelsJson,
            async client => await client.GetModelsAsync(),
            result =>
            {
                var r = (RouterModelsResponse)result!;
                Assert.Single(r.Models);
                Assert.Equal(8030000000, r.Models[0].ParameterCount);
            }),

        ["GetModelDetailAsync"] = new(
            "GET", "/api/router/models/org/model%3Atag", null, null, HttpStatusCode.OK, ModelDetailJson,
            async client => await client.GetModelDetailAsync("org/model:tag"),
            result =>
            {
                var r = (RouterModelDetailResponse)result!;
                Assert.Equal("org/model:tag", r.Name);
                Assert.Equal("insufficient VRAM", r.Targets[0].VetoReason);
            }),

        ["PreloadModelAsync"] = new(
            "POST", "/api/router/models/org/model%3Atag/preload",
            """{"keepAliveSeconds":900,"contextIntentTokens":8192,"target":"Desktop"}""",
            null, HttpStatusCode.OK, PreloadJson,
            async client => await client.PreloadModelAsync("org/model:tag",
                new RouterModelPreloadRequest(KeepAliveSeconds: 900, ContextIntentTokens: 8192, Target: "Desktop")),
            result =>
            {
                var r = (RouterModelPreloadResponse)result!;
                Assert.Equal("Desktop", r.SelectedTarget);
                Assert.True(r.LoadAccepted);
                Assert.Single(r.Candidates);
            }),

        ["UninstallModelAsync"] = new(
            "POST", "/api/router/models/uninstall",
            """{"model":"org/model:tag","targets":["Desktop","Server"]}""",
            null, HttpStatusCode.OK, UninstallJson,
            async client => await client.UninstallModelAsync("org/model:tag", ["Desktop", "Server"]),
            result =>
            {
                var r = (RouterModelUninstallResponse)result!;
                Assert.Equal("org/model:tag", r.Model);
                Assert.Equal(2, r.Targets.Count);
                Assert.True(r.Targets[0].Success);
                Assert.False(r.Targets[1].Success);
                Assert.Equal("model 'org/model:tag' not found", r.Targets[1].Error);
            }),

        ["PreviewPreloadAsync"] = new(
            "GET", "/api/router/preload/preview?model=org/model%3Atag&keepAliveSeconds=900"
                       + "&contextIntentTokens=4096&target=Desktop",
            null, null, HttpStatusCode.OK, PreviewJson,
            async client => await client.PreviewPreloadAsync("org/model:tag",
                keepAliveSeconds: 900, contextIntentTokens: 4096, target: "Desktop"),
            result =>
            {
                var r = (RouterModelPreloadResponse)result!;
                Assert.Null(r.LoadAccepted);
                Assert.Equal("busy", r.Candidates[0].SkipReason);
            }),

        ["ReclaimVramAsync"] = new(
            "POST", "/api/router/vram/reclaim",
            """{"target":"Desktop","location":null,"disableTarget":false}""",
            null, HttpStatusCode.OK, VramReclaimJson,
            async client => await client.ReclaimVramAsync(new RouterVramReclaimRequest(Target: "Desktop")),
            result =>
            {
                var r = (RouterVramReclaimResponse)result!;
                Assert.True(r.Success);
                Assert.Equal(2, r.TotalUnloaded);
            }),

        ["GetConfigAsync"] = new(
            "GET", "/api/router/config", null, null, HttpStatusCode.OK, ConfigJson,
            async client => await client.GetConfigAsync(),
            result =>
            {
                var r = (RouterConfigResponse)result!;
                Assert.Single(r.Targets);
                Assert.Equal(72, r.ApiSecurity!.DefaultKeyRotationHours);
                Assert.Equal(0.2, r.Pricing!.ElectricityPricePerKwh);
            }),

        ["UpdateConfigAsync"] = new(
            "PUT", "/api/router/config",
            """
            {"cloud":null,"dynamicContext":null,"recording":null,"resilience":null,"pricing":{
             "promptPricePerMillion":0.3,"completionPricePerMillion":1.2,"pricePerMillion":null,
             "currency":"$","cachedPromptPricePerMillion":null,"electricityPricePerKwh":null,
             "defaultNodePowerWatts":null}}
            """,
            null, HttpStatusCode.OK, UpdateConfigResponseJson,
            async client => await client.UpdateConfigAsync(new UpdateRouterConfigRequest(
                Pricing: new RouterPricingConfigDto(PromptPricePerMillion: 0.3, CompletionPricePerMillion: 1.2, PricePerMillion: null))),
            result =>
            {
                var r = (UpdateRouterConfigResponse)result!;
                Assert.True(r.Success);
                Assert.Null(r.Config);
            }),

        ["AddTargetAsync"] = new(
            "POST", "/api/router/targets", AddTargetBodyJson, null, HttpStatusCode.Created, AddTargetResponseJson,
            async client => await client.AddTargetAsync(new AddTargetRequest(
                "Edge", "http://edge:11434", MaxParallel: 2, TestConnectionFirst: true,
                HardwareHint: "RTX 4090", Type: "ollama", Provider: "LocalLab",
                Models: [new TargetModelDto("llama3:8b", ContextLength: 8192, Quantization: "q8_0")],
                PowerWatts: 95.0)),
            result =>
            {
                var r = (AddTargetResponse)result!;
                Assert.True(r.Success);
                Assert.Equal("Online", r.Liveness);
            }),

        ["UpdateTargetAsync"] = new(
            "PUT", "/api/router/targets/Edge",
            """
            {"url":"http://edge:11434","enabled":false,"hardwareHint":null,"maxParallel":4,
             "type":null,"apiKey":null,"provider":null,"models":null,"powerWatts":80}
            """,
            null, HttpStatusCode.OK, UpdateTargetResponseJson,
            async client => await client.UpdateTargetAsync("Edge",
                new UpdateTargetRequest(Url: "http://edge:11434", Enabled: false, MaxParallel: 4, PowerWatts: 80.0)),
            result =>
            {
                var r = (UpdateTargetResponse)result!;
                Assert.True(r.Success);
                Assert.Null(r.Target);
            }),

        ["DeleteTargetAsync"] = new(
            "DELETE", "/api/router/targets/Edge", null, null, HttpStatusCode.OK, DeleteTargetResponseJson,
            async client => await client.DeleteTargetAsync("Edge"),
            result =>
            {
                var r = (DeleteTargetResponse)result!;
                Assert.True(r.Success);
                Assert.Equal("target deleted: Edge", r.Message);
            }),

        ["ResetTargetStreakAsync"] = new(
            "POST", "/api/router/targets/Edge/reset-streak", null, null, HttpStatusCode.OK,
            ResetTargetStreakResponseJson,
            async client => await client.ResetTargetStreakAsync("Edge"),
            result =>
            {
                var r = (ResetTargetStreakResponse)result!;
                Assert.True(r.Success);
                Assert.Equal("Edge", r.Target);
                Assert.Equal(0, r.Streak);
                Assert.Equal("Closed", r.Status);
            }),

        ["ResetFleetStreaksAsync"] = new(
            "POST", "/api/router/fleet/reset-streaks", null, null, HttpStatusCode.OK,
            ResetFleetStreaksResponseJson,
            async client => await client.ResetFleetStreaksAsync(),
            result =>
            {
                var r = (ResetFleetStreaksResponse)result!;
                Assert.True(r.Success);
                Assert.Equal(1, r.ResetCount);
                Assert.Equal(2, r.Targets!.Count);
                Assert.Equal(4, r.Targets[0].PreviousStreak);
                Assert.True(r.Targets[0].Reset);
                Assert.Equal(0, r.Targets[1].PreviousStreak);
                Assert.False(r.Targets[1].Reset);
            }),

        ["StartBenchmarkAsync"] = new(
            "POST", "/api/router/benchmark",
            """{"target":"Desktop","roles":["AgenticChat"],"model":"llama3:8b"}""",
            null, (HttpStatusCode)202, BenchmarkRunJson,
            async client => await client.StartBenchmarkAsync(
                new RouterBenchmarkRequest(Target: "Desktop", Roles: [UsageRole.AgenticChat], Model: "llama3:8b")),
            result =>
            {
                var r = (RouterBenchmarkRunResponse)result!;
                Assert.True(r.Started);
                Assert.Equal(["Desktop", "Server"], r.QueuedTargets);
            }),

        ["GetBenchmarkResultsAsync"] = new(
            "GET", "/api/router/benchmark/results", null, null, HttpStatusCode.OK, BenchmarkResultsJson,
            async client => await client.GetBenchmarkResultsAsync(),
            result =>
            {
                var r = (RouterBenchmarkResultsResponse)result!;
                Assert.Equal(BenchmarkRunStatus.Succeeded, r.Targets[0].Status);
                Assert.Equal(UsageRole.AgenticChat, r.Targets[0].Results[0].Role);
                Assert.Equal(32.8, r.Targets[0].Results[0].DecodeTokensPerSecond);
            }),

        ["ClearBenchmarkResultsAsync"] = new(
            "DELETE", "/api/router/benchmark/results", null, null, HttpStatusCode.OK, BenchmarkClearedJson,
            async client => await client.ClearBenchmarkResultsAsync(),
            result => Assert.True(((RouterBenchmarkClearedResponse)result!).Cleared)),

        ["GetSessionsAsync"] = new(
            "GET", "/api/router/monitor/sessions", null, null, HttpStatusCode.OK, SessionsJson,
            async client => await client.GetSessionsAsync(),
            result =>
            {
                var r = (RouterSessionUsageListResponse)result!;
                Assert.Equal(1, r.Count);
                Assert.Equal("sess-1", r.Sessions[0].SessionId);
                Assert.Equal(0.006, r.Sessions[0].Cost);
            }),

        ["GetSessionDetailAsync"] = new(
            "GET", "/api/router/monitor/sessions/sess-123", null, null, HttpStatusCode.OK, SessionDetailJson,
            async client => await client.GetSessionDetailAsync("sess-123"),
            result =>
            {
                var r = (RouterSessionUsageDetailResponse)result!;
                Assert.Equal("sess-123", r.SessionId);
                Assert.Equal("Desktop", r.PerTarget[0].Target);
            }),

        ["ReleaseSessionAffinityAsync"] = new(
            "POST", "/api/router/sessions/affinity/release", null, "sess-123", HttpStatusCode.OK,
            AffinityReleaseJson,
            async client => await client.ReleaseSessionAffinityAsync("sess-123"),
            result =>
            {
                var r = (RouterSessionAffinityReleaseResponse)result!;
                Assert.True(r.Released);
                Assert.Equal("sess-123", r.SessionId);
            }),

        ["GetApiKeysAsync"] = new(
            "GET", "/api/router/api-keys", null, null, HttpStatusCode.OK, ApiKeysListJson,
            async client => await client.GetApiKeysAsync(),
            result =>
            {
                var r = (ApiKeysListResponse)result!;
                Assert.Equal("cli", r.Keys[0].Name);
                Assert.Null(r.Keys[0].ExpiresAt);
            }),

        ["CreateApiKeyAsync"] = new(
            "POST", "/api/router/api-keys", """{"name":"cli","validFor":168}""", null,
            HttpStatusCode.Created, ApiKeyCreatedJson,
            async client => await client.CreateApiKeyAsync(new ApiKeyCreateRequest("cli", ValidFor: 168)),
            result =>
            {
                var r = (ApiKeyCreatedResponse)result!;
                Assert.Equal("YWJjZGVmZ2g", r.Key);
                Assert.Equal(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero), r.ExpiresAt);
            }),

        ["DeleteApiKeyAsync"] = new(
            "DELETE", "/api/router/api-keys/cli", null, null, HttpStatusCode.OK, ApiKeyDeletedJson,
            async client => await client.DeleteApiKeyAsync("cli"),
            result =>
            {
                var r = (ApiKeyDeletedResponse)result!;
                Assert.True(r.Success);
                Assert.Null(r.Error);
            }),

        ["RotateDefaultKeyAsync"] = new(
            "POST", "/api/router/api-keys/default/rotate", null, null, HttpStatusCode.OK, ApiKeyRotatedJson,
            async client => await client.RotateDefaultKeyAsync(),
            result =>
            {
                var r = (ApiKeyRotatedResponse)result!;
                Assert.Equal("bmV3LWRlZmF1bHQta2V5", r.Key);
                Assert.Equal(GeneratedAt, r.GeneratedAt);
            }),

        ["ShutdownAsync"] = new(
            "POST", "/api/router/admin/shutdown", null, null, (HttpStatusCode)202, RouterShutdownResponseJson,
            async client => await client.ShutdownAsync(),
            result =>
            {
                var r = (RouterShutdownResponse)result!;
                Assert.True(r.Stopping);
                Assert.Equal(4242, r.ProcessId);
            }),

        ["GetRecommendationsAsync"] = new(
            "GET", "/api/router/recommendations", null, null, HttpStatusCode.OK, RecommendationsJson,
            async client => await client.GetRecommendationsAsync(),
            result =>
            {
                var r = (FleetRecommendations)result!;
                Assert.Equal(GeneratedAt, r.GeneratedAt);
                Assert.True(r.Roles.ContainsKey("basic"));
                Assert.Equal(4096, r.Roles["basic"].RecommendedContextWindow);
                Assert.Equal(8192, r.Models["llama3:8b"].NativeContext);
            }),

        ["GenerateRecommendationsAsync"] = new(
            "POST", "/api/router/recommendations/generate", null, null, HttpStatusCode.OK, RecommendationsGeneratedJson,
            async client => await client.GenerateRecommendationsAsync(),
            result =>
            {
                var r = (RouterRecommendationsGeneratedResponse)result!;
                Assert.True(r.Generated);
                Assert.Equal(1, r.RoleCount);
                Assert.Equal(1, r.ModelCount);
                Assert.Equal(["Local"], r.Targets);
                Assert.True(r.Recommendations.Roles.ContainsKey("basic"));
            }),

        ["ApplyRecommendationsAsync"] = new(
            "POST", "/api/router/recommendations/apply", null, null, HttpStatusCode.OK, RecommendationsAppliedJson,
            async client => await client.ApplyRecommendationsAsync(),
            result =>
            {
                var r = (RouterRecommendationsAppliedResponse)result!;
                Assert.True(r.Applied);
                var target = Assert.Single(r.Targets);
                Assert.Equal("Local", target.Target);
                Assert.Equal(8192, target.MaxContextCeiling);
            }),
    };

    // ------------------------------------------------------------------ canned JSON

    private const string RouterInfoJson = """
        {"version":"2026.11.0","isRouter":true,
         "fleet":{"totalTargets":3,"onlineTargets":2,"cloudEnabled":true},
         "features":{"dynamicContext":true,"autoShrink":false,"failover":true,"antiThrashingAffinity":true},
         "processId":1234,"startedAtUtc":"2026-11-10T12:00:00.000Z","uptimeSeconds":3600.5}
        """;

    private const string RouterShutdownResponseJson =
        """{"stopping":true,"processId":4242}""";

    private const string MonitorStateJson = """
        {
         "cloudEnabled": true,
         "fleet": [
          {
           "name": "Desktop", "enabled": true, "liveness": "Online", "inFlightRequests": 2,
           "contextAllocation": "baseline", "location": "local",
           "url": "http://127.0.0.1:11434", "activeModel": "llama3:8b",
           "hardwareProfile": "NVIDIA RTX 4090",
           "residentModels": ["llama3:8b", "qwen:2b"],
           "keepAliveSeconds": 900, "keepAliveSource": "class", "activeKeepAliveSeconds": -1
          },
          {
           "name": "Cloud", "enabled": true, "liveness": "Online", "inFlightRequests": 0,
           "contextAllocation": "baseline", "location": "cloud",
           "residentModels": null
          }
         ],
         "statistics": {
          "today": {
           "total": {"requests": 5, "inputTokens": 1000, "outputTokens": 200, "cachedInputTokens": 50, "elapsedMs": 250.5}
          },
          "last7Days": {}
         },
         "inProgress": [],
         "requests": [
          {
           "timestamp": "2026-11-10T12:00:00.000Z", "target": "Desktop", "model": "llama3:8b",
           "estimatedPromptTokens": 1000, "actualPromptTokens": 950, "actualResponseTokens": 200,
           "elapsedMs": 250.5, "statusCode": 200, "success": true, "sessionId": "sess-1",
           "cachedInputTokens": 50
          }
         ],
         "cost": {
          "currency": "$", "estimatedSavings": 12.5, "cloudOverflowCost": 0,
          "perTargetSavings": {"Desktop": 12.5}, "electricityCost": 1.2,
          "perTargetElectricityCost": {"Desktop": 1.2}
         },
         "overhead": {
          "configuredFactor": 1.2, "estimator": "token-estimate", "overallObservedFactor": 1.15,
          "sampleCount": 40, "models": []
         }
        }
        """;

    private const string FleetJson = """
        {"targets":[{
         "name":"Desktop","url":"http://127.0.0.1:11434","enabled":true,"liveness":"Online","latencyMs":4,
         "location":"local","preload":{"keepAliveSeconds":900,"keepAliveSource":"class"},
         "hardware":{"gpuVendor":"NVIDIA","devices":["RTX 4090"],"maxVramMB":24564,"maxParallel":4,
          "speedFactor":1.5,"isUnifiedMemory":false,"systemRamMB":32768,"memoryType":"Ddr5",
          "bandwidthGBs":1.8,"gpuName":"RTX 4090"},
         "execution":{"inFlightRequests":2,"activeModel":"llama3:8b","contextAllocation":"baseline",
          "activeSessions":1,"residentModels":["llama3:8b"],"activeKeepAliveSeconds":-1}}]}
        """;

    private const string FleetHardwareJson = """
        {"nodes":[{
         "target":"Desktop","source":"manual","reachable":true,
         "profile":{"nodeName":"desktop","os":"windows","architecture":"x64","isUnifiedMemory":false,
          "systemRamMB":32768,"systemRamType":"Ddr5",
          "gpus":[{"vendor":"NVIDIA","modelName":"RTX 4090","vramMB":24564,"vramType":"GDDR6X",
           "busWidthBits":384,"bandwidthGBs":1008}],
          "totalFastMemoryMB":24564,"effectiveBandwidthGBs":1008}}]}
        """;

    private const string HardwareProfileJson = """
        {"nodeName":"desktop","os":"windows","architecture":"x64","isUnifiedMemory":false,
         "systemRamMB":32768,"systemRamType":"Ddr5",
         "gpus":[{"vendor":"NVIDIA","modelName":"RTX 4090","vramMB":24564,"vramType":"GDDR6X",
          "busWidthBits":384,"bandwidthGBs":1008}],
         "totalFastMemoryMB":24564,"effectiveBandwidthGBs":1008}
        """;

    private const string CartographyJson = """
        {"nodes":[{
         "name":"Desktop","location":"local","enabled":true,"liveness":"Online","stale":false,
         "contextAllocation":"baseline","inFlightRequests":1,"totalLoadedVramBytes":1073741824,
         "loadedModels":[{"name":"llama3:8b","sizeVramBytes":5368709120,"quantization":"q8_0","contextLength":8192}],
         "url":"http://127.0.0.1:11434","hardware":{"isUnifiedMemory":false,"gpuName":"RTX 4090",
          "gpuVendor":"NVIDIA","totalVramMB":24564}}],
         "models":[{
          "name":"llama3:8b","architecture":"Dense","weightSizeBytes":4931000000,
          "nativeContextWindow":4096,"recommendedContextWindow":8192,"cloudOverflowAvailable":true,
          "deployments":[{"target":"Desktop","present":true,"capable":true,"contextCeiling":8192,
           "isResident":true,"inFlightRequests":1,"loadedSizeVramBytes":5368709120,"quantization":"q8_0"}]}],
         "loadedModels":[{
          "name":"llama3:8b","target":"Desktop","sizeVramBytes":5368709120,
          "contextAllocation":"baseline","liveness":"Online","stale":false,
          "quantization":"q8_0","contextLength":8192}]}
        """;

    private const string ModelsJson = """
        {"models":[{"name":"llama3:8b","architecture":"Dense","parameterCount":8030000000,
         "nativeContextWindow":4096,"recommendedContextWindow":8192,"cloudOverflowAvailable":true}]}
        """;

    private const string ModelDetailJson = """
        {"name":"org/model:tag","architecture":"Moe","parameterCount":305400000000,
         "nativeContextWindow":131072,"recommendedContextWindow":65536,
         "targets":[{"target":"Desktop","capable":false,"contextCeiling":8192,"isResident":false,
          "inFlightRequests":0,"vetoReason":"insufficient VRAM"}]}
        """;

    private const string PreloadJson = """
        {"model":"org/model:tag","selectedTarget":"Desktop","keepAliveSeconds":-1,"numCtx":8192,
         "contextIntentTokens":8192,"loadAccepted":true,"residencyConfirmed":true,"preloadDurationMs":1520,
         "candidates":[{"target":"Desktop","eligible":true,"inFlightRequests":0,"isResident":false,
          "contextCeiling":8192,"serverClass":false,"busy":false}],
         "reason":"auto-select"}
        """;

    private const string PreviewJson = """
        {"model":"org/model:tag","selectedTarget":"Desktop","keepAliveSeconds":900,"numCtx":4096,
         "contextIntentTokens":4096,"loadAccepted":null,"residencyConfirmed":null,"preloadDurationMs":null,
         "candidates":[{"target":"Desktop","eligible":false,"inFlightRequests":2,"isResident":true,
          "contextCeiling":8192,"serverClass":false,"busy":true,"skipReason":"busy"}],
         "reason":"preview-only"}
        """;

    private const string UninstallJson = """
        {"model":"org/model:tag",
         "targets":[{"target":"Desktop","success":true},
          {"target":"Server","success":false,"error":"model 'org/model:tag' not found"}],
         "message":null}
        """;

    private const string VramReclaimJson = """
        {"success":true,"totalUnloaded":2,
         "targets":[{"target":"Desktop","unloadedModels":["llama3:8b","qwen:2b"],"disabled":false}],
         "message":null}
        """;

    private const string ConfigJson = """
        {"targets":[{
         "name":"Desktop","url":"http://127.0.0.1:11434","enabled":true,"hardwareHint":"RTX 4090",
         "maxParallel":4,"type":"ollama","provider":null,
         "models":[{"name":"llama3:8b","contextLength":8192,"quantization":"q8_0"}],
         "apiKeyConfigured":false,"powerWatts":95}],
         "cloud":{"enabled":true,"defaultModel":"llama3:8b","preferredRelayTarget":"Desktop","overflowConfigured":true},
         "dynamicContext":{"enabled":true,"denseClampEnabled":true,"autoShrinkCooldownTurns":3,
          "autoShrinkIdleTimeoutSeconds":600},
         "recording":{"enabled":false,"outputDirectory":null,"maxFiles":500},
         "resilience":{"maxConsecutiveFailures":3,"consecutiveFailuresTokenTolerance":4000},
         "pricing":{"promptPricePerMillion":0.3,"completionPricePerMillion":1.2,"pricePerMillion":null,
          "currency":"$","cachedPromptPricePerMillion":0.03,"electricityPricePerKwh":0.2,"defaultNodePowerWatts":150},
         "apiSecurity":{"requireKeyForAllEndpoints":true,"defaultKeyRotationHours":72}}
        """;

    private const string UpdateConfigResponseJson =
        """{"success":true,"message":"configuration updated","config":null}""";

    private const string AddTargetBodyJson = """
        {"name":"Edge","url":"http://edge:11434","maxParallel":2,"testConnectionFirst":true,
         "hardwareHint":"RTX 4090","type":"ollama","apiKey":null,"provider":"LocalLab",
         "models":[{"name":"llama3:8b","contextLength":8192,"quantization":"q8_0"}],"powerWatts":95}
        """;

    private const string AddTargetResponseJson = """
        {"success":true,"connectionTested":true,"liveness":"Online","modelsDiscovered":2,
         "target":{"name":"Edge","url":"http://edge:11434","enabled":true,"hardwareHint":"RTX 4090",
          "maxParallel":2,"type":"ollama","provider":"LocalLab",
          "models":[{"name":"llama3:8b","contextLength":8192,"quantization":"q8_0"}],
          "apiKeyConfigured":false,"powerWatts":95},
         "latencyMs":12,"error":null}
        """;

    private const string UpdateTargetResponseJson =
        """{"success":true,"message":"target updated","target":null}""";

    private const string DeleteTargetResponseJson =
        """{"success":true,"message":"target deleted: Edge"}""";

    private const string ResetTargetStreakResponseJson =
        """{"success":true,"target":"Edge","streak":0,"status":"Closed"}""";

    private const string ResetFleetStreaksResponseJson =
        """
        {"success":true,"resetCount":1,
         "targets":[{"target":"Edge","previousStreak":4,"reset":true},
                     {"target":"Local","previousStreak":0,"reset":false}]}
        """;

    private const string BenchmarkRunJson =
        """{"started":true,"queuedTargets":["Desktop","Server"]}""";

    private const string BenchmarkResultsJson = """
        {"targets":[{"target":"Desktop","status":"Succeeded",
         "startedAt":"2026-11-10T12:00:00.000Z","finishedAt":"2026-11-10T12:05:00.000Z",
         "results":[{"model":"llama3:8b","role":"AgenticChat","numCtx":8192,"evalCount":2000,
          "evalDurationNs":61000000000,"decodeTokensPerSecond":32.8,"prefillTokensPerSecond":910.5,
          "viable":true,"reason":null}]}]}
        """;

    private const string BenchmarkClearedJson = """{"cleared":true}""";

    private const string RecommendationsJson = """
        {"generatedAt":"2026-11-10T12:00:00.000Z",
         "roles":{"basic":{"role":"basic","model":"llama3:8b","recommendedContextWindow":4096,
          "description":"smallest fast general model","nodes":[{"target":"Local","contextCeiling":4096,
           "baselineContext":2048,"tokensPerSecond":24.5,"vramMB":8192}]}},
         "models":{"llama3:8b":{"model":"llama3:8b","architecture":"llama","nativeContext":8192,
          "quantization":"q8_0","nodes":[{"target":"Local","contextCeiling":4096,
           "baselineContext":2048,"tokensPerSecond":24.5,"vramMB":8192}]}}}
        """;

    private const string RecommendationsGeneratedJson = """
        {"generated":true,"generatedAt":"2026-11-10T12:00:00.000Z","roleCount":1,"modelCount":1,
         "targets":["Local"],
         "recommendations":{"generatedAt":"2026-11-10T12:00:00.000Z",
          "roles":{"basic":{"role":"basic","model":"llama3:8b","recommendedContextWindow":4096,
           "description":"smallest fast general model","nodes":[]}},
          "models":{"llama3:8b":{"model":"llama3:8b","architecture":"llama","nativeContext":8192,
           "quantization":"q8_0","nodes":[]}}}}
        """;

    private const string RecommendationsAppliedJson = """
        {"applied":true,"generatedAt":"2026-11-10T12:00:00.000Z",
         "targets":[{"target":"Local","models":2,"maxContextCeiling":8192}]}
        """;

    private const string SessionsJson = """
        {"currency":"$","count":1,
         "sessions":[{"sessionId":"sess-1","requests":4,"inputTokens":2000,"cachedInputTokens":500,
          "outputTokens":800,"elapsedMs":1200.25,"lastSeenAt":"2026-11-10T12:30:00.000Z",
          "cloudValue":0.012,"electricityCost":0.004,"savings":0.008,"actualCloudCost":0.002,"cost":0.006}]}
        """;

    private const string SessionDetailJson = """
        {"currency":"$","sessionId":"sess-123","requests":4,"inputTokens":2000,"cachedInputTokens":500,
         "outputTokens":800,"elapsedMs":1200.25,"lastSeenAt":"2026-11-10T12:30:00.000Z",
         "cloudValue":0.012,"electricityCost":0.004,"savings":0.008,"actualCloudCost":0.002,"cost":0.006,
         "perTarget":[{"target":"Desktop","requests":4,"inputTokens":2000,"cachedInputTokens":500,
          "outputTokens":800,"elapsedMs":1200.25,"cloudValue":0.012,"electricityCost":0.004,"savings":0.008}]}
        """;

    private const string AffinityReleaseJson =
        """{"sessionId":"sess-123","released":true}""";

    private const string ApiKeysListJson =
        """{"keys":[{"name":"cli","createdAt":"2026-11-01T09:00:00.000Z","expiresAt":null}]}""";

    private const string ApiKeyCreatedJson = """
        {"success":true,"name":"cli","key":"YWJjZGVmZ2g","createdAt":"2026-11-10T12:00:00.000Z",
         "expiresAt":"2026-11-17T12:00:00.000Z"}
        """;

    private const string ApiKeyDeletedJson = """{"success":true,"error":null}""";

    private const string ApiKeyRotatedJson =
        """{"success":true,"key":"bmV3LWRlZmF1bHQta2V5","generatedAt":"2026-11-10T12:00:00.000Z"}""";

    private static NodeHardwareProfile SharedHardwareProfile => new(
        "desktop", "windows", "x64", IsUnifiedMemory: false, SystemRamMB: 32768,
        SystemRamType.Ddr5,
        Gpus: [new GpuDeviceProfile("NVIDIA", "RTX 4090", 24564, "GDDR6X", 384, 1008.0)],
        TotalFastMemoryMB: 24564, EffectiveBandwidthGBs: 1008.0);

    private void WriteKeyFile(string key)
    {
        Directory.CreateDirectory(_configDir);
        var envelope = $"{{\"key\":\"{key}\",\"generatedAt\":\"{GeneratedAt:O}\"}}";
        // PlainKeyProtector is the identity protector, so the on-disk blob IS the envelope.
        File.WriteAllText(Path.Combine(_configDir, "default-key.dat"), envelope);
    }

    private IDefaultKeyReader KeyReader() => OllamaRouterDefaultKeyReader.Create(_configDir, new PlainKeyProtector(), _ => null);

    private static OllamaRouterApiClient CreateClient(
        FakeHttpMessageHandler handler, string? apiKey = null, IDefaultKeyReader? keyReader = null) =>
        new(BaseUrl, apiKey, keyReader, new HttpClient(handler));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string MakeKey(string label) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(label))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ------------------------------------------------------------------
    //  PullModelAsync (task #125): the streaming NDJSON pull core
    // ------------------------------------------------------------------

    [Fact]
    public async Task PullModelAsync_HappyPath_SendsExactRequest_ReportsProgressInOrder_ReturnsSuccessResult()
    {
        var ndjson = string.Join('\n',
            """{"event":"started","target":"Local","model":"llama3.2:13b-q4_K_M"}""",
            """{"event":"progress","target":"Local","status":"pulling 34%","digest":"sha256:abc","totalBytes":100,"completedBytes":34}""",
            """{"event":"progress","target":"Local","status":"pulling 100%","totalBytes":100,"completedBytes":100}""",
            """{"event":"alias_started","target":"Local","alias":"llama3.2:13b-q4_K_M-8k","context":8192}""",
            """{"event":"alias_created","target":"Local","alias":"llama3.2:13b-q4_K_M-8k","context":8192}""",
            """{"event":"success","target":"Local","model":"llama3.2:13b-q4_K_M","alias":"llama3.2:13b-q4_K_M-8k","durationMs":42133}""");
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjson, Encoding.UTF8, "text/x-ndjson")
        });
        using var client = CreateClient(handler);
        var progress = new List<OllamaRouterModelPullProgress>();

        var result = await client.PullModelAsync(
            "llama3.2:13b-q4_K_M", "Local", 8192, new Progress<OllamaRouterModelPullProgress>(progress.Add));

        // The exact wire request (camelCase, all three fields).
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/router/models/pull", request.PathAndQuery);
        var body = (JsonObject)JsonNode.Parse(request.Body!)!;
        Assert.Equal("llama3.2:13b-q4_K_M", body["model"]!.GetValue<string>());
        Assert.Equal("Local", body["target"]!.GetValue<string>());
        Assert.Equal(8192, body["aliasContext"]!.GetValue<int>());

        // The progress callbacks, in stream order (pull lines, then the alias lifecycle).
        Assert.Equal(
            [
                new OllamaRouterModelPullProgress("pull", "pulling 34%", 100L, 34L, null, null),
                new OllamaRouterModelPullProgress("pull", "pulling 100%", 100L, 100L, null, null),
                new OllamaRouterModelPullProgress("alias", "started", null, null, "llama3.2:13b-q4_K_M-8k", 8192),
                new OllamaRouterModelPullProgress("alias", "created", null, null, "llama3.2:13b-q4_K_M-8k", 8192)
            ],
            progress);

        // The terminal result.
        Assert.True(result.Succeeded);
        Assert.Equal("Local", result.Target);
        Assert.Equal("llama3.2:13b-q4_K_M", result.Model);
        Assert.Equal("llama3.2:13b-q4_K_M-8k", result.Alias);
        Assert.Equal(8192, result.AliasContext);
        Assert.Null(result.Error);
        Assert.Equal(42133, result.DurationMs);
    }

    [Fact]
    public async Task PullModelAsync_PreStreamError_ThrowsOllamaRouterApiExceptionWithStatusAndError()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.NotFound, """{"error":"target not found: ghost"}"""));
        using var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<OllamaRouterApiException>(
            () => client.PullModelAsync("llama3:8b", "ghost", 8192));

        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
        Assert.Equal("target not found: ghost", ex.Error);
    }

    [Fact]
    public async Task PullModelAsync_InStreamErrorEvent_ReturnsFailedResult_WithoutThrowing()
    {
        var ndjson = string.Join('\n',
            """{"event":"started","target":"Local","model":"ghost"}""",
            """{"event":"error","target":"Local","stage":"pull","error":"model 'ghost' not found"}""");
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjson, Encoding.UTF8, "text/x-ndjson")
        });
        using var client = CreateClient(handler);

        var result = await client.PullModelAsync("ghost", "Local", null);

        Assert.False(result.Succeeded);
        Assert.Equal("Local", result.Target);
        Assert.Equal("ghost", result.Model);
        Assert.Equal("model 'ghost' not found", result.Error);
        Assert.Null(result.DurationMs);
    }

    [Fact]
    public async Task PullModelAsync_TruncatedStream_ReturnsUnexpectedEndFailure()
    {
        // The stream ends AFTER a progress line but WITHOUT a terminal event.
        var ndjson = string.Join('\n',
            """{"event":"started","target":"Local","model":"m"}""",
            """{"event":"progress","target":"Local","status":"pulling 10%"}""");
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjson, Encoding.UTF8, "text/x-ndjson")
        });
        using var client = CreateClient(handler);

        var result = await client.PullModelAsync("m", "Local", null);

        Assert.False(result.Succeeded);
        Assert.Equal("unexpected end of pull stream", result.Error);
    }

    [Fact]
    public async Task PullModelAsync_MidStreamCancellation_ThrowsOperationCanceled_WithoutPhantomSuccess()
    {
        var cts = new CancellationTokenSource();
        var handler = new GateNdjsonHandler(cts.Token);
        using var client = new OllamaRouterApiClient(BaseUrl, null, null, new HttpClient(handler));

        var pullTask = client.PullModelAsync("m", "Local", null, null, cts.Token);
        await Task.Delay(300); // let the first line be consumed and the read block on the gate
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pullTask);
    }

    /// <summary>
    /// A canned-response stream that serves ONE <c>started</c> line and then HOLDS the read
    /// open until the caller's token cancels (the mid-stream cancellation test: the client
    /// must surface an <c>OperationCanceledException</c>, never a success).
    /// </summary>
    private sealed class GateNdjsonHandler(CancellationToken ct) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GateStream(ct))
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/x-ndjson");
            return Task.FromResult(response);
        }

        private sealed class GateStream(CancellationToken ct) : Stream
        {
            private readonly byte[] _firstLine =
                Encoding.UTF8.GetBytes("""{"event":"started","target":"Local","model":"m"}""" + '\n');
            private int _served;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_served == 0)
                {
                    var n = Math.Min(count, _firstLine.Length);
                    Buffer.BlockCopy(_firstLine, 0, buffer, offset, n);
                    _served = 1;
                    return n;
                }

                // Hold the read open until the test cancels the token (bounded wait).
                ct.WaitHandle.WaitOne();
                ct.ThrowIfCancellationRequested();
                return 0; // EOF (unreachable: the cancellation throws first)
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    // ------------------------------------------------------------------ fake handler

    private sealed record CapturedRequest(
        string Method,
        string PathAndQuery,
        string? Body,
        string? ContentType,
        string? Authorization,
        string? SessionId);

    /// <summary>
    /// Captures every outgoing request (method, path+query, body, content type, Authorization
    /// header and X-OllamaRouter-Session-Id header) and serves canned responses from a responder
    /// delegate — a self-contained stand-in for the OllamaRouter server.
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, HttpResponseMessage> _responder;

        public FakeHttpMessageHandler(Func<CapturedRequest, HttpResponseMessage> responder) =>
            _responder = responder;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                Method: request.Method.Method,
                PathAndQuery: request.RequestUri!.PathAndQuery,
                Body: request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                ContentType: request.Content?.Headers.ContentType?.MediaType,
                Authorization: request.Headers.Authorization?.ToString(),
                SessionId: request.Headers.TryGetValues("X-OllamaRouter-Session-Id", out var sessionIds)
                    ? sessionIds.FirstOrDefault()
                    : null);
            Requests.Add(captured);
            return _responder(captured);
        }
    }
}
