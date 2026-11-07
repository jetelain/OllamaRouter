using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Configuration;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using System.Text.Json;

namespace OllamaRouter.Endpoints.RouterApi.Targets;

/// <summary>
/// The target provisioning endpoints of the OllamaRouter programmatic management API:
/// add / update / delete fleet targets, persisted to config/config.json (file-first,
/// atomic) and applied to the live options without a restart.
/// </summary>

public static class RouterTargetsEndpoints
{
    /// <summary>
    /// Maps the target provisioning endpoints (<c>POST/PUT/DELETE /api/router/targets[/{name}]</c>).
    /// </summary>
    public static WebApplication MapRouterTargetsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/router/targets", AddTargetHandler)
            .WithName("PostRouterTarget")
            .WithTags("Setup & Configuration")
            .WithSummary("Add a new compute target to the fleet (protected: API key required).")
            .WithDescription("Validation (400): invalid name (empty, > 64 chars, contains / \\ :, equals 'Cloud'), non-absolute / "
                + "non-http(s) URL, maxParallel < 1; duplicate name (case-insensitive) → 409. When testConnectionFirst (default "
                + "true) the target is probed immediately (bounded by the probe client timeout); a probe failure → 422 with a "
                + "retry hint (nothing persisted). On success (201) the target is persisted to config/config.json (atomic, "
                + "file-first) + applied to the live options, the model catalog is seeded and the target is immediately routable "
                + "on the inference (completion) dispatch path. 500 = persistence failure (nothing applied). Protected by the "
                + "API-key middleware (default protected set).");
        app.MapPut("/api/router/targets/{name}", UpdateTargetHandler)
            .WithName("PutRouterTarget")
            .WithTags("Setup & Configuration")
            .WithSummary("Update an existing target (protected: API key required).")
            .WithDescription("Updates target properties (url, enabled, hardwareHint, maxParallel) of an existing target "
                + "(case-insensitive lookup, unknown name → 404). Field semantics: null/absent = no change (all four null → 400); "
                + "hardwareHint:'' clears the hint; url must be an absolute http(s) URI (trailing / stripped); maxParallel >= 1. "
                + "Persists the merge to config/config.json (file-first, atomic) + applies to the live options — an enabled "
                + "change takes effect on the very next routing decision (no restart). 500 = persistence failure (NO in-memory "
                + "change). Protected by the API-key middleware (default protected set).");
        app.MapDelete("/api/router/targets/{name}", DeleteTargetHandler)
            .WithName("DeleteRouterTarget")
            .WithTags("Setup & Configuration")
            .WithSummary("Remove a target from the fleet (protected: API key required).")
            .WithDescription("Removes the target (case-insensitive lookup, unknown name → 404) from the in-memory fleet and from "
                + "config/config.json (file-first, atomic). Liveness slots / session-affinity leases of the removed name are "
                + "orphaned but inert (the re-prober and routing only ever see live fleet names; leases expire by TTL). 500 = "
                + "persistence failure (NO in-memory change). Protected by the API-key middleware (default protected set).");
        return app;
    }

    /// <summary>
    /// Handler of <c>POST /api/router/targets</c>: adds a new compute target to the fleet.
    /// Validation (400): invalid name (empty, > 64 chars, contains / \ :, equals "Cloud"),
    /// non-absolute / non-http(s) URL, maxParallel &lt; 1; duplicate name (case-insensitive) →
    /// 409. When <c>testConnectionFirst</c> (default true) the target is probed immediately via
    /// <c>ITargetLivenessService.EnsureKnownAsync</c> (bounded by the probe client timeout); a
    /// probe failure → 422 with a retry hint (nothing persisted). On success the target is
    /// persisted to <c>config/config.json</c> (atomic, file-first) + applied to the live options,
    /// the model catalog is seeded (<c>IModelCatalogCacheService.RefreshAsync</c>, bounded by the
    /// 4 s catalog client) and the target is immediately routable on the inference (completion)
    /// dispatch path (the middleware resolves the candidate list live per request). Status
    /// matrix: 201 created / 400 validation / 409 duplicate / 422 probe failed / 500
    /// persistence failure (nothing applied).
    /// </summary>
    private static async Task<IResult> AddTargetHandler(
        HttpRequest request,
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        AddTargetRequest? addRequest;
        try
        {
            addRequest = await JsonSerializer.DeserializeAsync(
                request.Body,
                OllamaRouterJsonSerializerContext.Default.AddTargetRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Malformed JSON body → 400 below.
            addRequest = null;
        }

        if (addRequest is null)
        {
            return AddTargetFailure("invalid or missing target body", StatusCodes.Status400BadRequest);
        }

        var effectiveTargets = options.CurrentValue.GetEffectiveTargets();
        // JSON omission of a non-nullable string field deserializes to null at runtime,
        // so the null-fallbacks are defensive (and keep the 400 path deterministic).
        var name = addRequest.Name?.Trim() ?? string.Empty;
        var rawUrl = addRequest.Url?.Trim() ?? string.Empty;
        var maxParallel = addRequest.MaxParallel;
        var hardwareHint = addRequest.HardwareHint;
        hardwareHint = string.IsNullOrWhiteSpace(hardwareHint) ? null : hardwareHint.Trim();

        if (name.Length == 0 || name.Length > 64 ||
            name.IndexOfAny(InvalidTargetNameChars) >= 0 ||
            string.Equals(name, RoutingTarget.Cloud.Value, StringComparison.OrdinalIgnoreCase))
        {
            return AddTargetFailure(
                "invalid target name: must be non-empty, max 64 characters, contain no '/', '\\' or ':' and must not be 'Cloud'",
                StatusCodes.Status400BadRequest);
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return AddTargetFailure(
                $"invalid target url '{rawUrl}': must be an absolute http(s) URI", StatusCodes.Status400BadRequest);
        }

        if (maxParallel < 1)
        {
            return AddTargetFailure("maxParallel must be >= 1", StatusCodes.Status400BadRequest);
        }

        if (effectiveTargets.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return AddTargetFailure(
                $"a target named '{name}' already exists", StatusCodes.Status409Conflict);
        }

        var normalizedUrl = url.ToString().TrimEnd('/');

        // The candidate list for the probe + the eventual persist (in-memory order =
        // effective targets + the new one).
        var newTargets = new List<OllamaTargetOptions>(effectiveTargets.Count + 1);
        newTargets.AddRange(effectiveTargets);
        newTargets.Add(new OllamaTargetOptions
        {
            Name = name,
            Url = normalizedUrl,
            Enabled = true,
            MaxParallel = maxParallel,
            HardwareHint = hardwareHint
        });

        // Immediate liveness probe (coalesced, bounded by the probe client timeout). Skipped
        // when testConnectionFirst is false or the liveness service is not resolvable
        // (minimal hosts): the target is still added.
        // The liveness service resolves the probe URL from the live options, so the new target
        // is applied to memory FIRST (temporarily): on probe failure or persistence failure the
        // previous list is restored — a 422/500 never leaves the new target in memory (the
        // file is only written by the persistence service, which runs last).
        var connectionTested = false;
        var livenessState = TargetLivenessState.Unknown;
        long? latencyMs = null;
        var liveness = serviceProvider.GetService<ITargetLivenessService>();
        if (addRequest.TestConnectionFirst && liveness is not null)
        {
            connectionTested = true;
            var current = options.CurrentValue;
            current.Targets = new List<OllamaTargetOptions>(newTargets);
            try
            {
                livenessState = await liveness.EnsureKnownAsync(new RoutingTarget(name), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                livenessState = TargetLivenessState.Offline;
            }

            latencyMs = liveness.GetEntries()?.GetValueOrDefault(new RoutingTarget(name))?.LastProbeLatencyMs;

            if (livenessState == TargetLivenessState.Offline)
            {
                current.Targets = new List<OllamaTargetOptions>(effectiveTargets);
                return Results.Json(new AddTargetResponse(
                    false,
                    connectionTested,
                    livenessState.ToString(),
                    0,
                    null,
                    latencyMs,
                    $"Connection test failed: target '{name}' at {normalizedUrl} is unreachable " +
                        "(liveness: Offline). Retry with testConnectionFirst: false to add it anyway."),
                    OllamaRouterJsonSerializerContext.Default.AddTargetResponse,
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }

        // Seed the model catalog (bounded by the catalog client's 4 s timeout; fail-soft: a
        // discovery hiccup never blocks the add — the liveness rejoin auto-refresh covers it).
        var modelsDiscovered = 0;
        var catalog = serviceProvider.GetService<IModelCatalogCacheService>();
        if (catalog is not null)
        {
            try
            {
                modelsDiscovered = (await catalog.RefreshAsync(normalizedUrl, cancellationToken).ConfigureAwait(false)).Count;
            }
            catch
            {
                modelsDiscovered = 0;
            }
        }

        var persistence = serviceProvider.GetService<IRouterConfigPersistence>();
        if (persistence is null)
        {
            if (connectionTested)
            {
                options.CurrentValue.Targets = new List<OllamaTargetOptions>(effectiveTargets); // roll back the temp apply
            }

            return AddTargetFailure(
                "router configuration persistence is not available in this host", StatusCodes.Status500InternalServerError);
        }

        var result = await persistence.PersistTargetsAsync(newTargets, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            if (connectionTested)
            {
                options.CurrentValue.Targets = new List<OllamaTargetOptions>(effectiveTargets); // file write failed: roll back
            }

            return AddTargetFailure(
                $"failed to persist target: {result.Error}", StatusCodes.Status500InternalServerError);
        }

        return Results.Json(new AddTargetResponse(
            true,
            connectionTested,
            livenessState.ToString(),
            modelsDiscovered,
            new RouterTargetConfigDto(name, normalizedUrl, true, hardwareHint, maxParallel),
            latencyMs),
            OllamaRouterJsonSerializerContext.Default.AddTargetResponse,
            statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// Handler of <c>PUT /api/router/targets/{name}</c>: updates target properties (url,
    /// enabled, hardwareHint, maxParallel) of an existing target (case-insensitive lookup,
    /// unknown name → 404). Field semantics: null/absent = no change (all four null → 400);
    /// <c>hardwareHint: ""</c> clears the hint; <c>url</c> must be an absolute http(s) URI
    /// (trailing / stripped); <c>maxParallel</c> &gt;= 1. Persists the merge to
    /// <c>config/config.json</c> (file-first, atomic) + applies to the live options — an
    /// <c>enabled</c> change takes effect on the very next routing decision (no restart).
    /// Persistence failure → 500 with NO in-memory change.
    /// </summary>
    private static async Task<IResult> UpdateTargetHandler(
        HttpRequest request,
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        string name,
        CancellationToken cancellationToken)
    {
        UpdateTargetRequest? updateRequest;
        try
        {
            updateRequest = await JsonSerializer.DeserializeAsync(
                request.Body,
                OllamaRouterJsonSerializerContext.Default.UpdateTargetRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Malformed JSON body → 400 below.
            updateRequest = null;
        }

        if (updateRequest is null ||
            (updateRequest.Url is null && updateRequest.Enabled is null &&
             updateRequest.HardwareHint is null && updateRequest.MaxParallel is null))
        {
            return UpdateTargetFailure($"no target fields provided for '{name}'", StatusCodes.Status400BadRequest);
        }

        string? normalizedUrl = null;
        if (updateRequest.Url is { } rawUrl)
        {
            var trimmedUrl = rawUrl.Trim();
            if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var url) ||
                (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            {
                return UpdateTargetFailure(
                    $"invalid target url '{trimmedUrl}': must be an absolute http(s) URI", StatusCodes.Status400BadRequest);
            }
            normalizedUrl = url.ToString().TrimEnd('/');
        }

        if (updateRequest.MaxParallel is { } maxParallel && maxParallel < 1)
        {
            return UpdateTargetFailure("maxParallel must be >= 1", StatusCodes.Status400BadRequest);
        }

        var effectiveTargets = options.CurrentValue.GetEffectiveTargets();
        var index = Array.FindIndex(effectiveTargets.ToArray(), t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return UpdateTargetFailure($"target not found: {name}", StatusCodes.Status404NotFound);
        }

        var updated = CopyTargetOptions(effectiveTargets[index]);
        if (normalizedUrl is not null)
        {
            updated.Url = normalizedUrl;
        }
        if (updateRequest.Enabled is { } enabled)
        {
            updated.Enabled = enabled;
        }
        if (updateRequest.HardwareHint is { } hardwareHint)
        {
            updated.HardwareHint = string.IsNullOrWhiteSpace(hardwareHint) ? null : hardwareHint.Trim();
        }
        if (updateRequest.MaxParallel is { } parallel)
        {
            updated.MaxParallel = parallel;
        }

        var newTargets = effectiveTargets.ToList();
        newTargets[index] = updated;

        var persistence = serviceProvider.GetService<IRouterConfigPersistence>();
        if (persistence is null)
        {
            return UpdateTargetFailure(
                "router configuration persistence is not available in this host", StatusCodes.Status500InternalServerError);
        }

        var result = await persistence.PersistTargetsAsync(newTargets, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return UpdateTargetFailure(
                $"failed to persist target: {result.Error}", StatusCodes.Status500InternalServerError);
        }

        return Results.Json(new UpdateTargetResponse(
            true,
            $"Target '{updated.Name}' updated successfully.",
            new RouterTargetConfigDto(
                updated.Name ?? name,
                updated.Url ?? string.Empty,
                updated.Enabled,
                updated.HardwareHint,
                updated.MaxParallel)),
            OllamaRouterJsonSerializerContext.Default.UpdateTargetResponse);
    }

    /// <summary>
    /// Handler of <c>DELETE /api/router/targets/{name}</c>: removes the target (case-insensitive
    /// lookup, unknown name → 404) from the in-memory fleet and from
    /// <c>config/config.json</c> (file-first, atomic). Liveness slots / session-affinity leases
    /// of the removed name are orphaned but inert (the re-prober and routing only ever see live
    /// fleet names; leases expire by TTL). Persistence failure → 500 with NO in-memory change.
    /// </summary>
    private static async Task<IResult> DeleteTargetHandler(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        string name,
        CancellationToken cancellationToken)
    {
        var effectiveTargets = options.CurrentValue.GetEffectiveTargets();
        var index = Array.FindIndex(effectiveTargets.ToArray(), t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return Results.Json(
                new DeleteTargetResponse(false, $"target not found: {name}"),
                OllamaRouterJsonSerializerContext.Default.DeleteTargetResponse,
                statusCode: StatusCodes.Status404NotFound);
        }

        var newTargets = effectiveTargets.ToList();
        newTargets.RemoveAt(index);

        var persistence = serviceProvider.GetService<IRouterConfigPersistence>();
        if (persistence is null)
        {
            return Results.Json(
                new DeleteTargetResponse(false,
                    "router configuration persistence is not available in this host"),
                OllamaRouterJsonSerializerContext.Default.DeleteTargetResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var result = await persistence.PersistTargetsAsync(newTargets, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return Results.Json(
                new DeleteTargetResponse(false, $"failed to persist target: {result.Error}"),
                OllamaRouterJsonSerializerContext.Default.DeleteTargetResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Json(
            new DeleteTargetResponse(true, $"Target '{name}' removed successfully."),
            OllamaRouterJsonSerializerContext.Default.DeleteTargetResponse);
    }

    private static readonly char[] InvalidTargetNameChars = ['/', '\\', ':'];
    /// <summary>Field-by-field copy of a target options instance (copy-on-write for PUT).</summary>
    private static OllamaTargetOptions CopyTargetOptions(OllamaTargetOptions target) => new()
    {
        Name = target.Name,
        Url = target.Url,
        Location = target.Location,
        RouterUrl = target.RouterUrl,
        MaxVramMB = target.MaxVramMB,
        KeepAliveSeconds = target.KeepAliveSeconds,
        SpeedFactor = target.SpeedFactor,
        MaxParallel = target.MaxParallel,
        Enabled = target.Enabled,
        HardwareHint = target.HardwareHint
    };
    private static IResult AddTargetFailure(string error, int statusCode) =>
        Results.Json(
            new AddTargetResponse(false, false, TargetLivenessState.Unknown.ToString(), 0, null, null, error),
            OllamaRouterJsonSerializerContext.Default.AddTargetResponse,
            statusCode: statusCode);
    private static IResult UpdateTargetFailure(string error, int statusCode) =>
        Results.Json(
            new UpdateTargetResponse(false, error),
            OllamaRouterJsonSerializerContext.Default.UpdateTargetResponse,
            statusCode: statusCode);
}
