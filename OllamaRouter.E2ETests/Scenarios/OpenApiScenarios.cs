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
/// Public by default (read-only; the RouterApiKey requirement covers the 12 #50 protected
/// operations only) — no key is needed for this scenario.
/// </summary>
internal static class OpenApiScenarios
{
    /// <summary>The 25 (method, path) operations the document must contain (21 path items).</summary>
    private static readonly (string Method, string Path)[] ExpectedOperations =
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
        // The generator emits the registered catch-alls /api/router/models/{*modelName} and
        // /api/router/models/{*tail} with the leading '*' stripped (framework normalization).
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

    public static Scenario GetRouterOpenApiDocument() => new("Router OpenAPI document /api/router/openapi.json (25 operations, OpenAPI 3.x)", async config =>
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

        return null;
    });
}
