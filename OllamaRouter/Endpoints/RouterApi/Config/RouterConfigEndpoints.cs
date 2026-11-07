using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Configuration;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Routing;
using System.Text.Json;

namespace OllamaRouter.Endpoints.RouterApi.Config;

/// <summary>
/// The dynamic fleet configuration / Setup API endpoints of the OllamaRouter programmatic
/// management API: the full live configuration read and the partial per-section update,
/// persisted to config/config.json and applied to the live IOptionsMonitor-bound options
/// without a restart.
/// </summary>

public static class RouterConfigEndpoints
{
    /// <summary>
    /// Maps the router configuration endpoints (<c>GET /api/router/config</c>, <c>PUT /api/router/config</c>).
    /// </summary>
    public static WebApplication MapRouterConfigEndpoints(this WebApplication app)
    {
        // ------------------------------------------------------------------
        //  Dynamic fleet configuration / Setup API (task #19): in-app provisioning of the
        //  compute fleet + router settings, persisted to config/config.json and applied to
        //  the live IOptionsMonitor-bound options without a restart (inference dispatch path
        //  only — see the class-level YARP scope note).
        // ------------------------------------------------------------------
        app.MapGet("/api/router/config", (IOptionsMonitor<OllamaRouterOptions> options) =>
        {
            return Results.Json(
                BuildRouterConfigResponse(options.CurrentValue),
                OllamaRouterJsonSerializerContext.Default.RouterConfigResponse);
        })
        .WithName("GetRouterConfig")
        .WithTags("Setup & Configuration")
        .WithSummary("Full live router configuration (protected: API key required).")
        .WithDescription("The effective fleet targets (configuration order; the Cloud pseudo-target is never listed — it is a "
            + "relay, never a fleet member), cloud overflow, dynamic context, recording, failure resilience, pricing and the "
            + "apiSecurity section SETTINGS (requireKeyForAllEndpoints, defaultKeyRotationHours). NO key material is ever "
            + "exposed. Protected by the API-key middleware (default protected set).");
        app.MapPut("/api/router/config", PutRouterConfigHandler)
            .WithName("PutRouterConfig")
            .WithTags("Setup & Configuration")
            .WithSummary("Partial per-section router configuration update (protected: API key required).")
            .WithDescription("Update any subset of cloud / dynamicContext / recording / resilience / pricing (all five null/absent "
                + "→ 400). Persists ONLY the provided sections to config/config.json (merge, preserving all other user keys "
                + "including Recording:TokenMetricsCsv) and applies them to the live in-memory options immediately (no restart). "
                + "Validation (400): negative cooldown/timeout/maxFiles/resilience values, negative pricing rates, empty "
                + "currency. 500 = persistence failure (NO in-memory change — the file write happens first). Env vars / CLI args "
                + "sit above config/config.json: an environment-pinned value stays effective after a PUT. Echoes the resulting "
                + "full configuration. Protected by the API-key middleware (default protected set).");
        return app;
    }

    // ------------------------------------------------------------------
    //  Dynamic fleet configuration / Setup API (task #19)
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the full configuration response shared by <c>GET /api/router/config</c> and the
    /// <c>PUT /api/router/config</c> echo. All values are read from the live options instance;
    /// NO key material is exposed (the API-key system of task #50 stores the default key
    /// protected in config/default-key.dat and user keys as hashes only — the response carries
    /// the <c>apiSecurity</c> SECTION SETTINGS, never any key). The Cloud pseudo-target is not
    /// part of <c>targets</c> (it is a relay, never a fleet member).
    /// </summary>
    private static RouterConfigResponse BuildRouterConfigResponse(OllamaRouterOptions options)
    {
        var targets = options.GetEffectiveTargets()
            .Select(t => new RouterTargetConfigDto(
                t.Name ?? string.Empty,
                t.Url ?? string.Empty,
                t.Enabled,
                t.HardwareHint,
                t.MaxParallel))
            .ToList();

        var recording = options.Recording ?? new RecordingOptions();
        var pricing = options.Pricing ?? new PricingOptions();
        return new RouterConfigResponse(
            targets,
            new RouterCloudConfigDto(
                options.CloudOverflow.Enabled,
                options.CloudOverflow.DefaultModel,
                options.CloudOverflow.PreferredRelayTarget,
                options.IsCloudOverflowConfigured()),
            new RouterDynamicContextConfigDto(
                options.DynamicContext.Enabled,
                options.DynamicContext.DenseClampEnabled,
                options.DynamicContext.AutoShrinkCooldownTurns,
                options.DynamicContext.AutoShrinkIdleTimeoutSeconds),
            new RouterRecordingConfigDto(
                recording.Enabled,
                recording.OutputDirectory,
                recording.MaxFiles),
            new RouterResilienceConfigDto(
                options.MaxConsecutiveFailures,
                options.ConsecutiveFailuresTokenTolerance),
            new RouterPricingConfigDto(
                pricing.PromptPricePerMillion,
                pricing.CompletionPricePerMillion,
                pricing.PricePerMillion,
                pricing.Currency),
            new ApiSecurityConfigDto(
                options.ApiSecurity.RequireKeyForAllEndpoints,
                options.ApiSecurity.DefaultKeyRotationHours));
    }

    /// <summary>
    /// Handler of <c>PUT /api/router/config</c>: partial per-section update of cloud,
    /// dynamicContext, recording, resilience and/or pricing (any subset; all-absent → 400).
    /// Persists ONLY the provided sections to <c>config/config.json</c> (merge, preserving all
    /// other user keys incl. <c>Recording:TokenMetricsCsv</c>) and applies them to the live
    /// in-memory options instance immediately (no restart required), then echoes the resulting
    /// full configuration. Validation (400): negative cooldown/timeout/maxFiles/resilience
    /// values, negative pricing rates, empty currency. Persistence failure → 500 with NO
    /// in-memory change (the file write happens first, see IRouterConfigPersistence).
    /// Precedence note: env vars / CLI args sit above config/config.json — an environment-pinned
    /// value stays effective in the bound configuration after a PUT (the in-memory apply shows
    /// the API value until the next rebind; standard .NET behavior).
    /// </summary>
    private static async Task<IResult> PutRouterConfigHandler(
        HttpRequest request,
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        UpdateRouterConfigRequest? updateRequest;
        try
        {
            updateRequest = await JsonSerializer.DeserializeAsync(
                request.Body,
                OllamaRouterJsonSerializerContext.Default.UpdateRouterConfigRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Malformed JSON body → 400 below.
            updateRequest = null;
        }

        var validationError = ValidateRouterConfigUpdate(updateRequest);
        if (validationError is not null)
        {
            return ConfigFailure(validationError);
        }

        var persistence = serviceProvider.GetService<IRouterConfigPersistence>();
        if (persistence is null)
        {
            // A host-level misconfiguration (persistence service not registered), not a client
            // error — same 500 semantics as the target CRUD handlers.
            return ConfigFailure(
                "router configuration persistence is not available in this host",
                StatusCodes.Status500InternalServerError);
        }

        var result = await persistence.PersistConfigAsync(updateRequest!, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return ConfigFailure($"failed to persist router configuration: {result.Error}");
        }

        return Results.Json(new UpdateRouterConfigResponse(
            true,
            "Router configuration updated successfully.",
            BuildRouterConfigResponse(options.CurrentValue)),
            OllamaRouterJsonSerializerContext.Default.UpdateRouterConfigResponse);
    }

    /// <summary>
    /// Validates the <c>PUT /api/router/config</c> body: returns a 400 error message, or null
    /// when the update is acceptable (at least one section present, no negative knob values).
    /// </summary>
    private static string? ValidateRouterConfigUpdate(UpdateRouterConfigRequest? request)
    {
        if (request is null ||
            (request.Cloud is null && request.DynamicContext is null &&
             request.Recording is null && request.Resilience is null && request.Pricing is null))
        {
            return "no configuration sections provided";
        }

        if (request.DynamicContext is { } dynamicContext &&
            (dynamicContext.AutoShrinkCooldownTurns < 0 || dynamicContext.AutoShrinkIdleTimeoutSeconds < 0))
        {
            return "dynamicContext.autoShrinkCooldownTurns and dynamicContext.autoShrinkIdleTimeoutSeconds must be >= 0";
        }

        if (request.Recording is { } recording && recording.MaxFiles < 0)
        {
            return "recording.maxFiles must be >= 0";
        }

        if (request.Resilience is { } resilience &&
            (resilience.MaxConsecutiveFailures < 0 || resilience.ConsecutiveFailuresTokenTolerance < 0))
        {
            return "resilience.maxConsecutiveFailures and resilience.consecutiveFailuresTokenTolerance must be >= 0";
        }

        if (request.Pricing is { } pricing &&
            ((pricing.PromptPricePerMillion is { } prompt && prompt < 0) ||
             (pricing.CompletionPricePerMillion is { } completion && completion < 0) ||
             (pricing.PricePerMillion is { } flat && flat < 0) ||
             string.IsNullOrWhiteSpace(pricing.Currency)))
        {
            return "pricing rates must be >= 0 and currency must be non-empty";
        }

        return null;
    }

    private static IResult ConfigFailure(string error, int statusCode = StatusCodes.Status400BadRequest) =>
        Results.Json(
            new UpdateRouterConfigResponse(false, error),
            OllamaRouterJsonSerializerContext.Default.UpdateRouterConfigResponse,
            statusCode: statusCode);
}
