using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Shared Cloud Overflow resolution logic used by both the middleware's dispatch path and the
/// <see cref="CloudRoutingTargetHandler"/> so the two call sites stay identical:
/// <list type="bullet">
/// <item><see cref="ResolveCloudModel"/> — the model-rewrite precedence (per-model
/// <see cref="ModelThresholds.CloudModel"/> wins over
/// <see cref="CloudOverflowOptions.DefaultModel"/>; null when neither is configured).</item>
/// <item><see cref="IsCloudOverflowAvailable"/> — the composed availability predicate
/// (targets.json Cloud toggle AND <see cref="CloudOverflowOptions.Enabled"/> AND a resolvable
/// cloud model).</item>
/// </list>
/// The preferred-relay + fallback candidate chain lives in the middleware's dispatch loop
/// (which needs the resolved URLs); this type keeps the model/availability logic single-sourced.
/// </summary>
public static class CloudRelayResolver
{
    /// <summary>
    /// Returns the cloud model name the request body must be rewritten to, applying the
    /// precedence: per-model <see cref="ModelThresholds.CloudModel"/> (if non-empty), else
    /// <see cref="CloudOverflowOptions.DefaultModel"/> (if non-empty), else null (cloud overflow
    /// is unavailable for this model).
    /// </summary>
    public static string? ResolveCloudModel(OllamaRouterOptions options, string requestedModelName)
    {
        var normalizedName = RoutingDecisionService.NormalizeModelName(requestedModelName);
        if (options.Models.TryGetValue(normalizedName, out var thresholds) &&
            !string.IsNullOrEmpty(thresholds.CloudModel))
        {
            return thresholds.CloudModel;
        }

        return string.IsNullOrEmpty(options.CloudOverflow.DefaultModel)
            ? null
            : options.CloudOverflow.DefaultModel;
    }

    /// <summary>
    /// Composed Cloud Overflow availability for a request: the targets.json Cloud toggle
    /// (<paramref name="availability"/>) AND the static <see cref="CloudOverflowOptions.Enabled"/>
    /// switch AND a resolvable cloud model (per-model or default).
    /// </summary>
    public static bool IsCloudOverflowAvailable(
        ITargetAvailabilityService availability,
        OllamaRouterOptions options,
        string requestedModelName)
    {
        return availability.IsEnabled(RoutingTarget.Cloud) &&
               options.CloudOverflow.Enabled &&
               ResolveCloudModel(options, requestedModelName) is not null;
    }

}
