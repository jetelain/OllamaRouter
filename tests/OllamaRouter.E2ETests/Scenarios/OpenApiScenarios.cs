using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaRouter.E2ETests.Scenarios;

/// <summary>
/// Scenario exercising the runtime OpenAPI document of the router management API (task #51):
/// - GET /api/router/openapi.json — generated per request in ALL environments by the .NET 10
///   built-in generator from the same AOT source-generated DTOs the endpoints use (no drift;
///   this doubles as the AOT-published-build smoke check, since the E2E run hits the live
///   app produced by the release workflow's PublishAot publish).
/// Public by default (read-only; the RouterApiKey requirement covers the 20 default-protected
/// operations only: the #50 set plus the #66 recommendation writes, the #99 admin shutdown,
/// the #95 failure-streak reset admin actions, the #126 destructive model uninstall and the
/// #143 daemon-side hardware probe) —
/// no key is needed for this scenario.
/// </summary>
internal static class OpenApiScenarios
{
    /// <summary>The 36 (method, path) operations the document must contain (32 path items).</summary>
    private static readonly (string Method, string Path)[] ExpectedOperations =
    [
        ("GET", "/api/router/info"),
        ("GET", "/api/router/fleet"),
        ("GET", "/api/router/hardware"),
        ("GET", "/api/router/fleet/hardware"),
        ("PUT", "/api/router/fleet/hardware/{targetName}"),
        ("POST", "/api/router/fleet/hardware/{targetName}/probe"),
        ("POST", "/api/router/benchmark"),
        ("GET", "/api/router/benchmark/results"),
        ("DELETE", "/api/router/benchmark/results"),
        ("GET", "/api/router/models"),
        // The generator emits the registered catch-alls /api/router/models/{*modelName} and
        // /api/router/models/{*tail} with the leading '*' stripped (framework normalization).
        ("GET", "/api/router/models/{modelName}"),
        ("GET", "/api/router/cartography"),
        ("POST", "/api/router/vram/reclaim"),
        ("POST", "/api/router/fleet/unload"),
        ("POST", "/api/router/context/evaluate"),
        ("POST", "/api/router/models/{tail}"),
        ("POST", "/api/router/models/uninstall"),
        ("GET", "/api/router/preload/preview"),
        ("GET", "/api/router/config"),
        ("PUT", "/api/router/config"),
        ("POST", "/api/router/targets"),
        ("PUT", "/api/router/targets/{name}"),
        ("DELETE", "/api/router/targets/{name}"),
        ("POST", "/api/router/targets/{name}/reset-streak"),
        ("POST", "/api/router/fleet/reset-streaks"),
        ("GET", "/api/router/api-keys"),
        ("POST", "/api/router/api-keys"),
        ("DELETE", "/api/router/api-keys/{name}"),
        ("POST", "/api/router/api-keys/default/rotate"),
        // The generator emits the registered catch-all /api/router/monitor/sessions/{*sessionId}
        // with the leading '*' stripped (framework normalization, same as the models catch-alls).
        ("GET", "/api/router/monitor/sessions"),
        ("GET", "/api/router/monitor/sessions/{sessionId}"),
        ("POST", "/api/router/sessions/affinity/release"),
        ("GET", "/api/router/recommendations"),
        ("POST", "/api/router/recommendations/generate"),
        ("POST", "/api/router/recommendations/apply"),
        ("POST", "/api/router/admin/shutdown")
    ];

    public static Scenario GetRouterOpenApiDocument() => new("Router OpenAPI document /api/router/openapi.json (36 operations, OpenAPI 3.x)", async config =>
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.RouterUrl) };

        var response = await http.GetAsync("/api/router/openapi.json");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return $"Expected 200 OK from /api/router/openapi.json, got {(int)response.StatusCode} {response.ReasonPhrase}.";
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType != "application/json")
        {
            return $"Expected Content-Type 'application/json', got '{contentType}'.";
        }

        var text = await response.Content.ReadAsStringAsync();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            return $"Document is not valid JSON: {ex.Message}";
        }

        if (root is not JsonObject document)
        {
            return "Document root is not a JSON object.";
        }

        var openapi = document["openapi"]?.GetValue<string>();
        if (openapi is null || !openapi.StartsWith("3.", StringComparison.Ordinal))
        {
            return $"Expected an OpenAPI 3.x 'openapi' field, got '{openapi}'.";
        }

        var info = document["info"] as JsonObject;
        if (info is null)
        {
            return "Missing 'info' section.";
        }

        var title = info["title"]?.GetValue<string>();
        if (title != "OllamaRouter Management API")
        {
            return $"Expected info.title 'OllamaRouter Management API', got '{title}'.";
        }

        if (string.IsNullOrWhiteSpace(info["description"]?.GetValue<string>()))
        {
            return "info.description is missing or empty.";
        }

        if (string.IsNullOrWhiteSpace(info["version"]?.GetValue<string>()))
        {
            return "info.version is missing or empty (must match the assembly informational version).";
        }

        var paths = document["paths"] as JsonObject;
        if (paths is null)
        {
            return "Missing 'paths' section.";
        }

        var actualOperations = new HashSet<(string Method, string Path)>();
        var operationIds = new HashSet<string>();
        var idDuplicated = false;
        foreach (var pathEntry in (IDictionary<string, JsonNode?>)paths)
        {
            if (pathEntry.Value is not JsonObject pathItem)
            {
                continue;
            }

            foreach (var methodEntry in (IDictionary<string, JsonNode?>)pathItem)
            {
                if (methodEntry.Key is not ("get" or "put" or "post" or "delete" or "patch"))
                {
                    continue;
                }

                actualOperations.Add((methodEntry.Key.ToUpperInvariant(), pathEntry.Key));
                if (methodEntry.Value is JsonObject operation)
                {
                    var id = operation["operationId"]?.GetValue<string>();
                    if (id is not null)
                    {
                        if (!operationIds.Add(id))
                        {
                            idDuplicated = true;
                        }
                    }
                }
            }
        }

        if (actualOperations.Count != ExpectedOperations.Length)
        {
            return $"Expected {ExpectedOperations.Length} operations, got {actualOperations.Count}.";
        }

        foreach (var (method, path) in ExpectedOperations)
        {
            if (!actualOperations.Contains((method, path)))
            {
                return $"Missing operation {method} {path}.";
            }
        }

        if (idDuplicated)
        {
            return "operationIds are not unique across the document.";
        }

        if (operationIds.Count != ExpectedOperations.Length)
        {
            return $"Expected {ExpectedOperations.Length} unique operationIds, got {operationIds.Count}.";
        }

        // Task #87: the session 200 schemas expose the session-level actualCloudCost/cost
        // properties (the AOT source-generator inlines the nested item schemas, so the list
        // row and per-target row are checked on the inlined 'items' objects), and the
        // per-target row carries NEITHER (session-level only).
        if (document["components"] is not JsonObject components
            || components["schemas"] is not JsonObject schemas)
        {
            return "Missing 'components.schemas' section.";
        }

        JsonObject? FindItemsProperties(JsonObject? schema, string property)
        {
            if (schema?["properties"] is not JsonObject properties
                || properties[property] is not JsonObject arrayProperty
                || arrayProperty["items"] is not JsonObject items
                || items["properties"] is not JsonObject itemProperties)
            {
                return null;
            }

            return itemProperties;
        }

        if (schemas["RouterSessionUsageListResponse"] is not JsonObject listSchema)
        {
            return "Missing the RouterSessionUsageListResponse component schema.";
        }

        if (FindItemsProperties(listSchema, "sessions") is not JsonObject listItemProperties
            || listItemProperties["actualCloudCost"] is null
            || listItemProperties["cost"] is null)
        {
            return "The session list row schema is missing the 'actualCloudCost' and/or 'cost' properties.";
        }

        if (schemas["RouterSessionUsageDetailResponse"] is not JsonObject detailSchema
            || detailSchema["properties"] is not JsonObject detailProperties)
        {
            return "Missing the RouterSessionUsageDetailResponse component schema or its 'properties'.";
        }

        if (detailProperties["actualCloudCost"] is null || detailProperties["cost"] is null)
        {
            return "The session detail schema is missing the top-level 'actualCloudCost' and/or 'cost' properties.";
        }

        if (FindItemsProperties(detailSchema, "perTarget") is not JsonObject perTargetProperties
            || perTargetProperties["actualCloudCost"] is not null
            || perTargetProperties["cost"] is not null)
        {
            return "The per-target row schema must NOT carry 'actualCloudCost'/'cost' (session-level only).";
        }

        return null;
    });
}
