using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Configuration;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Shared;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OllamaRouter.Endpoints.RouterApi.Targets;

/// <summary>
/// The target provisioning endpoints of the OllamaRouter programmatic management API:
/// add / update / delete fleet targets, persisted to config/config.json (file-first,
/// atomic) and applied to the live options without a restart.
/// </summary>

public static class RouterTargetsEndpoints
{
    /// <summary>
    /// Task #143: the ssh username charset of the daemon-side hardware probe credential
    /// (POSIX-ish: letters, digits, dot, dash, underscore — 1..64 chars, no spaces / @ / : /
    /// separators, so the value is safe to interpolate into the ssh command's
    /// <c>{user}@{host}</c> argument).
    /// </summary>
    internal static readonly Regex SshUserPattern =
        new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        app.MapPost("/api/router/targets/{name}/reset-streak", ResetTargetStreakHandler)
            .WithName("PostRouterTargetResetStreak")
            .WithTags("Setup & Configuration")
            .WithSummary("Reset a target's failure streak + circuit-breaker state (protected: API key required).")
            .WithDescription("Resets the target's consecutive-failure streak to 0 and clears its circuit-breaker cooldown "
                + "(task #95): the target becomes immediately routable on the very next routing decision (no restart, NO "
                + "config write). Runtime failure-streak state only — liveness state, availability toggles and "
                + "session-affinity leases are untouched. Case-insensitive target lookup; unknown name (or 'Cloud' — "
                + "the pseudo-target is not in the effective fleet) → 404 'target not found: {name}' (checked BEFORE "
                + "the streak-service availability). Protected by the API-key middleware (default protected set). "
                + "Fleet-wide variant: POST /api/router/fleet/reset-streaks.");
        app.MapPost("/api/router/fleet/reset-streaks", ResetFleetStreaksHandler)
            .WithName("PostRouterFleetResetStreaks")
            .WithTags("Identity & Fleet")
            .WithSummary("Reset failure streaks + circuit-breaker state across the whole fleet (protected: API key required).")
            .WithDescription("Resets the consecutive-failure streak of EVERY effective target (configuration order) plus "
                + "the Cloud pseudo-target when cloud overflow is enabled (task #95) — a per-target no-op when the streak "
                + "is untracked. Reports resetCount (the number of targets whose previousStreak was above zero) and the "
                + "per-target previousStreak/reset rows; a second call reports resetCount 0 (idempotent). Runtime "
                + "failure-streak state only — liveness state, availability toggles and session-affinity leases are "
                + "untouched; every reset target is immediately routable on the very next routing decision (no restart, "
                + "NO config write). Protected by the API-key middleware (default protected set). "
                + "Per-target variant: POST /api/router/targets/{name}/reset-streak.");
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
        // Task #61 heterogeneous fields (null-tolerant: JSON omission deserializes to null).
        var apiKey = addRequest.ApiKey;
        var provider = addRequest.Provider;
        var declaredModels = addRequest.Models;

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

        if (!TryParseTargetType(addRequest.Type, out var targetType))
        {
            return AddTargetFailure(
                $"invalid target type '{addRequest.Type}': must be 'ollama', 'openai' or 'osnative' (case-insensitive)",
                StatusCodes.Status400BadRequest);
        }

        if (!string.IsNullOrWhiteSpace(apiKey) && targetType != TargetType.OpenAI)
        {
            return AddTargetFailure(
                "apiKey can only be set on targets of type 'openai'", StatusCodes.Status400BadRequest);
        }

        if (HasBlankModelNames(declaredModels))
        {
            return AddTargetFailure("models must not contain blank names", StatusCodes.Status400BadRequest);
        }

        // Task #84: a negative power draw is invalid (0 = zero-consumption node is valid).
        if (addRequest.PowerWatts is { } requestPowerWatts && requestPowerWatts < 0)
        {
            return AddTargetFailure("powerWatts must be >= 0", StatusCodes.Status400BadRequest);
        }

        // Task #143: the ssh credential of the daemon-side hardware probe — a non-blank
        // sshUser must match the POSIX-ish username charset; sshPort must be 1..65535.
        var addSshUser = string.IsNullOrWhiteSpace(addRequest.SshUser) ? null : addRequest.SshUser;
        if (addSshUser is not null && !SshUserPattern.IsMatch(addSshUser))
        {
            return AddTargetFailure(
                "sshUser must be 1-64 characters of [A-Za-z0-9._-] (letters, digits, dot, dash, underscore only)",
                StatusCodes.Status400BadRequest);
        }

        if (addRequest.SshPort is { } addSshPort && (addSshPort < 1 || addSshPort > 65535))
        {
            return AddTargetFailure("sshPort must be between 1 and 65535", StatusCodes.Status400BadRequest);
        }

        if (effectiveTargets.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return AddTargetFailure(
                $"a target named '{name}' already exists", StatusCodes.Status409Conflict);
        }

        var normalizedUrl = url.ToString().TrimEnd('/');
        var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();
        var modelsList = ToTargetModelConfigs(declaredModels);

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
            HardwareHint = hardwareHint,
            Type = targetType,
            // In-memory options always carry the UNWRAPPED key (the persistence layer wraps
            // it for config/config.json; the bind-time unwrap restores it on the next reload).
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
            Provider = normalizedProvider,
            Models = modelsList,
            PowerWatts = addRequest.PowerWatts,
            SshUser = addSshUser,
            SshPort = addRequest.SshPort
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
        // Non-Ollama targets (task #61) have a SYNTHETIC catalog: the seed count is the
        // declared roster size (no /api/tags probe — the provider does not speak it).
        var modelsDiscovered = 0;
        if (targetType == TargetType.OpenAI)
        {
            modelsDiscovered = modelsList.Count;
        }
        else
        {
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
            new RouterTargetConfigDto(
                name,
                normalizedUrl,
                true,
                hardwareHint,
                maxParallel,
                targetType.ToString().ToLowerInvariant(),
                normalizedProvider,
                modelsList.Count > 0
                    ? modelsList.Select(m => new TargetModelDto(m.Name, m.ContextLength, m.Quantization)).ToList()
                    : null,
                !string.IsNullOrWhiteSpace(apiKey),
                addRequest.PowerWatts,
                addSshUser,
                addRequest.SshPort),
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
             updateRequest.HardwareHint is null && updateRequest.MaxParallel is null &&
             updateRequest.Type is null && updateRequest.ApiKey is null &&
             updateRequest.Provider is null && updateRequest.Models is null &&
             updateRequest.PowerWatts is null && updateRequest.SshUser is null &&
             updateRequest.SshPort is null))
        {
            return UpdateTargetFailure($"no target fields provided for '{name}'", StatusCodes.Status400BadRequest);
        }

        // Task #84: a negative power draw is invalid (0 = zero-consumption node is valid).
        if (updateRequest.PowerWatts is { } updatePowerWatts && updatePowerWatts < 0)
        {
            return UpdateTargetFailure("powerWatts must be >= 0", StatusCodes.Status400BadRequest);
        }

        // Task #143: ssh credential validation (null/absent = no change; "" clears the user;
        // a non-blank value must match the POSIX-ish username charset; sshPort 1..65535) —
        // BEFORE the target lookup/persist, mirroring the other per-field 400s.
        if (updateRequest.SshUser is { } updateSshUserCandidate &&
            !string.IsNullOrWhiteSpace(updateSshUserCandidate) &&
            !SshUserPattern.IsMatch(updateSshUserCandidate))
        {
            return UpdateTargetFailure(
                "sshUser must be 1-64 characters of [A-Za-z0-9._-] (letters, digits, dot, dash, underscore only) or \"\" to clear it",
                StatusCodes.Status400BadRequest);
        }

        if (updateRequest.SshPort is { } updateSshPortCandidate && (updateSshPortCandidate < 1 || updateSshPortCandidate > 65535))
        {
            return UpdateTargetFailure("sshPort must be between 1 and 65535", StatusCodes.Status400BadRequest);
        }

        // Task #61: an explicitly provided type must be one of the known families (null/absent
        // = no change, handled below); the apiKey/type interplay is validated AFTER the target
        // lookup (the effective type may come from the EXISTING target when the request does
        // not change it).
        var typeProvided = updateRequest.Type is not null;
        var targetTypeValue = TargetType.Ollama;
        if (typeProvided && !TryParseTargetType(updateRequest.Type, out targetTypeValue))
        {
            return UpdateTargetFailure(
                $"invalid target type '{updateRequest.Type}': must be 'ollama', 'openai' or 'osnative' (case-insensitive)",
                StatusCodes.Status400BadRequest);
        }

        if (HasBlankModelNames(updateRequest.Models))
        {
            return UpdateTargetFailure("models must not contain blank names", StatusCodes.Status400BadRequest);
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

        // Task #61 heterogeneous fields (partial-update semantics: null/absent = no change).
        if (typeProvided)
        {
            updated.Type = targetTypeValue;
        }

        if (updateRequest.ApiKey is { } updateApiKey)
        {
            // "" (or blank) clears the key; a non-blank value is trimmed. An apiKey can only be
            // set (non-blank) when the EFFECTIVE type (the request's when provided, otherwise the
            // target's existing one) is 'openai'.
            var effectiveType = typeProvided ? targetTypeValue : updated.Type;
            if (!string.IsNullOrWhiteSpace(updateApiKey) && effectiveType != TargetType.OpenAI)
            {
                return UpdateTargetFailure(
                    "apiKey can only be set on targets of type 'openai'", StatusCodes.Status400BadRequest);
            }

            updated.ApiKey = string.IsNullOrWhiteSpace(updateApiKey) ? null : updateApiKey.Trim();
        }

        if (updateRequest.Provider is { } updateProvider)
        {
            updated.Provider = string.IsNullOrWhiteSpace(updateProvider) ? null : updateProvider.Trim();
        }

        if (updateRequest.Models is { } updateModels)
        {
            updated.Models = ToTargetModelConfigs(updateModels);
        }

        // Task #84: node power-draw override (null/absent = no change; clearing a previously
        // set override is done by editing config/config.json — STJ cannot distinguish an
        // explicit JSON null from an absent key).
        if (updateRequest.PowerWatts is { } powerWatts)
        {
            updated.PowerWatts = powerWatts;
        }

        // Task #143: ssh credential (null/absent = no change; ""/blank clears the user; a
        // non-blank value was validated above; sshPort copied verbatim when present —
        // clearing a set port = edit config/config.json, the documented powerWatts precedent).
        if (updateRequest.SshUser is { } updateSshUser)
        {
            updated.SshUser = string.IsNullOrWhiteSpace(updateSshUser) ? null : updateSshUser;
        }

        if (updateRequest.SshPort is { } updateSshPort)
        {
            updated.SshPort = updateSshPort;
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
                updated.MaxParallel,
                updated.Type.ToString().ToLowerInvariant(),
                updated.Provider,
                updated.Models is { Count: > 0 } updatedModels
                    ? updatedModels.Select(m => new TargetModelDto(m.Name, m.ContextLength, m.Quantization)).ToList()
                    : null,
                !string.IsNullOrWhiteSpace(updated.ApiKey),
                updated.PowerWatts,
                updated.SshUser,
                updated.SshPort)),
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

    /// <summary>
    /// Handler of <c>POST /api/router/targets/{name}/reset-streak</c> (task #95): resets the
    /// target's failure streak to 0 and clears its circuit-breaker cooldown — the target is
    /// immediately routable on the next routing decision (no restart, no config write). The
    /// case-insensitive membership check runs FIRST (unknown name / <c>Cloud</c> → 404 even
    /// when the streak service is absent); 500 only for a host without the streak service.
    /// </summary>
    private static IResult ResetTargetStreakHandler(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        string name)
    {
        // 404 BEFORE 500: an unknown name wins even when the streak service is not registered.
        var effectiveTargets = options.CurrentValue.GetEffectiveTargets();
        if (!effectiveTargets.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Json(
                new ResetTargetStreakResponse(false, name, null, null, $"target not found: {name}"),
                OllamaRouterJsonSerializerContext.Default.ResetTargetStreakResponse,
                statusCode: StatusCodes.Status404NotFound);
        }

        var streakService = serviceProvider.GetService<ITargetFailureStreakService>();
        if (streakService is null)
        {
            return Results.Json(
                new ResetTargetStreakResponse(
                    false, name, null, null, "failure-streak tracking is not available in this host"),
                OllamaRouterJsonSerializerContext.Default.ResetTargetStreakResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var routingTarget = (RoutingTarget)name;
        // The shared singleton the routing middleware gates on: the reset takes effect on the
        // very next routing decision. Liveness / availability flags / leases are untouched.
        streakService.ResetStreak(routingTarget);
        var opt = options.CurrentValue;
        var status = FailureStreakWire.ToStateString(
            streakService.GetCircuitState(
                routingTarget, opt.MaxConsecutiveFailures, TimeSpan.FromSeconds(opt.FailureStreakCooldownSeconds)));

        return Results.Json(
            new ResetTargetStreakResponse(true, name, streakService.GetStreak(routingTarget), status),
            OllamaRouterJsonSerializerContext.Default.ResetTargetStreakResponse);
    }

    /// <summary>
    /// Handler of <c>POST /api/router/fleet/reset-streaks</c> (task #95): resets the failure
    /// streak of every effective target (configuration order) plus the Cloud pseudo-target
    /// when cloud overflow is enabled. Per-target: <c>previousStreak</c> = the streak read
    /// BEFORE the reset, <c>reset: true</c> iff it was above zero; <c>resetCount</c> = the
    /// number of reset targets. Idempotent (a second call reports <c>resetCount: 0</c>); a
    /// host without the streak service → 500.
    /// </summary>
    private static IResult ResetFleetStreaksHandler(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider)
    {
        var streakService = serviceProvider.GetService<ITargetFailureStreakService>();
        if (streakService is null)
        {
            return Results.Json(
                new ResetFleetStreaksResponse(false, 0, null,
                    "failure-streak tracking is not available in this host"),
                OllamaRouterJsonSerializerContext.Default.ResetFleetStreaksResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var opt = options.CurrentValue;
        var effectiveTargets = opt.GetEffectiveTargets();
        var rows = new List<ResetFleetStreaksTarget>(effectiveTargets.Count + 1);
        void ResetOne(RoutingTarget target)
        {
            var previousStreak = streakService.GetStreak(target);
            streakService.ResetStreak(target);
            rows.Add(new ResetFleetStreaksTarget(target.Value, previousStreak, previousStreak > 0));
        }

        foreach (var target in effectiveTargets)
        {
            ResetOne((RoutingTarget)target.Name!);
        }

        // The Cloud pseudo-target can carry a tracked streak (dispatch-loop relay failures);
        // the reset is a no-op when untracked. Included only when cloud overflow is enabled.
        if (opt.CloudOverflow.Enabled)
        {
            ResetOne(RoutingTarget.Cloud);
        }

        return Results.Json(
            new ResetFleetStreaksResponse(true, rows.Count(r => r.PreviousStreak > 0), rows),
            OllamaRouterJsonSerializerContext.Default.ResetFleetStreaksResponse);
    }

    private static readonly char[] InvalidTargetNameChars = ['/', '\\', ':'];

    /// <summary>
    /// Parses the Setup API wire value of a target's protocol family (task #61):
    /// case-insensitive <c>ollama|openai|osnative</c>; null/blank defaults to
    /// <see cref="TargetType.Ollama"/> (the config binder's enum default, so a legacy POST
    /// without a <c>type</c> field is byte-identical).
    /// </summary>
    private static bool TryParseTargetType(string? raw, out TargetType type)
    {
        type = TargetType.Ollama;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        return Enum.TryParse<TargetType>(raw.Trim(), ignoreCase: true, out type)
            || (raw.Trim().Length == 0);
    }

    /// <summary>
    /// Whether any declared model of the Setup API payload has a blank name (task #61):
    /// blank roster entries are rejected with a 400 (every consumer skips them, so accepting
    /// them would silently drop declared models).
    /// </summary>
    private static bool HasBlankModelNames(IReadOnlyList<TargetModelDto>? models)
    {
        if (models is null)
        {
            return false;
        }

        return models.Any(m => string.IsNullOrWhiteSpace(m?.Name));
    }

    /// <summary>
    /// Maps the Setup API model DTOs to the options-level declared roster (task #61),
    /// trimming names and dropping entries whose name is blank (the caller 400s those first,
    /// so this is defensive only). Null/absent maps to an empty roster (permissive policy).
    /// </summary>
    private static List<TargetModelConfig> ToTargetModelConfigs(IReadOnlyList<TargetModelDto>? models)
    {
        var result = new List<TargetModelConfig>();
        if (models is null)
        {
            return result;
        }

        foreach (var model in models)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Name))
            {
                continue;
            }

            result.Add(new TargetModelConfig
            {
                Name = model.Name.Trim(),
                ContextLength = model.ContextLength,
                Quantization = string.IsNullOrWhiteSpace(model.Quantization) ? null : model.Quantization.Trim()
            });
        }

        return result;
    }

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
        HardwareHint = target.HardwareHint,
        Type = target.Type,
        ApiKey = target.ApiKey,
        Provider = target.Provider,
        PowerWatts = target.PowerWatts,
        SshUser = target.SshUser,
        SshPort = target.SshPort,
        Models = target.Models is { Count: > 0 } existing
            ? existing.Select(m => new TargetModelConfig
                {
                    Name = m.Name,
                    ContextLength = m.ContextLength,
                    Quantization = m.Quantization
                }).ToList()
            : []
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
