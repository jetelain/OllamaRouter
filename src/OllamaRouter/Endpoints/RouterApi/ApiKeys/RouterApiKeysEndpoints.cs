using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OllamaRouter.Middleware;
using OllamaRouter.Security;
using OllamaRouter.Serialization;
using System.Text.Json;

namespace OllamaRouter.Endpoints.RouterApi.ApiKeys;

/// <summary>
/// The API key management endpoints of the OllamaRouter programmatic management API: every
/// route is key-protected by the ApiKeyAuthMiddleware BY DEFINITION (managing keys
/// requires a valid key).
/// </summary>

public static class RouterApiKeysEndpoints
{
    /// <summary>
    /// Maps the API key management endpoints (<c>/api/router/api-keys[/{name}]</c>, <c>POST /api/router/api-keys/default/rotate</c>).
    /// </summary>
    public static WebApplication MapRouterApiKeysEndpoints(this WebApplication app)
    {
        // ------------------------------------------------------------------
        //  API key management (task #50): every route below is key-protected by the
        //  ApiKeyAuthMiddleware BY DEFINITION (managing keys requires a valid key).
        // ------------------------------------------------------------------
        app.MapGet("/api/router/api-keys", (IServiceProvider serviceProvider) =>
        {
            var keyStore = serviceProvider.GetService<IApiKeyStore>();
            if (keyStore is null)
            {
                return KeyStoreFailure();
            }

            var keys = keyStore.GetUserKeys()
                .Select(k => new ApiKeyInfoResponse(k.Name, k.CreatedAt, k.ExpiresAt))
                .ToList();
            return Results.Json(new ApiKeysListResponse(keys),
                OllamaRouterJsonSerializerContext.Default.ApiKeysListResponse);
        })
        .WithName("GetRouterApiKeys")
        .WithTags("API Key Security")
        .WithSummary("List the user API keys, masked (protected: API key required).")
        .WithDescription("Identity + timestamps ONLY — the response NEVER contains the hash or the raw key value. Empty keys[] "
            + "when no user keys exist. 500 when the key store is unavailable in the host. Key management is protected by the "
            + "API-key middleware BY DEFINITION (managing keys requires a valid key).");
        app.MapPost("/api/router/api-keys", CreateApiKeyHandler)
            .WithName("PostRouterApiKey")
            .WithTags("API Key Security")
            .WithSummary("Create a user API key (protected: API key required).")
            .WithDescription("Body: { name, validFor? } where validFor is an INTEGER NUMBER OF HOURS (e.g. 168 = 7 days; "
                + "omitted/null = no expiry; < 1 → 400). The raw base64url key is returned to the caller EXACTLY ONCE in the 201 "
                + "response — only its SHA-256 hash is persisted (config/api-keys.json, 0600 on Unix). 400 invalid name/validFor; "
                + "409 duplicate name (case-insensitive); 500 persistence failure or missing key store. Key management is "
                + "protected by the API-key middleware BY DEFINITION.");
        app.MapDelete("/api/router/api-keys/{name}", DeleteApiKeyHandler)
            .WithName("DeleteRouterApiKey")
            .WithTags("API Key Security")
            .WithSummary("Delete a user API key (protected: API key required).")
            .WithDescription("Case-insensitive lookup. 200 { success: true } on success; unknown name → 404 { success: false, "
                + "error: 'API key not found: {name}' }; 500 when the key store is unavailable. Key management is protected by "
                + "the API-key middleware BY DEFINITION.");
        app.MapPost("/api/router/api-keys/default/rotate", RotateDefaultApiKeyHandler)
            .WithName("PostRotateRouterDefaultApiKey")
            .WithTags("API Key Security")
            .WithSummary("Rotate the default API key immediately (protected: API key required).")
            .WithDescription("Manually rotates the default key IMMEDIATELY (ignores the elapsed interval — same semantics as an "
                + "auto-rotation tick): the previous default key is dead instantly (no grace period) and the new raw base64url "
                + "key is returned exactly once (it is also logged at INFO by the store). 500 when the key store is unavailable. "
                + "Key management is protected by the API-key middleware BY DEFINITION.");
        return app;
    }

    // ------------------------------------------------------------------
    //  API key management (task #50) — key-protected by the auth middleware by definition
    // ------------------------------------------------------------------

    private static IResult KeyStoreFailure() =>
        Results.Json(
            new OllamaErrorResponse("router API key store is not available in this host"),
            OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
            statusCode: StatusCodes.Status500InternalServerError);
    /// <summary>
    /// Handler of <c>POST /api/router/api-keys</c> (task #50): creates a user API key. Body:
    /// <c>{ name, validFor? }</c> where <c>validFor</c> is an INTEGER NUMBER OF HOURS
    /// (e.g. 168 = 7 days; omitted/null = no expiry; &lt; 1 → 400). The raw base64url key is
    /// returned to the caller EXACTLY ONCE in the 201 response — only its SHA-256 hash is
    /// persisted (config/api-keys.json, 0600 on Unix). Status matrix: 201 created / 400 invalid
    /// name or validFor / 409 duplicate name (case-insensitive) / 500 persistence failure or
    /// missing key store.
    /// </summary>
    private static async Task<IResult> CreateApiKeyHandler(
        HttpRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var keyStore = serviceProvider.GetService<IApiKeyStore>();
        if (keyStore is null)
        {
            return KeyStoreFailure();
        }

        ApiKeyCreateRequest? createRequest;
        try
        {
            createRequest = await JsonSerializer.DeserializeAsync(
                request.Body,
                OllamaRouterJsonSerializerContext.Default.ApiKeyCreateRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            createRequest = null;
        }

        if (createRequest is null)
        {
            return ApiKeyError("invalid or missing API key request body", StatusCodes.Status400BadRequest);
        }

        // JSON omission of a non-nullable string field deserializes to null at runtime, so the
        // null-fallback is defensive (and keeps the 400 path deterministic).
        var name = createRequest.Name?.Trim() ?? string.Empty;
        var result = await keyStore.CreateUserKeyAsync(name, createRequest.ValidFor).ConfigureAwait(false);
        switch (result.Failure)
        {
            case ApiKeyCreateFailure.Duplicate:
                return ApiKeyError(
                    $"an API key named '{name}' already exists", StatusCodes.Status409Conflict);
            case ApiKeyCreateFailure.PersistError:
                return ApiKeyError(
                    $"failed to create the API key: {result.Error}", StatusCodes.Status500InternalServerError);
            case ApiKeyCreateFailure.None when result.Created is { } created:
                return Results.Json(
                    new ApiKeyCreatedResponse(true, created.Name, created.Key, created.CreatedAt, created.ExpiresAt),
                    OllamaRouterJsonSerializerContext.Default.ApiKeyCreatedResponse,
                    statusCode: StatusCodes.Status201Created);
            default:
                return ApiKeyError(result.Error ?? "invalid API key request body", StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Handler of <c>DELETE /api/router/api-keys/{name}</c> (task #50): deletes the user key
    /// (case-insensitive) → 200 { success: true }; unknown name → 404 { success: false, error }.
    /// </summary>
    private static async Task<IResult> DeleteApiKeyHandler(
        IServiceProvider serviceProvider,
        string name,
        CancellationToken cancellationToken)
    {
        var keyStore = serviceProvider.GetService<IApiKeyStore>();
        if (keyStore is null)
        {
            return KeyStoreFailure();
        }

        var deleted = await keyStore.DeleteUserKeyAsync(name).ConfigureAwait(false);
        return deleted
            ? Results.Json(new ApiKeyDeletedResponse(true, null),
                OllamaRouterJsonSerializerContext.Default.ApiKeyDeletedResponse)
            : Results.Json(new ApiKeyDeletedResponse(false, $"API key not found: {name}"),
                OllamaRouterJsonSerializerContext.Default.ApiKeyDeletedResponse,
                statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Handler of <c>POST /api/router/api-keys/default/rotate</c> (task #50): manually rotates
    /// the default key IMMEDIATELY (ignores the elapsed interval — same semantics as an
    /// auto-rotation tick): the previous default key is dead instantly (no grace period) and
    /// the new raw key is returned exactly once (it is also logged at INFO by the store).
    /// </summary>
    private static async Task<IResult> RotateDefaultApiKeyHandler(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var keyStore = serviceProvider.GetService<IApiKeyStore>();
        if (keyStore is null)
        {
            return KeyStoreFailure();
        }

        var key = await keyStore.RotateDefaultKeyAsync().ConfigureAwait(false);
        return Results.Json(
            new ApiKeyRotatedResponse(true, key, keyStore.GetDefaultKeyGeneratedAt()),
            OllamaRouterJsonSerializerContext.Default.ApiKeyRotatedResponse);
    }

    private static IResult ApiKeyError(string error, int statusCode)
        => Results.Json(
            new OllamaErrorResponse(error),
            OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
            statusCode: statusCode);
}
