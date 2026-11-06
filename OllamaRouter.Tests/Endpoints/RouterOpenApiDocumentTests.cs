using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OllamaRouter.Endpoints;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Serialization;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Full-pipeline integration tests for the runtime OpenAPI document (task #51): the document
/// is generated per request by the .NET 10 built-in generator (<see cref="RouterOpenApiConventions"/>
/// + the source-generated DTO schemas) and served at <c>GET /api/router/openapi.json</c> in
/// ALL environments. The host runs the production wiring (<c>AddOllamaRouter</c> instance
/// overload + <c>UseOllamaRouterPipeline</c>) on loopback with a temp content root/state
/// directory, so production and tests stay identical.
/// </summary>
public class RouterOpenApiDocumentTests
{
    private sealed class DocHarness : IAsyncDisposable
    {
        public DocHarness(WebApplication app, HttpClient client, IApiKeyStore keys, string contentRoot)
        {
            App = app;
            Client = client;
            Keys = keys;
            ContentRoot = contentRoot;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public IApiKeyStore Keys { get; }

        public string ContentRoot { get; }

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

        public static async Task<DocHarness> StartAsync(bool requireKeyForAllEndpoints)
        {
            var root = Path.Combine(Path.GetTempPath(), $"ollamarouter-openapi-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var stateDirectory = Path.Combine(root, "state");

            var options = new OllamaRouterOptions
            {
                BindAddress = "http://127.0.0.1:0",
                Targets = [],
                TerminalMonitoring = new TerminalMonitoringOptions { Enabled = false },
                ApiSecurity = new ApiSecurityOptions { RequireKeyForAllEndpoints = requireKeyForAllEndpoints }
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

            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(15) };
            return new DocHarness(app, client, app.Services.GetRequiredService<IApiKeyStore>(), root);
        }
    }

    /// <summary>
    /// The approved 25 router operations: (method, document path) pairs — 21 path items.
    /// NOTE: the generator emits the two catch-all templates with the leading <c>*</c> stripped
    /// (the framework's documented template normalization: the registered
    /// <c>/api/router/models/{*modelName}</c> / <c>POST /api/router/models/{*tail}</c> are
    /// documented as <c>{modelName}</c> / <c>{tail}</c>) — the assertions use the EMITTED form.
    /// </summary>
    private static readonly (string Method, string Path)[] ApprovedOperations =
    [
        ("GET", "/api/router/info"),
        ("GET", "/api/router/fleet"),
        ("GET", "/api/router/hardware"),
        ("GET", "/api/router/fleet/hardware"),
        ("PUT", "/api/router/fleet/hardware/{targetName}"),
        ("POST", "/api/router/benchmark"),
        ("GET", "/api/router/benchmark/results"),
        ("DELETE", "/api/router/benchmark/results"),
        ("GET", "/api/router/models"),
        ("GET", "/api/router/models/{modelName}"),
        ("GET", "/api/router/cartography"),
        ("POST", "/api/router/vram/reclaim"),
        ("POST", "/api/router/fleet/unload"),
        ("POST", "/api/router/context/evaluate"),
        ("POST", "/api/router/models/{tail}"),
        ("GET", "/api/router/preload/preview"),
        ("GET", "/api/router/config"),
        ("PUT", "/api/router/config"),
        ("POST", "/api/router/targets"),
        ("PUT", "/api/router/targets/{name}"),
        ("DELETE", "/api/router/targets/{name}"),
        ("GET", "/api/router/api-keys"),
        ("POST", "/api/router/api-keys"),
        ("DELETE", "/api/router/api-keys/{name}"),
        ("POST", "/api/router/api-keys/default/rotate")
    ];

    /// <summary>The seven functional tag groups (the complete tag set of the document).</summary>
    private static readonly string[] ApprovedTags =
    [
        "Identity & Fleet",
        "Hardware Profiling",
        "Benchmarking",
        "Model Catalog & Cartography",
        "Fleet Operations",
        "Setup & Configuration",
        "API Key Security"
    ];

    /// <summary>
    /// Approved operationId per (method, path) — unique across the document.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string, string), string> ApprovedOperationIds =
        new Dictionary<(string, string), string>
        {
            { ("GET", "/api/router/info"), "GetRouterInfo" },
            { ("GET", "/api/router/fleet"), "GetRouterFleet" },
            { ("GET", "/api/router/hardware"), "GetRouterHardware" },
            { ("GET", "/api/router/fleet/hardware"), "GetRouterFleetHardware" },
            { ("PUT", "/api/router/fleet/hardware/{targetName}"), "PutRouterFleetHardwareProfile" },
            { ("POST", "/api/router/benchmark"), "PostRouterBenchmarkRun" },
            { ("GET", "/api/router/benchmark/results"), "GetRouterBenchmarkResults" },
            { ("DELETE", "/api/router/benchmark/results"), "DeleteRouterBenchmarkResults" },
            { ("GET", "/api/router/models"), "GetRouterModels" },
            { ("GET", "/api/router/models/{modelName}"), "GetRouterModelDetail" },
            { ("GET", "/api/router/cartography"), "GetRouterCartography" },
            { ("POST", "/api/router/vram/reclaim"), "PostRouterVramReclaim" },
            { ("POST", "/api/router/fleet/unload"), "PostRouterFleetUnload" },
            { ("POST", "/api/router/context/evaluate"), "PostRouterContextEvaluate" },
            { ("POST", "/api/router/models/{tail}"), "PostRouterModelPreload" },
            { ("GET", "/api/router/preload/preview"), "GetRouterPreloadPreview" },
            { ("GET", "/api/router/config"), "GetRouterConfig" },
            { ("PUT", "/api/router/config"), "PutRouterConfig" },
            { ("POST", "/api/router/targets"), "PostRouterTarget" },
            { ("PUT", "/api/router/targets/{name}"), "PutRouterTarget" },
            { ("DELETE", "/api/router/targets/{name}"), "DeleteRouterTarget" },
            { ("GET", "/api/router/api-keys"), "GetRouterApiKeys" },
            { ("POST", "/api/router/api-keys"), "PostRouterApiKey" },
            { ("DELETE", "/api/router/api-keys/{name}"), "DeleteRouterApiKey" },
            { ("POST", "/api/router/api-keys/default/rotate"), "PostRotateRouterDefaultApiKey" }
        };

    /// <summary>
    /// The per-operation status matrix: (method, path) → (successCode → schema record name)
    /// and (errorCode → schema record name). Error schemas are the shared
    /// <c>OllamaErrorResponse</c> or the endpoint's own <c>success:false</c> response shape.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string, string), (string Code, string Schema)[]> StatusMatrix =
        new Dictionary<(string, string), (string, string)[]>
        {
            { ("GET", "/api/router/info"), [("200", "RouterInfoResponse")] },
            { ("GET", "/api/router/fleet"), [("200", "RouterFleetResponse")] },
            { ("GET", "/api/router/hardware"), [("200", "NodeHardwareProfile")] },
            { ("GET", "/api/router/fleet/hardware"), [("200", "RouterFleetHardwareResponse")] },
            { ("PUT", "/api/router/fleet/hardware/{targetName}"),
                [("200", "NodeHardwareProfile"), ("400", "OllamaErrorResponse"), ("404", "OllamaErrorResponse")] },
            { ("POST", "/api/router/benchmark"),
                [("202", "RouterBenchmarkRunResponse"), ("400", "OllamaErrorResponse"), ("404", "OllamaErrorResponse"), ("409", "OllamaErrorResponse")] },
            { ("GET", "/api/router/benchmark/results"), [("200", "RouterBenchmarkResultsResponse")] },
            { ("DELETE", "/api/router/benchmark/results"), [("200", "RouterBenchmarkClearedResponse")] },
            { ("GET", "/api/router/models"), [("200", "RouterModelsResponse")] },
            { ("GET", "/api/router/models/{modelName}"),
                [("200", "RouterModelDetailResponse"), ("404", "OllamaErrorResponse")] },
            { ("GET", "/api/router/cartography"), [("200", "RouterCartographyResponse")] },
            { ("POST", "/api/router/vram/reclaim"),
                [("200", "RouterVramReclaimResponse"), ("404", "OllamaErrorResponse")] },
            { ("POST", "/api/router/fleet/unload"),
                [("200", "RouterVramReclaimResponse"), ("404", "OllamaErrorResponse")] },
            { ("POST", "/api/router/context/evaluate"),
                [("200", "RouterContextEvaluateResponse"), ("400", "OllamaErrorResponse"), ("503", "OllamaErrorResponse")] },
            { ("POST", "/api/router/models/{tail}"),
                [("200", "RouterModelPreloadResponse"), ("400", "OllamaErrorResponse"), ("404", "OllamaErrorResponse"), ("503", "OllamaErrorResponse")] },
            { ("GET", "/api/router/preload/preview"),
                [("200", "RouterModelPreloadResponse"), ("400", "OllamaErrorResponse"), ("404", "OllamaErrorResponse"), ("503", "OllamaErrorResponse")] },
            { ("GET", "/api/router/config"), [("200", "RouterConfigResponse")] },
            { ("PUT", "/api/router/config"),
                [("200", "UpdateRouterConfigResponse"), ("400", "UpdateRouterConfigResponse"), ("500", "UpdateRouterConfigResponse")] },
            { ("POST", "/api/router/targets"),
                [("201", "AddTargetResponse"), ("400", "AddTargetResponse"), ("409", "AddTargetResponse"), ("422", "AddTargetResponse"), ("500", "AddTargetResponse")] },
            { ("PUT", "/api/router/targets/{name}"),
                [("200", "UpdateTargetResponse"), ("400", "UpdateTargetResponse"), ("404", "UpdateTargetResponse"), ("500", "UpdateTargetResponse")] },
            { ("DELETE", "/api/router/targets/{name}"),
                [("200", "DeleteTargetResponse"), ("404", "DeleteTargetResponse"), ("500", "DeleteTargetResponse")] },
            { ("GET", "/api/router/api-keys"),
                [("200", "ApiKeysListResponse"), ("500", "OllamaErrorResponse")] },
            { ("POST", "/api/router/api-keys"),
                [("201", "ApiKeyCreatedResponse"), ("400", "OllamaErrorResponse"), ("409", "OllamaErrorResponse"), ("500", "OllamaErrorResponse")] },
            { ("DELETE", "/api/router/api-keys/{name}"),
                [("200", "ApiKeyDeletedResponse"), ("404", "ApiKeyDeletedResponse"), ("500", "OllamaErrorResponse")] },
            { ("POST", "/api/router/api-keys/default/rotate"),
                [("200", "ApiKeyRotatedResponse"), ("500", "OllamaErrorResponse")] }
        };

    /// <summary>
    /// The parameters the generator must document, with their expected BASE schema type
    /// (the 3.1 generator emits 'type' as a string or a string array, e.g.
    /// ["integer","null"] for a nullable [FromQuery] int?). Covers the route parameters of
    /// the templated operations AND the query parameters; asserting the types guards against
    /// parameter schemas collapsing into shared generic-name components (e.g. every
    /// Nullable<T> ref-ing one "Nullable`1" schema of the first-registered type).
    /// </summary>
    private static readonly IReadOnlyDictionary<(string, string), (string Name, string Type)[]> ExpectedParameters =
        new Dictionary<(string, string), (string, string)[]>
        {
            { ("PUT", "/api/router/fleet/hardware/{targetName}"), [("targetName", "string")] },
            { ("GET", "/api/router/models/{modelName}"), [("modelName", "string")] },
            { ("POST", "/api/router/vram/reclaim"), [("target", "string"), ("disableTarget", "boolean")] },
            { ("POST", "/api/router/fleet/unload"), [("target", "string"), ("disableTarget", "boolean")] },
            { ("POST", "/api/router/models/{tail}"),
                [("tail", "string"), ("keepAliveSeconds", "integer"), ("contextIntentTokens", "integer"), ("target", "string")] },
            { ("GET", "/api/router/preload/preview"),
                [("model", "string"), ("keepAliveSeconds", "integer"), ("contextIntentTokens", "integer"), ("target", "string")] },
            { ("PUT", "/api/router/targets/{name}"), [("name", "string")] },
            { ("DELETE", "/api/router/targets/{name}"), [("name", "string")] },
            { ("DELETE", "/api/router/api-keys/{name}"), [("name", "string")] }
        };

    private static string[] JsonObjectKeys(JsonObject obj) =>
        ((IDictionary<string, JsonNode>)obj).Keys.ToArray();

    private static async Task<JsonObject> FetchDocumentAsync(DocHarness harness)
    {
        using var response = await harness.Client.GetAsync("/api/router/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.Equal("application/json", mediaType);
        var text = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(text);
        Assert.NotNull(json);
        return (JsonObject)json!;
    }

    private static OpenApiOperationNode GetOperation(JsonObject document, string method, string path)
    {
        var paths = (JsonObject)document["paths"]!;
        var pathItem = (JsonObject)paths[path]!;
        var op = pathItem[method.ToLowerInvariant()];
        Assert.NotNull(op);
        return new OpenApiOperationNode((JsonObject)op!);
    }

    private sealed record OpenApiOperationNode(JsonObject Op)
    {
        public string? OperationId => Op["operationId"]?.GetValue<string>();

        public string? Summary => Op["summary"]?.GetValue<string>();

        public string? Description => Op["description"]?.GetValue<string>();

        public string[] Tags =>
            (Op["tags"] as JsonArray)?.Select(t => t!.GetValue<string>()!).ToArray() ?? [];

        public JsonNode? BodySchema => (Op["requestBody"] as JsonObject)?["content"]?["application/json"]?["schema"];

        public JsonObject? Responses => Op["responses"] as JsonObject;

        public JsonNode? ResponseSchema(string code) => Responses?[code]?["content"]?["application/json"]?["schema"];

        public string? ResponseRef(string code) => (ResponseSchema(code) as JsonObject)?["$ref"]?.GetValue<string>();

        public bool HasSecurity => Op["security"] is { } security && (security as JsonArray)?.Count > 0;
    }

    [Fact]
    public async Task Document_ReturnsOpenApi31_WithInfo_TitleAndVersion()
    {
        await using var harness = await DocHarness.StartAsync(requireKeyForAllEndpoints: false);
        var document = await FetchDocumentAsync(harness);

        var openapi = document["openapi"]?.GetValue<string>();
        Assert.NotNull(openapi);
        Assert.StartsWith("3.", openapi!);

        var info = (JsonObject)document["info"]!;
        Assert.Equal("OllamaRouter Management API", info["title"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(info["description"]?.GetValue<string>()));

        // The version MUST match the assembly informational version AND the version reported
        // by GET /api/router/info (same source: RouterApiEndpointsExtensions.GetRouterVersion).
        var assemblyVersion = Assembly
            .GetAssembly(typeof(RouterApiEndpointsExtensions))
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(assemblyVersion));
        Assert.Equal(assemblyVersion, info["version"]?.GetValue<string>());

        using var infoResponse = await harness.Client.GetAsync("/api/router/info");
        Assert.Equal(HttpStatusCode.OK, infoResponse.StatusCode);
        var infoJson = (JsonObject)JsonNode.Parse(await infoResponse.Content.ReadAsStringAsync())!;
        Assert.Equal(info["version"]!.GetValue<string>(), infoJson["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task Document_ContainsExactlyThe25RouterOperations_AndExcludesEverythingElse()
    {
        await using var harness = await DocHarness.StartAsync(requireKeyForAllEndpoints: false);
        var document = await FetchDocumentAsync(harness);
        var paths = (JsonObject)document["paths"]!;

        // Exactly the 25 approved (method, path) pairs (21 path items).
        Assert.Equal(21, paths.Count);
        var pathKeys = JsonObjectKeys(paths);
        var actualOperations = pathKeys
            .Select(path => (Path: path, Item: (JsonObject)paths[path]!))
            .SelectMany(entry => entry.Item
                .Where(member =>
                    member.Key is "get" or "put" or "post" or "delete" or "patch" or "head" or "options")
                .Select(member => (member.Key.ToUpperInvariant(), entry.Path)))
            .ToArray();

        Assert.Equal(ApprovedOperations.Length, actualOperations.Length);
        var actualSet = actualOperations.ToHashSet();
        foreach (var (method, path) in ApprovedOperations)
        {
            Assert.Contains((method, path), actualSet);
        }

        // No monitor / Ollama-compatible hybrid / YARP route may leak in, and the document
        // endpoint's own route must be absent from the document.
        foreach (var path in pathKeys)
        {
            Assert.True(path.StartsWith("/api/router/", StringComparison.Ordinal),
                $"unexpected non-router path in document: '{path}'");
            Assert.True(path != "/api/router/openapi.json", "the document endpoint's own route must be absent");
        }
    }

    [Fact]
    public async Task Document_OperationIds_ArePresent_Unique_AndApproved()
    {
        await using var harness = await DocHarness.StartAsync(requireKeyForAllEndpoints: false);
        var document = await FetchDocumentAsync(harness);

        var operationIds = new List<string>();
        foreach (var (method, path) in ApprovedOperations)
        {
            var operation = GetOperation(document, method, path);
            var expectedId = ApprovedOperationIds[(method, path)];
            Assert.True(operation.OperationId == expectedId, $"operationId of {method} {path} (got {operation.OperationId})");
            operationIds.Add(operation.OperationId!);
        }

        Assert.Equal(operationIds.Count, operationIds.Distinct().Count());
    }

    [Fact]
    public async Task Document_EveryOperation_HasSummary_Description_Tags_AndDocumentedParameters()
    {
        await using var harness = await DocHarness.StartAsync(requireKeyForAllEndpoints: false);
        var document = await FetchDocumentAsync(harness);

        var allTags = new HashSet<string>();
        foreach (var (method, path) in ApprovedOperations)
        {
            var operation = GetOperation(document, method, path);
            Assert.False(string.IsNullOrWhiteSpace(operation.Summary), $"{method} {path}: summary");
            Assert.False(string.IsNullOrWhiteSpace(operation.Description), $"{method} {path}: description");
            Assert.True(operation.Tags.Length > 0, $"{method} {path}: tags");
            allTags.UnionWith(operation.Tags);
        }

        // The tag set equals the seven functional groups.
        Assert.Equal(new HashSet<string>(ApprovedTags), allTags);

        // Every documented parameter carries the expected BASE schema type (route AND query
        // parameters; parameter schemas are inlined, never shared generic-name $refs).
        foreach (var ((method, path), expected) in ExpectedParameters)
        {
            var operation = GetOperation(document, method, path);
            var parameters = (operation.Op["parameters"] as JsonArray)?.
                Select(p => (JsonObject)p!)
                .ToDictionary(p => p["name"]!.GetValue<string>(), StringComparer.Ordinal)
                ?? new Dictionary<string, JsonObject>();

            foreach (var (name, expectedType) in expected)
            {
                Assert.True(parameters.TryGetValue(name, out var parameter), $"{method} {path}: parameter '{name}'");

                var schema = (JsonObject)parameter["schema"]!;
                Assert.True(schema["$ref"] is null,
                    $"{method} {path}: parameter '{name}' must not be a shared $ref");

                var typeNode = schema["type"];
                var types = typeNode is JsonArray typeArray
                    ? typeArray.Select(t => t!.GetValue<string>()!).ToArray()
                    : [typeNode!.GetValue<string>()!];
                Assert.True(
                    types.Contains(expectedType, StringComparer.Ordinal),
                    $"{method} {path}: parameter '{name}' must be typed '{expectedType}' (got [{string.Join(",", types)}])");
            }
        }
    }

    [Fact]
    public async Task Document_ResponseSchemas_AreNamedComponentRefs_MatchingTheStatusMatrix()
    {
        await using var harness = await DocHarness.StartAsync(requireKeyForAllEndpoints: false);
        var document = await FetchDocumentAsync(harness);
        var components = (JsonObject)document["components"]!;
        var schemas = (JsonObject)components["schemas"]!;

        foreach (var ((method, path), matrix) in StatusMatrix)
        {
            var operation = GetOperation(document, method, path);
            var expectedCodes = matrix.Select(m => m.Code).ToArray();

            // Exactly the documented status codes; protected operations additionally carry
            // the 401 (asserted in detail in the security test).
            var expectedWith401 = ApiSecurityPolicy.IsProtectedRoute(method, path)
                ? expectedCodes.Append("401").ToArray()
                : expectedCodes;
            var actualCodes = JsonObjectKeys(operation.Responses!);
            expectedCodes = expectedWith401;
            Assert.True(expectedCodes.SequenceEqual(actualCodes), $"{method} {path}: status codes (got [{string.Join(",", actualCodes)}])");

            foreach (var (code, schemaName) in matrix)
            {
                var schema = operation.ResponseSchema(code);
                var reference = (schema as JsonObject)?["$ref"]?.GetValue<string>();
                Assert.True(reference is not null, $"{method} {path} {code}: schema must be a $ref to a named component");
                Assert.True(
                    reference!.EndsWith("/" + schemaName, StringComparison.Ordinal),
                    $"{method} {path} {code}: expected $ref to '{schemaName}', got '{reference}'");

                // The referenced component exists (named DTO record schema, not anonymous).
                Assert.Contains(
                    reference[(reference.LastIndexOf('/') + 1)..],
                    JsonObjectKeys(schemas));
            }
        }
    }

    [Fact]
    public async Task Document_RequestBodies_AreDocumented_ForTheBodyAcceptingEndpoints()
    {
        var expected = new Dictionary<(string, string), (Type Schema, bool Required)>
        {
            { ("PUT", "/api/router/fleet/hardware/{targetName}"), (typeof(NodeHardwareProfile), true) },
            { ("POST", "/api/router/benchmark"), (typeof(RouterBenchmarkRequest), false) },
            { ("POST", "/api/router/vram/reclaim"), (typeof(RouterVramReclaimRequest), false) },
            { ("POST", "/api/router/fleet/unload"), (typeof(RouterVramReclaimRequest), false) },
            { ("POST", "/api/router/context/evaluate"), (typeof(RouterContextEvaluateRequest), true) },
            { ("POST", "/api/router/models/{tail}"), (typeof(RouterModelPreloadRequest), false) },
            { ("PUT", "/api/router/config"), (typeof(UpdateRouterConfigRequest), true) },
            { ("POST", "/api/router/targets"), (typeof(AddTargetRequest), true) },
            { ("PUT", "/api/router/targets/{name}"), (typeof(UpdateTargetRequest), true) },
            { ("POST", "/api/router/api-keys"), (typeof(ApiKeyCreateRequest), true) }
        };

        await using var harness = await DocHarness.StartAsync(requireKeyForAllEndpoints: false);
        var document = await FetchDocumentAsync(harness);

        // The body-accepting endpoints carry the typed request body; the other 15 do not
        // (the plan's "nine" body list plus POST /api/router/api-keys from task #50).
        foreach (var (method, path) in ApprovedOperations)
        {
            var operation = GetOperation(document, method, path);
            if (expected.TryGetValue((method, path), out var body))
            {
                var requestBody = (JsonObject)operation.Op["requestBody"]!;
                // OpenAPI omits "required" for false (the default) when serializing.
                var required = requestBody["required"] is { } requiredNode
                    ? requiredNode.GetValue<bool>()
                    : false;
                Assert.True(body.Required == required, $"{method} {path}: required");
                var reference = (operation.BodySchema as JsonObject)?["$ref"]?.GetValue<string>();
                Assert.True(reference is not null, $"{method} {path}: request body schema must be a named $ref");
                Assert.True(reference!.EndsWith("/" + body.Schema.Name, StringComparison.Ordinal),
                    $"{method} {path}: expected request body $ref to '{body.Schema.Name}', got '{reference}'");
            }
            else
            {
                Assert.True(operation.Op["requestBody"] is null, $"{method} {path}: unexpected request body");
            }
        }
    }

    [Fact]
    public async Task Document_SecurityScheme_Declared_AndRequirementApplied_ExactlyToTheProtectedSet()
    {
        await using var harness = await DocHarness.StartAsync(requireKeyForAllEndpoints: false);
        var document = await FetchDocumentAsync(harness);

        var schemes = (JsonObject)((JsonObject)document["components"]!)["securitySchemes"]!;
        // Exactly ONE security scheme: RouterApiKey (http / bearer / base64url + description).
        Assert.Single(schemes);
        var scheme = (JsonObject)schemes["RouterApiKey"]!;
        Assert.Equal("http", scheme["type"]?.GetValue<string>());
        Assert.Equal("bearer", scheme["scheme"]?.GetValue<string>());
        Assert.Equal("base64url", scheme["bearerFormat"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(scheme["description"]?.GetValue<string>()));

        // The requirement is applied to EXACTLY the operations the production policy protects
        // (12 of the 25), each with a documented 401 OllamaErrorResponse.
        var protectedCount = 0;
        foreach (var (method, path) in ApprovedOperations)
        {
            var operation = GetOperation(document, method, path);
            var isProtected = ApiSecurityPolicy.IsProtectedRoute(method, path);
            Assert.True(
                isProtected == operation.HasSecurity,
                $"{method} {path}: security requirement must match ApiSecurityPolicy.IsProtectedRoute");

            if (isProtected)
            {
                protectedCount++;
                var security = (JsonArray)operation.Op["security"]!;
                var requirement = (JsonObject)security[0]!;
                var schemeKeys = JsonObjectKeys(requirement);
                Assert.Single(schemeKeys);
                Assert.Equal("RouterApiKey", schemeKeys[0]);

                var reference = operation.ResponseRef("401");
                Assert.True(reference is not null, $"{method} {path}: protected operations document a 401");
                Assert.EndsWith("/OllamaErrorResponse", reference!, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(operation.Responses?["401"] is null, $"{method} {path}: 401 only on protected operations");
            }
        }

        // The #50 default protected set is exactly 12 operations.
        Assert.Equal(12, protectedCount);
    }

    [Fact]
    public async Task Document_Endpoint_RequiresKey_Only_WhenRequireKeyForAllEndpoints_IsSet()
    {
        // Default mode: the document is public (read-only; no security requirement).
        await using (var open = await DocHarness.StartAsync(requireKeyForAllEndpoints: false))
        {
            var document = await FetchDocumentAsync(open);
            Assert.NotNull(document);
        }

        // All-endpoints mode: EVERY route (including the document endpoint) 401s without a
        // key and serves with the default key (the #50 semantics).
        await using var locked = await DocHarness.StartAsync(requireKeyForAllEndpoints: true);

        using var unauthorized = await locked.Client.GetAsync("/api/router/openapi.json");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("Bearer", unauthorized.Headers.WwwAuthenticate?.FirstOrDefault()?.Scheme);

        var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/router/openapi.json");
        authorizedRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", locked.Keys.GetDefaultKey());
        using var authorized = await locked.Client.SendAsync(authorizedRequest);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        var json = JsonNode.Parse(await authorized.Content.ReadAsStringAsync());
        Assert.NotNull(json?["paths"]);
    }
}
