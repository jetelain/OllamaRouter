using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Serialization;

namespace OllamaRouter.Middleware;

/// <summary>
/// API key enforcement middleware (task #50), placed FIRST in the request pipeline (before the
/// routing middleware, <c>UseRouting</c> and the YARP catch-all) so a 401 short-circuits
/// before header sanitization/dispatch, endpoint selection and proxying.
/// <para>
/// Route decision (per request, LIVE option read): <c>ApiSecurity.RequireKeyForAllEndpoints</c>
/// (all routes protected) OR the default config-affecting route set from
/// <see cref="ApiSecurityPolicy.IsProtectedRoute"/> (path + method based, uniform over mapped
/// endpoints AND proxy paths).
/// </para>
/// <para>
/// Key extraction: an <c>Authorization: Bearer &lt;key&gt;</c> header (scheme case-insensitive)
/// takes precedence, else the <c>X-Api-Key: &lt;key&gt;</c> header (value trimmed). On failure
/// the request is short-circuited with <c>401 Unauthorized</c> + <c>WWW-Authenticate: Bearer</c>
/// + a JSON <c>OllamaErrorResponse</c> body (source-gen serialization, no MVC allocation,
/// NativeAOT-safe); the warning log carries NO key material (only method/path/remote IP).
/// </para>
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private const string XApiKeyHeader = "X-Api-Key";
    private const string BearerSchemePrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly IApiKeyStore _keyStore;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    /// <summary>
    /// Creates the middleware. All dependencies are DI-resolved: the key store is registered
    /// by <c>AddOllamaRouterCore</c> in every host that uses the full pipeline
    /// (<c>UseOllamaRouterPipeline</c>); minimal endpoint-only test hosts never register this
    /// middleware and are unaffected.
    /// </summary>
    public ApiKeyAuthMiddleware(
        RequestDelegate next,
        IOptionsMonitor<OllamaRouterOptions> options,
        IApiKeyStore keyStore,
        ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Pipeline entry: unprotected routes pass through untouched; protected routes without a
    /// valid key get an immediate 401.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var protectedRoute = _options.CurrentValue.ApiSecurity.RequireKeyForAllEndpoints ||
            ApiSecurityPolicy.IsProtectedRoute(request.Method, request.Path.HasValue ? request.Path.Value : null);

        if (!protectedRoute)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var presentedKey = ExtractKey(request);
        if (presentedKey is not null && _keyStore.ValidateKey(presentedKey))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning(
            "Rejected {Method} {Path} from {RemoteIp}: missing or invalid API key.",
            request.Method, request.Path, context.Connection.RemoteIpAddress);
        await WriteUnauthorizedAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the presented key: <c>Authorization</c> with the Bearer scheme (case-insensitive)
    /// wins, else the <c>X-Api-Key</c> header; the value is trimmed. Returns null when neither
    /// header carries a usable value.
    /// </summary>
    private static string? ExtractKey(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(BearerSchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var key = authorization[BearerSchemePrefix.Length..].Trim();
            return key.Length > 0 ? key : null;
        }

        if (request.Headers.TryGetValue(XApiKeyHeader, out var xApiKey))
        {
            var key = xApiKey.ToString().Trim();
            return key.Length > 0 ? key : null;
        }

        return null;
    }

    /// <summary>
    /// Writes the 401 short-circuit: <c>WWW-Authenticate: Bearer</c> + a JSON
    /// <c>OllamaErrorResponse</c> body via the source-gen serializer context (no reflection).
    /// No key material is ever echoed in the response or the logs.
    /// </summary>
    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new OllamaErrorResponse(
            "unauthorized: a valid API key is required for this endpoint (Authorization: Bearer <key> or X-Api-Key: <key>)");
        await context.Response.Body.WriteAsync(
            JsonSerializer.SerializeToUtf8Bytes(body, OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse).AsMemory(),
            context.RequestAborted).ConfigureAwait(false);
    }
}
