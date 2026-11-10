using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using System.Text.Json.Nodes;

namespace OllamaRouter.Endpoints.RouterApi.Shared;

/// <summary>
/// Shared internal helper methods of the OllamaRouter programmatic management API: model
/// discovery / summary computation and per-target qualification, used across multiple
/// per-area endpoint classes and the preload service (single canonical definitions).
/// </summary>

internal static class RouterApiSharedHelpers
{
    internal static async Task<bool> CatalogHasAnyModelsAsync(
        IModelCatalogCacheService? modelCatalogCache,
        string? url,
        CancellationToken cancellationToken)
    {
        if (modelCatalogCache is null || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            return await modelCatalogCache.HasAnyModelsAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failing catalog check must never break the info payload.
            return false;
        }
    }

    // Renders the current context allocation for one fleet target from the lease snapshot:
    // any expanded lease -> "<maxBucket>k (expanded)"; a single baseline lease ->
    // "<bucket>k"; no leases at all (or no manager) -> "baseline". Cloud is always "baseline".
    internal static string FormatContextAllocation(
        RoutingTarget target,
        IReadOnlyDictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>? leaseSnapshot)
    {
        if (target == RoutingTarget.Cloud || leaseSnapshot is null ||
            !leaseSnapshot.TryGetValue(target, out var leases) || leases.Count == 0)
        {
            return "baseline";
        }

        if (leases.Any(lease => lease.IsExpanded))
        {
            return $"{FormatBucketTokens(leases.Max(lease => lease.ActiveBucket))} (expanded)";
        }

        return FormatBucketTokens(leases[0].ActiveBucket);
    }

    private static string FormatBucketTokens(int tokens)
    {
        if (tokens >= 1_000_000)
        {
            return TrimTrailingZero((tokens / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + "M";
        }
        if (tokens >= 1_000)
        {
            return TrimTrailingZero((tokens / 1_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + "k";
        }

        return tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // "32.0" → "32" (spec string set: "32k", "262.1k", "1.2M"); "65.5" stays "65.5".
    private static string TrimTrailingZero(string value) => value.EndsWith(".0", StringComparison.Ordinal)
        ? value[..^2]
        : value;
    /// <summary>
    /// Merged fleet model names (first-seen order) plus the cloud synthesis names (appended
    /// when not already present): the unified catalog of local, remote LAN nodes, and cloud
    /// overflow models. The cloud syntheses are included only when the Cloud overflow switch
    /// AND the targets.json Cloud toggle are both on (same rules as the /api/tags endpoint).
    /// </summary>
    internal static async Task<List<string>> GetFleetModelNamesAsync(
        OllamaRouterOptions opt,
        ITargetAvailabilityService? availability,
        IFleetAggregationService? fleetAggregation,
        IModelCatalogCacheService? catalogCache,
        HttpContext? context,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();

        if (fleetAggregation is not null)
        {
            try
            {
                var merged = await fleetAggregation.GetTagsFleetAsync(
                    BuildCloudTagSyntheses(opt, availability), context).ConfigureAwait(false);
                foreach (var entry in merged)
                {
                    if (entry?["name"]?.ToString() is { Length: > 0 } name &&
                        ShouldIncludeModel(name, opt) &&
                        !ContainsName(names, name))
                    {
                        names.Add(name);
                    }
                }
            }
            catch
            {
                // A failing aggregation must never break the model capability listing.
            }
        }
        else if (catalogCache is not null)
        {
            // Degraded enumeration: no fan-out service, so read each effective target's
            // catalog directly in declaration order.
            foreach (var target in opt.GetEffectiveTargets())
            {
                if (string.IsNullOrWhiteSpace(target.Url))
                {
                    continue;
                }

                try
                {
                    foreach (var name in await catalogCache.RefreshAsync(target.Url, cancellationToken).ConfigureAwait(false))
                    {
                        if (ShouldIncludeModel(name, opt) && !ContainsName(names, name))
                        {
                            names.Add(name);
                        }
                    }
                }
                catch
                {
                    // Unreachable target contributes nothing.
                }
            }

            foreach (var synthesis in BuildCloudTagSyntheses(opt, availability) ?? [])
            {
                if (synthesis?["name"]?.ToString() is { Length: > 0 } name &&
                    ShouldIncludeModel(name, opt) &&
                    !ContainsName(names, name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Cloud synthesis model names (per-model <c>Models[*].CloudModel</c> in configuration
    /// order, then <c>CloudOverflow.DefaultModel</c>): the same rules and JSON shape as the
    /// /api/tags endpoint's syntheses. Returns null when Cloud overflow is unavailable
    /// (static switch off OR the targets.json Cloud toggle off).
    /// </summary>
    private static IReadOnlyList<JsonNode>? BuildCloudTagSyntheses(OllamaRouterOptions options, ITargetAvailabilityService? availability)
    {
        if (!options.CloudOverflow.Enabled ||
            (availability is not null && !availability.IsEnabled(RoutingTarget.Cloud)))
        {
            return null;
        }

        var syntheses = new List<JsonNode>();
        if (options.Models != null)
        {
            foreach (var (_, modelOpts) in options.Models)
            {
                if (!string.IsNullOrWhiteSpace(modelOpts.CloudModel))
                {
                    syntheses.Add(MakeCloudSynthesisNode(modelOpts.CloudModel));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(options.CloudOverflow.DefaultModel))
        {
            syntheses.Add(MakeCloudSynthesisNode(options.CloudOverflow.DefaultModel));
        }

        return syntheses.Count > 0 ? syntheses : null;
    }

    private static JsonNode MakeCloudSynthesisNode(string cloudName) => new JsonObject
    {
        ["name"] = cloudName,
        ["model"] = cloudName,
        ["modified_at"] = DateTime.UtcNow.ToString("o"),
        ["size"] = 0,
        ["details"] = new JsonObject
        {
            ["family"] = "cloud",
            ["parameter_size"] = "cloud",
            ["quantization_level"] = "cloud"
        }
    };
    /// <summary>
    /// Resolves an incoming model name against the fleet catalog: case-insensitive exact
    /// full name (tag included) first, then the tagless-prefix fallback (strip the incoming
    /// name after ':' — or use the whole tagless name — and match any catalog entry sharing
    /// that prefix, normally the ":latest" variant). Cloud synthesis names are catalog
    /// entries like any other.
    /// Returns null (→ 404) when nothing matches.
    /// </summary>
    internal static string? ResolveModelName(string requested, IReadOnlyList<string> names)
    {
        var exact = names.FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var colonIndex = requested.IndexOf(':');
        var prefix = colonIndex > 0 ? requested[..colonIndex] : requested;
        if (prefix.Length == 0)
        {
            return null;
        }

        return names.FirstOrDefault(name =>
            name.IndexOf(':') > 0 &&
            string.Equals(name[..name.IndexOf(':')], prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsName(IReadOnlyList<string> names, string candidate) =>
        names.Any(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
    /// <summary>
    /// True when any effective target's cached /api/tags catalog contains the model
    /// (case-insensitive name match). Null catalog cache ⇒ false.
    /// </summary>
    internal static async Task<bool> AnyTargetHasModelAsync(
        IModelCatalogCacheService? catalogCache,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        string modelName,
        CancellationToken cancellationToken)
    {
        if (catalogCache is null)
        {
            return false;
        }

        foreach (var target in effectiveTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Url))
            {
                continue;
            }

            try
            {
                if (await catalogCache.ModelExistsAsync(target.Url, modelName, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch
            {
                // A failing catalog check must never break the capability report.
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether a model should be included in the fleet model listing.
    /// When FilterNamespacedModels is true (default), models containing a namespace separator ('/' or '\')
    /// are filtered out unless explicitly declared in <see cref="OllamaRouterOptions.Models"/> or
    /// configured as a Cloud Overflow model.
    /// </summary>
    internal static bool ShouldIncludeModel(string name, OllamaRouterOptions opt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!opt.FilterNamespacedModels)
        {
            return true;
        }

        // Native library models and local aliases do not contain a namespace separator ('/' or '\').
        if (!name.Contains('/') && !name.Contains('\\'))
        {
            return true;
        }

        // Namespaced models (e.g. "jetelain/*") are filtered out unless explicitly configured.
        return IsModelConfigured(name, opt);
    }

    /// <summary>
    /// Returns true if the model is explicitly configured in <see cref="OllamaRouterOptions.Models"/>
    /// or <see cref="OllamaRouterOptions.CloudOverflow"/>.
    /// </summary>
    internal static bool IsModelConfigured(string name, OllamaRouterOptions opt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = RoutingDecisionService.NormalizeModelName(name);

        if (opt.Models.Count > 0)
        {
            foreach (var key in opt.Models.Keys)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var normalizedKey = RoutingDecisionService.NormalizeModelName(key);
                if (string.Equals(normalizedKey, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedKey, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var threshold in opt.Models.Values)
            {
                if (!string.IsNullOrEmpty(threshold.CloudModel))
                {
                    var normalizedCloud = RoutingDecisionService.NormalizeModelName(threshold.CloudModel);
                    if (string.Equals(threshold.CloudModel, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(threshold.CloudModel, normalized, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normalizedCloud, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normalizedCloud, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(opt.CloudOverflow.DefaultModel))
        {
            var normalizedDefault = RoutingDecisionService.NormalizeModelName(opt.CloudOverflow.DefaultModel);
            if (string.Equals(opt.CloudOverflow.DefaultModel, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(opt.CloudOverflow.DefaultModel, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedDefault, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedDefault, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a requested model name across all effective target catalogs without namespace filtering,
    /// enabling direct lookup of specific models on /api/router/models/{name}.
    /// </summary>
    internal static async Task<string?> ResolveModelOnFleetAsync(
        IModelCatalogCacheService? catalogCache,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        string requested,
        CancellationToken cancellationToken)
    {
        if (catalogCache is null)
        {
            return null;
        }

        var allNames = new List<string>();
        foreach (var target in effectiveTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Url))
            {
                continue;
            }

            try
            {
                var targetModels = await catalogCache.RefreshAsync(target.Url, cancellationToken).ConfigureAwait(false);
                foreach (var name in targetModels)
                {
                    if (!ContainsName(allNames, name))
                    {
                        allNames.Add(name);
                    }
                }
            }
            catch
            {
                // A failing catalog check must never break the model detail lookup.
            }
        }

        return ResolveModelName(requested, allNames);
    }

    /// <summary>
    /// Shared model-summary computation: discovers the metadata on the FIRST effective target
    /// (declaration order) that hosts the model — one /api/show + one /api/tags per model —
    /// reuses that single metadata for every target's pure VRAM-math ceiling derivation, and
    /// computes recommendedContextWindow as the max CMax over the HARDWARE-CAPABLE fleet
    /// targets (cloud overflow is never a ceiling contributor; it is surfaced only via
    /// CloudOverflowAvailable). Returns the public summary plus the raw per-target metadata
    /// (only when the model is present on the fleet) so the cartography endpoint can build
    /// its deployment rows without re-discovering anything.
    /// </summary>
    internal static async Task<ModelSummaryData> ComputeModelSummaryAsync(
        string name,
        OllamaRouterOptions opt,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetAvailabilityService? availability,
        CancellationToken cancellationToken)
    {
        var cloudOverflowAvailable = availability is not null &&
            CloudRelayResolver.IsCloudOverflowAvailable(availability, opt, name);

        // No capability stack (minimal host) or the model is on no target: degrade.
        var presentOnFleet = await AnyTargetHasModelAsync(catalogCache, effectiveTargets, name, cancellationToken)
            .ConfigureAwait(false);
        if (catalogCache is null || capabilityEngine is null || !presentOnFleet)
        {
            return new ModelSummaryData(BuildDegradedSummary(name, cloudOverflowAvailable,
                cloudOnly: catalogCache is not null && !presentOnFleet), null, [], presentOnFleet);
        }

        var normalizedName = RoutingDecisionService.NormalizeModelName(name);
        var pinnedArchitecture = opt.Models.TryGetValue(normalizedName, out var thresholds) ? thresholds.Architecture : null;

        // Fetch per-target metadata across all effective targets hosting the model.
        // Also capture primaryMetadata from the first target with valid model info for general model properties.
        ModelMetadata? primaryMetadata = null;
        var rawTargetMetadata = new List<(OllamaTargetOptions Target, ModelMetadata Metadata)>();

        foreach (var target in effectiveTargets)
        {
            var targetMetadata = await GetTargetMetadataAsync(target, name, catalogCache, capabilityEngine, cancellationToken).ConfigureAwait(false);
            if (targetMetadata is not null)
            {
                if (primaryMetadata is null || (!primaryMetadata.HasModelInfo && targetMetadata.HasModelInfo))
                {
                    primaryMetadata = targetMetadata;
                }
                rawTargetMetadata.Add((target, targetMetadata));
            }
        }

        if (primaryMetadata is null)
        {
            // Per-model discovery failure degrades to the conservative defaults.
            return new ModelSummaryData(BuildDegradedSummary(name, cloudOverflowAvailable, cloudOnly: false), null, [], presentOnFleet);
        }

        var (architecture, _) = capabilityEngine.ClassifyArchitecture(primaryMetadata, pinnedArchitecture);

        // recommendedContextWindow (task #26): dense models report the max pre-configured CMax
        // across hardware-capable targets (each target evaluated with its own Modelfile/GGUF metadata);
        // MoE models report the max of the MoE-aware extensible capacity min(native ?? 128k, MaxContextTokens ?? ∞),
        // which is the extensible ceiling beyond the node's pre-configured context.
        var recommended = ModelCapabilityEngine.OllamaDefaultContext;
        long? maxNativeLength = null;

        foreach (var (target, rawMeta) in rawTargetMetadata)
        {
            var effectiveMeta = MergeMetadata(rawMeta, primaryMetadata);
            if (effectiveMeta.NativeContextLength is { } len)
            {
                maxNativeLength = Math.Max(maxNativeLength ?? 0, len);
            }

            var capability = capabilityEngine.DeriveCapability(effectiveMeta, target.Name!, target, pinnedArchitecture);
            if (capability.HardwareCapable)
            {
                var capacity = capability.Architecture == ModelArchitectureKind.Moe
                    ? ModelCapabilityEngine.GetMoeExtensibleCapacity(capability, thresholds?.MaxContextTokens)
                    : capability.CMax;
                recommended = Math.Max(recommended, capacity);
            }
        }

        var nativeContext = maxNativeLength is { } maxNative
            ? (int)Math.Min(maxNative, int.MaxValue)
            : (primaryMetadata.NativeContextLength is { } pLen ? (int)Math.Min(pLen, int.MaxValue) : ModelCapabilityEngine.OllamaDefaultContext);

        return new ModelSummaryData(
            new RouterModelSummary(
                Name: name,
                Architecture: ModelCapabilityEngine.ArchitectureWireName(architecture),
                ParameterCount: primaryMetadata.ParameterCount,
                NativeContextWindow: nativeContext,
                RecommendedContextWindow: recommended,
                CloudOverflowAvailable: cloudOverflowAvailable),
            primaryMetadata,
            rawTargetMetadata,
            presentOnFleet);
    }

    private static RouterModelSummary BuildDegradedSummary(string name, bool cloudOverflowAvailable, bool cloudOnly)
    {
        // Cloud-only synthesis entries have no discoverable metadata at all (native +
        // recommended = 0); every other degraded case falls back to the Ollama default.
        var native = cloudOnly ? 0 : ModelCapabilityEngine.OllamaDefaultContext;
        return new RouterModelSummary(
            Name: name,
            Architecture: "Unknown",
            ParameterCount: null,
            NativeContextWindow: native,
            RecommendedContextWindow: native,
            CloudOverflowAvailable: cloudOverflowAvailable);
    }

    /// <summary>
    /// Merges target-specific metadata with primary fleet metadata: node-specific Modelfile parameters
    /// (such as <c>num_ctx</c>) remain distinct per target, while model-intrinsic properties (such as
    /// parameter count and architecture) fall back to primary metadata if the target daemon returned degraded info.
    /// </summary>
    internal static ModelMetadata MergeMetadata(ModelMetadata targetMetadata, ModelMetadata? primaryMetadata)
    {
        if (primaryMetadata is null)
        {
            return targetMetadata;
        }

        // If targetMetadata has no model info (e.g. show was missing/failed), use primaryMetadata
        // but preserve target's weight size if available.
        if (!targetMetadata.HasModelInfo && primaryMetadata.HasModelInfo)
        {
            return primaryMetadata with
            {
                WeightSizeBytes = targetMetadata.WeightSizeBytes > 0 ? targetMetadata.WeightSizeBytes : primaryMetadata.WeightSizeBytes,
                ModelfileNumCtx = targetMetadata.ModelfileNumCtx ?? primaryMetadata.ModelfileNumCtx
            };
        }

        // Target has model info: target's ModelfileNumCtx wins for that target (even if null!),
        // but intrinsic properties fall back to primary if missing.
        return targetMetadata with
        {
            ParameterCount = targetMetadata.ParameterCount ?? primaryMetadata.ParameterCount,
            ExpertCount = targetMetadata.ExpertCount > 0 ? targetMetadata.ExpertCount : primaryMetadata.ExpertCount,
            ModelFamily = targetMetadata.ModelFamily ?? primaryMetadata.ModelFamily,
            NativeContextLength = targetMetadata.NativeContextLength ?? primaryMetadata.NativeContextLength,
            QuantizationLevel = targetMetadata.QuantizationLevel ?? primaryMetadata.QuantizationLevel,
            HasModelInfo = targetMetadata.HasModelInfo || primaryMetadata.HasModelInfo
        };
    }

    /// <summary>
    /// Fetches model metadata from a specific target's daemon. Returns null if the target does not host
    /// the model, is unreachable, or metadata discovery fails.
    /// </summary>
    internal static async Task<ModelMetadata?> GetTargetMetadataAsync(
        OllamaTargetOptions target,
        string modelName,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        CancellationToken cancellationToken)
    {
        if (catalogCache is null || capabilityEngine is null || string.IsNullOrWhiteSpace(target.Url))
        {
            return null;
        }

        try
        {
            var present = await catalogCache.ModelExistsAsync(target.Url, modelName, cancellationToken).ConfigureAwait(false);
            if (!present)
            {
                return null;
            }

            var digest = await catalogCache.GetCurrentDigestAsync(target.Url, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(digest))
            {
                return null;
            }

            return await capabilityEngine.GetMetadataAsync(target.Url, modelName, digest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Per-target qualification (the shared core of the <c>GET /api/router/models/{name}</c>
    /// target matrix and the cartography deployment rows): Capable = hardware-capable
    /// capability AND model present on that target; the veto reasons are: model absent →
    /// "model not present in target catalog"; LargeDense on a small-class target → the exact
    /// <see cref="ModelCapabilityEngine.LargeDenseVetoReason"/> string; undiscoverable
    /// capability → no veto reason (permissive fallback, matching the routing pipeline).
    /// </summary>
    internal static async Task<TargetQualification> ComputeTargetQualificationAsync(
        string name,
        OllamaTargetOptions target,
        ModelMetadata? primaryMetadata,
        string? pinnedArchitecture,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetExecutionTracker? executionTracker,
        CancellationToken cancellationToken)
    {
        var targetName = target.Name!;
        var routingTarget = (RoutingTarget)targetName;

        bool present = false;
        if (catalogCache is not null && !string.IsNullOrWhiteSpace(target.Url))
        {
            try
            {
                present = await catalogCache.ModelExistsAsync(target.Url, name, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                present = false;
            }
        }

        ModelMetadata? metadata = null;
        if (present)
        {
            metadata = await GetTargetMetadataAsync(target, name, catalogCache, capabilityEngine, cancellationToken).ConfigureAwait(false);
        }

        var effectiveMetadata = metadata is not null
            ? MergeMetadata(metadata, primaryMetadata)
            : primaryMetadata;

        TargetModelCapability? capability = effectiveMetadata is not null && capabilityEngine is not null
            ? capabilityEngine.DeriveCapability(effectiveMetadata, targetName, target, pinnedArchitecture)
            : null;

        var capable = capability is not null && capability.HardwareCapable && present;

        string? vetoReason = null;
        if (!capable)
        {
            if (!present)
            {
                vetoReason = "model not present in target catalog";
            }
            else if (capability is { Architecture: ModelArchitectureKind.LargeDense, HardwareCapable: false })
            {
                vetoReason = ModelCapabilityEngine.LargeDenseVetoReason;
            }
            // Undiscoverable capability: permissive — no veto reason, capable follows the
            // model-presence flag (which is already false here).
        }

        var executionState = executionTracker?.GetState(routingTarget);
        var isResident = executionState?.LoadedModels.Any(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Name, RoutingDecisionService.NormalizeModelName(name), StringComparison.OrdinalIgnoreCase))
            ?? false;
        var inFlight = executionState?.InFlightRequests ?? 0;

        return new TargetQualification(
            Target: targetName,
            Present: present,
            Capable: capable,
            VetoReason: vetoReason,
            ContextCeiling: capability?.CMax ?? ModelCapabilityEngine.OllamaDefaultContext,
            IsResident: isResident,
            InFlightRequests: inFlight,
            EffectiveMetadata: effectiveMetadata,
            TargetMetadata: metadata);
    }
}
