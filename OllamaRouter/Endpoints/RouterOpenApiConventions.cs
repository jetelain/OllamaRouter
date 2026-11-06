using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using OllamaRouter.Middleware;
using OllamaRouter.Security;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Single source of truth for the runtime OpenAPI document of the router management API
/// (task #51): the document is generated per request by the .NET 10 built-in generator
/// (first-party <c>Microsoft.AspNetCore.OpenApi</c> package; AOT-safe, schemas resolve from
/// the source-generated <c>OllamaRouterJsonSerializerContext</c> via the app's
/// <c>HttpJsonOptions</c>) and is served at exactly <c>GET /api/router/openapi.json</c>
/// (document name <see cref="DocumentName"/> + the <c>{documentName}</c> route parameter).
/// <para>
/// Scope: the 25 <c>/api/router/*</c> management operations ONLY (monitor, Ollama-compatible
/// hybrid and YARP routes are excluded via <see cref="OpenApiOptions.ShouldInclude"/>, and the
/// document endpoint's own template route is dropped). The security requirement is derived by
/// calling the production <see cref="ApiSecurityPolicy.IsProtectedRoute"/> on each route
/// template, so the document can never drift from the enforced set (12 protected operations
/// in default mode; <c>ApiSecurity.RequireKeyForAllEndpoints: true</c> protects every route).
/// </para>
/// <para>
/// All per-operation work (request bodies, the response status matrix, security + 401) is
/// done by the DOCUMENT transformer, which the .NET 10 generator runs as the FINAL pass
/// (order: built-in operation construction → user operation transformers → document
/// transformers). The router endpoints read <c>HttpRequest.Body</c> manually (no
/// <c>[FromBody]</c> metadata), so the built-in path creates no request body for them and
/// derives only the <c>TypedResults</c> responses — no error matrix, no 401, no security.
/// Replacing the response set wholesale and setting the request body explicitly in the
/// final pass is the single choke point that sees every operation's final state.
/// </para>
/// </summary>
public static class RouterOpenApiConventions
{
    /// <summary>The OpenAPI document name (the served URL is <c>/api/router/openapi.json</c>).</summary>
    public const string DocumentName = "openapi";

    /// <summary>
    /// The route pattern the document is mapped at. The <c>{documentName}</c> parameter MUST be
    /// kept (removing it is documented as not recommended): any other single-segment
    /// <c>*.json</c> name resolves no registered document (404).
    /// </summary>
    public const string DocumentRoute = "/api/router/{documentName}.json";

    /// <summary>
    /// Registers the API Explorer + OpenAPI generation services with the router conventions
    /// (3.1 spec, <c>/api/router/*</c>-only scope, named component schemas, document
    /// transformer). Idempotent with respect to the API Explorer providers; called once from
    /// <c>AddOllamaRouterCore</c> so the production host and the full-pipeline test hosts
    /// stay identical.
    /// </summary>
    public static IServiceCollection AddRouterOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;

            // NOTE: no OpenApiOptions.CreateSchemaReferenceId is set on purpose. The named
            // component schemas of the DTO records are registered explicitly by the document
            // transformer (first registration wins, keyed by the type's simple name), and the
            // generator inlines every other node (nested DTOs, primitives, Nullable<T> of the
            // [FromQuery] parameters) with its exact type. Keying by the simple type name
            // would instead collapse ALL Nullable<T> parameter schemas into one colliding
            // "Nullable`1" component (first registration wins → e.g. int? keepAliveSeconds
            // documented as the first-registered bool?) and emit orphan "String" components.

            // Keep ONLY the /api/router/ management operations (relative paths are
            // lowercase, no leading slash) and explicitly drop the document endpoint's own
            // template route (any casing of the {documentName} parameter) so it never
            // appears as an operation of itself.
            options.ShouldInclude = description =>
            {
                if (string.IsNullOrWhiteSpace(description.RelativePath) ||
                    !description.RelativePath.StartsWith("api/router/", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var leaf = description.RelativePath[(description.RelativePath.LastIndexOf('/') + 1)..];
                return !(leaf[0] == '{' &&
                         leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            };

            options.AddDocumentTransformer(RouterOpenApiDocumentTransformer.TransformAsync);
        });

        return services;
    }
}

/// <summary>
/// Document-level transformer (task #51): sets <c>info</c> (title / description / the same
/// assembly informational version <c>GET /api/router/info</c> reports), declares the
/// <c>RouterApiKey</c> security scheme with the task #50 key-storage / rotation contract,
/// and finalizes EVERY operation: attaches the JSON request bodies the endpoints read from
/// <c>HttpRequest.Body</c> manually (no <c>[FromBody]</c> binding metadata exists for the
/// generator to discover), replaces the response set with the per-operation status-code
/// matrix (success responses resolve to the named component schemas of the DTO records;
/// errors use the endpoint's real error payload), and — and ONLY where the production
/// <see cref="ApiSecurityPolicy.IsProtectedRoute"/> says so (called with the route template)
/// — applies the <c>RouterApiKey</c> security requirement plus the 401 response. Metadata
/// (operationId / tags / summary / description / parameters) is NOT touched here: it flows
/// from the endpoints' <c>WithName/WithTags/WithSummary/WithDescription</c>.
/// Reflection-free: only the <c>Microsoft.OpenApi</c> model is touched (AOT-safe).
/// </summary>
internal static class RouterOpenApiDocumentTransformer
{
    public const string SecuritySchemeName = "RouterApiKey";

    public static async Task TransformAsync(
        OpenApiDocument document,
        Microsoft.AspNetCore.OpenApi.OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "OllamaRouter Management API",
            Description =
                "Management API of the OllamaRouter fleet gateway: dynamic multi-Ollama-target " +
                "routing with hardware-aware context sizing and cloud overflow, fleet hardware " +
                "profiling, model benchmarking, the zero-config model catalog & fleet cartography, " +
                "fleet operations (VRAM reclaim / fleet unload, dynamic context evaluation, " +
                "router-owned model pre-load) and in-app fleet / router configuration. " +
                "The Ollama-compatible inference surface (chat, generate, tags, ps, models) and " +
                "the YARP proxy are outside this document.",
            Version = RouterApiEndpointsExtensions.GetRouterVersion()
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SecuritySchemeName] =
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "base64url",
                Description =
                    "API key (task #50): the raw 32-byte base64url key is accepted via " +
                    "'Authorization: Bearer <key>' (preferred) OR the 'X-Api-Key: <key>' header " +
                    "(OpenAPI cannot express the second header on the same scheme). The default " +
                    "key is auto-generated in config/default-key.dat (DPAPI-wrapped on Windows, " +
                    "file mode 0600 on Unix) and auto-rotated every ApiSecurity.DefaultKeyRotationHours " +
                    "(default 24 h) with NO grace period — re-read the key after a 401. User keys " +
                    "persist only as SHA-256 hashes in config/api-keys.json. 401 responses carry " +
                    "'WWW-Authenticate: Bearer' plus the OllamaErrorResponse JSON body. The " +
                    "requirement is derived from the production ApiSecurityPolicy default set " +
                    "(config-affecting routes + key management); ApiSecurity.RequireKeyForAllEndpoints=true " +
                    "protects EVERY route (including this document endpoint)."
            };

        // Finalize every operation (see the class-level note on transformer ordering).
        if (document.Paths is { } paths)
        {
            foreach (var (path, pathItem) in paths)
            {
                if (pathItem.Operations is not { } operations)
                {
                    continue;
                }

                foreach (var (operationType, operation) in operations)
                {
                    await ApplyOperationAsync(
                        document, operation, context,
                        operationType.ToString().ToUpperInvariant(), path,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task ApplyOperationAsync(
        OpenApiDocument document,
        OpenApiOperation operation,
        Microsoft.AspNetCore.OpenApi.OpenApiDocumentTransformerContext context,
        string method,
        string path,
        CancellationToken cancellationToken)
    {
        var spec = Specs.TryGetValue(CreateSpecKey(method, path), out var matched)
            ? matched
            : new RouterOperationSpec(typeof(object), responses: []);

        // Request body (the router endpoints read HttpRequest.Body manually — no [FromBody]
        // metadata exists for the generator to discover).
        if (spec.RequestBodyType is not null)
        {
            var bodySchema = await GetNamedSchemaAsync(
                document, context, spec.RequestBodyType, cancellationToken).ConfigureAwait(false);
            operation.RequestBody = new OpenApiRequestBody
            {
                Description = spec.RequestBodyDescription,
                Required = spec.RequestBodyRequired,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Schema = bodySchema } }
            };
        }
        else
        {
            // The generator auto-derives a request body from the TypedResult of endpoints that
            // declare an HttpRequest parameter (the manual-body pattern); drop it where the
            // operation takes no body (GETs, DELETEs, the rotate endpoint, ...).
            operation.RequestBody = null;
        }

        // Response matrix: replace the generator's responses with the endpoint's real contract
        // (success + every documented error status; the schemas are the named component
        // references of the DTO records, never anonymous objects).
        var responses = new OpenApiResponses();
        foreach (var (code, schemaType, responseDescription) in spec.Responses)
        {
            var responseSchema = await GetNamedSchemaAsync(
                document, context, schemaType, cancellationToken).ConfigureAwait(false);
            responses[code] = new OpenApiResponse
            {
                Description = responseDescription,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Schema = responseSchema } }
            };
        }

        // Security: derived from the PRODUCTION policy called with the route template (the
        // single-segment logic matches template segments; exactly 12 of the 25 operations
        // match). A 401 response is documented on exactly those operations.
        if (ApiSecurityPolicy.IsProtectedRoute(method, path))
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SecuritySchemeName, document)] = []
                }
            ];
            var unauthorizedSchema = await GetNamedSchemaAsync(
                document, context, typeof(OllamaErrorResponse), cancellationToken).ConfigureAwait(false);
            responses["401"] = new OpenApiResponse
            {
                Description =
                    "Missing or invalid API key. Carries 'WWW-Authenticate: Bearer' plus the " +
                    "OllamaErrorResponse JSON body; there is NO rotation grace period — re-read " +
                    "the key after a 401.",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Schema = unauthorizedSchema } }
            };
        }

        operation.Responses = responses;
    }

    /// <summary>
    /// Resolves <paramref name="type"/> to a $ref against a NAMED component schema
    /// (<c>#/components/schemas/{Type.Name}</c>): the 10.x schema service inlines root schemas
    /// (componentization only applies to nested/parameter nodes), so the generated schema is
    /// registered in <c>components.schemas</c> explicitly (first registration wins; later
    /// operations reuse the reference) and the $ref is returned.
    /// </summary>
    private static async Task<IOpenApiSchema> GetNamedSchemaAsync(
        OpenApiDocument document,
        Microsoft.AspNetCore.OpenApi.OpenApiDocumentTransformerContext context,
        Type type,
        CancellationToken cancellationToken)
    {
        var schema = await context.GetOrCreateSchemaAsync(
            type, null, cancellationToken).ConfigureAwait(false);
        if (schema is OpenApiSchemaReference)
        {
            return schema;
        }

        var components = document.Components ??= new OpenApiComponents();
        components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        if (!components.Schemas.ContainsKey(type.Name))
        {
            components.Schemas[type.Name] = schema;
        }

        return new OpenApiSchemaReference(type.Name, document);
    }

    private static readonly IReadOnlyDictionary<string, RouterOperationSpec> Specs =
        new Dictionary<string, RouterOperationSpec>(StringComparer.Ordinal)
        {
            ["GET /api/router/info"] = new(typeof(RouterInfoResponse),
                [("200", typeof(RouterInfoResponse), "Router identity, version, fleet summary and active features.")]),

            ["GET /api/router/fleet"] = new(typeof(RouterFleetResponse),
                [("200", typeof(RouterFleetResponse), "Full fleet hardware topology (configured targets in configuration order, Cloud pseudo-target last).")]),

            ["GET /api/router/hardware"] = new(typeof(NodeHardwareProfile),
                [("200", typeof(NodeHardwareProfile), "The local node's own hardware profile (cached after the first probe).")]),

            ["GET /api/router/fleet/hardware"] = new(typeof(RouterFleetHardwareResponse),
                [("200", typeof(RouterFleetHardwareResponse), "Fleet hardware topology (parallel peer probes, fail-soft per node).")]),

            ["PUT /api/router/fleet/hardware/*"] = new(requestBodyType: typeof(NodeHardwareProfile),
                requestRequired: true,
                requestDescription:
                    "The hardware profile to store for the target (manual push for bare Ollama " +
                    "targets without a router daemon). 'nodeName' is required.",
                responses:
                [
                    ("200", typeof(NodeHardwareProfile), "The stored profile."),
                    ("400", typeof(OllamaErrorResponse), "Invalid or missing hardware profile body (nodeName required)."),
                    ("404", typeof(OllamaErrorResponse), "target not found: {targetName}")
                ]),

            ["POST /api/router/benchmark"] = new(requestBodyType: typeof(RouterBenchmarkRequest),
                requestDescription:
                    "Optional: a null/absent body is valid and benchmarks ALL effective non-Cloud " +
                    "targets. A malformed body → 400. 'target' null/absent = all effective targets; " +
                    "'roles'/'model' filter the catalog candidates (null/empty = all).",
                responses:
                [
                    ("202", typeof(RouterBenchmarkRunResponse), "Accepted and queued (non-blocking — the run loop drains the targets sequentially in the background)."),
                    ("400", typeof(OllamaErrorResponse), "Malformed request body."),
                    ("404", typeof(OllamaErrorResponse), "target not found: {target}"),
                    ("409", typeof(OllamaErrorResponse), "benchmark run already in progress")
                ]),

            ["GET /api/router/benchmark/results"] = new(typeof(RouterBenchmarkResultsResponse),
                [("200", typeof(RouterBenchmarkResultsResponse), "Every known target result (persisted or in-flight; in-flight rows carry status \"Running\").")]),

            ["DELETE /api/router/benchmark/results"] = new(typeof(RouterBenchmarkClearedResponse),
                [("200", typeof(RouterBenchmarkClearedResponse), "Results cleared (in-memory + benchmarks.json + benchmark-seeded speed entries; \"learned\" entries untouched; in-flight runs keep running).")]),

            ["GET /api/router/models"] = new(typeof(RouterModelsResponse),
                [("200", typeof(RouterModelsResponse), "Zero-config model capability catalog (merged fleet catalog + cloud syntheses).")]),

            ["GET /api/router/models/*"] = new(typeof(RouterModelDetailResponse),
                responses:
                [
                    ("200", typeof(RouterModelDetailResponse), "Capability detail + per-target qualification matrix (Cloud pseudo-target never listed; cloud-only models carry an empty matrix)."),
                    ("404", typeof(OllamaErrorResponse), "model not found")
                ]),

            ["GET /api/router/cartography"] = new(typeof(RouterCartographyResponse),
                [("200", typeof(RouterCartographyResponse), "Fleet cartography: nodes, per-model deployment matrix and the flat loaded-process list (snapshot reads; never triggers an /api/ps refresh).")]),

            ["POST /api/router/vram/reclaim"] = new(requestBodyType: typeof(RouterVramReclaimRequest),
                requestDescription:
                    "Optional: an absent/empty body is valid, a malformed body falls through to the " +
                    "query values, and an explicit body is authoritative per field (body wins over " +
                    "query: the vram-reclaim pattern). 'target' null/absent = ALL effective targets " +
                    "(fail-soft per target; the Cloud pseudo-target is never part of the effective " +
                    "list); 'disableTarget' default false.",
                responses:
                [
                    ("200", typeof(RouterVramReclaimResponse), "Reclaim report (fail-soft: success:false + message \"no targets configured\" when the effective target list is empty; unreachable daemons fail soft per target)."),
                    ("404", typeof(OllamaErrorResponse), "target not found: {target}")
                ]),

            ["POST /api/router/fleet/unload"] = new(requestBodyType: typeof(RouterVramReclaimRequest),
                requestDescription:
                    "Exact alias of POST /api/router/vram/reclaim (identical handler, parameters and " +
                    "response shape). Optional: an absent/empty body is valid, a malformed body falls " +
                    "through to the query values, and an explicit body is authoritative per field " +
                    "(body wins over query). 'target' null/absent = ALL effective targets (fail-soft " +
                    "per target; the Cloud pseudo-target is never part of the effective list); " +
                    "'disableTarget' default false.",
                responses:
                [
                    ("200", typeof(RouterVramReclaimResponse), "Unload report (fail-soft: success:false + message \"no targets configured\" when the effective target list is empty; unreachable daemons fail soft per target)."),
                    ("404", typeof(OllamaErrorResponse), "target not found: {target}")
                ]),

            ["POST /api/router/context/evaluate"] = new(requestBodyType: typeof(RouterContextEvaluateRequest),
                requestRequired: true,
                requestDescription:
                    "Required: 'model' (non-empty) and 'estimatedTokens' (>= 1); a malformed body " +
                    "falls through to validation (400). 'clientRequestedNumCtx' mirrors the client's " +
                    "static options.num_ctx (if any).",
                responses:
                [
                    ("200", typeof(RouterContextEvaluateResponse), "Stateless routing + dynamic context sizing preview (no LLM inference)."),
                    ("400", typeof(OllamaErrorResponse), "model is required / estimatedTokens must be >= 1"),
                    ("503", typeof(OllamaErrorResponse), "no available routing target")
                ]),

            ["POST /api/router/models/*"] = new(requestBodyType: typeof(RouterModelPreloadRequest),
                requestDescription:
                    "Optional: an absent/empty body is valid, a malformed body falls through to the " +
                    "query values, and an explicit body is authoritative per field (body wins over " +
                    "query). 'keepAliveSeconds' (-1 = indefinite, positive = seconds; 0 / <= -2 → " +
                    "400), 'contextIntentTokens' (>= 1), 'target' (pin overrides selection, not the " +
                    "safety gates).",
                responses:
                [
                    ("200", typeof(RouterModelPreloadResponse), "Selection report + applied keep_alive / num_ctx (fail-soft: a failed load/confirmation is 200 with loadAccepted/residencyConfirmed false, never 5xx)."),
                    ("400", typeof(OllamaErrorResponse), "Invalid preload path (missing /preload suffix), keepAliveSeconds or contextIntentTokens validation."),
                    ("404", typeof(OllamaErrorResponse), "model or pinned target unknown."),
                    ("503", typeof(OllamaErrorResponse), "no capable target available for the requested context (candidates carry the skip reasons).")
                ]),

            ["GET /api/router/preload/preview"] = new(typeof(RouterModelPreloadResponse),
                responses:
                [
                    ("200", typeof(RouterModelPreloadResponse), "Side-effect-free preview: the EXACT same selection as the POST with zero daemon calls (loadAccepted/residencyConfirmed/preloadDurationMs null)."),
                    ("400", typeof(OllamaErrorResponse), "model query parameter is required / keepAliveSeconds or contextIntentTokens validation."),
                    ("404", typeof(OllamaErrorResponse), "model or pinned target unknown."),
                    ("503", typeof(OllamaErrorResponse), "no capable target available for the requested context.")
                ]),

            ["GET /api/router/config"] = new(typeof(RouterConfigResponse),
                [("200", typeof(RouterConfigResponse), "Full live router configuration (NO key material — the apiSecurity section carries settings only; the Cloud pseudo-target is never in targets).")]),

            ["PUT /api/router/config"] = new(requestBodyType: typeof(UpdateRouterConfigRequest),
                requestRequired: true,
                requestDescription:
                    "Required: a partial per-section update — any subset of cloud / dynamicContext / " +
                    "recording / resilience / pricing (all five null/absent → 400). Persists ONLY the " +
                    "provided sections to config/config.json (merge) and applies them to the live " +
                    "options (no restart).",
                responses:
                [
                    ("200", typeof(UpdateRouterConfigResponse), "Success echo with the resulting FULL configuration (config omitted on failure)."),
                    ("400", typeof(UpdateRouterConfigResponse), "Validation failure (success:false, config omitted)."),
                    ("500", typeof(UpdateRouterConfigResponse), "Persistence failure (success:false, NO in-memory change).")
                ]),

            ["POST /api/router/targets"] = new(requestBodyType: typeof(AddTargetRequest),
                requestRequired: true,
                requestDescription:
                    "Required: 'name' (non-empty, <= 64 chars, no / \\ :, not \"Cloud\") and 'url' " +
                    "(absolute http(s)); 'testConnectionFirst' default true (an immediate liveness " +
                    "probe runs before persisting; probe failure → 422 with a retry hint).",
                responses:
                [
                    ("201", typeof(AddTargetResponse), "Created: the target row + probe/reporting data (target immediately routable)."),
                    ("400", typeof(AddTargetResponse), "Invalid body (success:false, error set)."),
                    ("409", typeof(AddTargetResponse), "a target named '{name}' already exists (success:false)."),
                    ("422", typeof(AddTargetResponse), "Connection test failed: target unreachable — retry with testConnectionFirst:false (success:false, nothing persisted)."),
                    ("500", typeof(AddTargetResponse), "Persistence failure (success:false, nothing applied).")
                ]),

            ["PUT /api/router/targets/*"] = new(requestBodyType: typeof(UpdateTargetRequest),
                requestRequired: true,
                requestDescription:
                    "Required: partial target update — null/absent = no change (all four null → 400); " +
                    "hardwareHint:\"\" clears the hint; 'url' must be an absolute http(s) URI; " +
                    "'maxParallel' >= 1. An 'enabled' change takes effect on the very next routing " +
                    "decision.",
                responses:
                [
                    ("200", typeof(UpdateTargetResponse), "Updated target (success:true)."),
                    ("400", typeof(UpdateTargetResponse), "Validation failure (success:false)."),
                    ("404", typeof(UpdateTargetResponse), "target not found: {name} (success:false)."),
                    ("500", typeof(UpdateTargetResponse), "Persistence failure (success:false, NO in-memory change).")
                ]),

            ["DELETE /api/router/targets/*"] = new(typeof(DeleteTargetResponse),
                responses:
                [
                    ("200", typeof(DeleteTargetResponse), "Target removed (success:true)."),
                    ("404", typeof(DeleteTargetResponse), "target not found: {name} (success:false)."),
                    ("500", typeof(DeleteTargetResponse), "Persistence failure (success:false, NO in-memory change).")
                ]),

            ["GET /api/router/api-keys"] = new(typeof(ApiKeysListResponse),
                responses:
                [
                    ("200", typeof(ApiKeysListResponse), "Masked user key list (name + timestamps ONLY — never the hash or the raw key; empty array when none exist)."),
                    ("500", typeof(OllamaErrorResponse), "router API key store is not available in this host")
                ]),

            ["POST /api/router/api-keys"] = new(requestBodyType: typeof(ApiKeyCreateRequest),
                requestRequired: true,
                requestDescription:
                    "Required: 'name' (<= 64 chars, no whitespace / slash / colon / control " +
                    "characters, case-insensitively unique); 'validFor' is an OPTIONAL integer " +
                    "NUMBER OF HOURS (e.g. 168 = 7 days; omitted/null = no expiry; < 1 → 400).",
                responses:
                [
                    ("201", typeof(ApiKeyCreatedResponse), "Created: the raw base64url key is returned EXACTLY ONCE (only its SHA-256 hash is persisted in config/api-keys.json)."),
                    ("400", typeof(OllamaErrorResponse), "invalid name or validFor < 1"),
                    ("409", typeof(OllamaErrorResponse), "an API key named '{name}' already exists"),
                    ("500", typeof(OllamaErrorResponse), "persistence failure or missing key store")
                ]),

            ["DELETE /api/router/api-keys/*"] = new(typeof(ApiKeyDeletedResponse),
                responses:
                [
                    ("200", typeof(ApiKeyDeletedResponse), "Deleted (success:true)."),
                    ("404", typeof(ApiKeyDeletedResponse), "API key not found: {name} (success:false)."),
                    ("500", typeof(OllamaErrorResponse), "router API key store is not available in this host")
                ]),

            ["POST /api/router/api-keys/default/rotate"] = new(typeof(ApiKeyRotatedResponse),
                responses:
                [
                    ("200", typeof(ApiKeyRotatedResponse), "Rotated: the NEW raw default key (returned exactly once; also logged at INFO) — the previous default key is dead instantly (no grace period, same semantics as an auto-rotation tick)."),
                    ("500", typeof(OllamaErrorResponse), "router API key store is not available in this host")
                ])
        };

    /// <summary>
    /// Canonical spec-table key: <c>METHOD /a/b/{any}</c> with static segments lowercased and
    /// every route parameter collapsed to <c>*</c> (robust to the casing/spelling of the
    /// emitted catch-all templates).
    /// </summary>
    private static string CreateSpecKey(string method, string path)
    {
        var segments = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var canonical = string.Join(
            "/",
            segments.Select(segment =>
                segment[0] == '{'
                    ? "*"
                    : segment.ToLowerInvariant()));
        return method.ToUpperInvariant() + " /" + canonical;
    }
}

/// <summary>
/// The documented contract of one router operation: the optional JSON request body type
/// (+ required flag / merge-semantics description) and the response matrix
/// (status code string → schema type → description). All schema types are the AOT
/// source-generated DTO records of <c>OllamaRouterJsonSerializerContext</c>.
/// </summary>
internal sealed record RouterOperationSpec(
    Type? requestBodyType,
    bool requestRequired = false,
    string? requestDescription = null,
    (string Code, Type SchemaType, string Description)[] responses = null!)
{
    public Type? RequestBodyType { get; } = requestBodyType;
    public bool RequestBodyRequired { get; } = requestRequired;
    public string? RequestBodyDescription { get; } = requestDescription;
    public (string Code, Type SchemaType, string Description)[] Responses { get; } = responses;

    /// <summary>Bodyless operation: only the response matrix (no request body).</summary>
    public RouterOperationSpec(Type responseType, (string, Type, string)[] responses)
        : this(null, false, null, responses)
    {
    }
}
