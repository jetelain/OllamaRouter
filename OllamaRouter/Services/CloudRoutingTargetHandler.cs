namespace OllamaRouter.Services;

using Microsoft.Extensions.Options;
using OllamaRouter.Options;

/// <summary>
/// Handles overflow routing logic and availability for the ollama.com cloud target.
/// </summary>
public sealed class CloudRoutingTargetHandler(
    ITargetAvailabilityService targetAvailability,
    IOptions<OllamaRouterOptions>? options = null,
    ILogger<CloudRoutingTargetHandler>? logger = null) : IRoutingTargetHandler
{
    public RoutingTarget Target => RoutingTarget.Cloud;

    public bool IsOverflow => true;

    public bool IsEnabled => targetAvailability.IsEnabled(RoutingTarget.Cloud);

    /// <summary>
    /// Determines whether the cloud target is currently available.
    /// Since Cloud serves as an overflow target that handles concurrent requests without
    /// single-GPU concurrency constraints, it is available whenever it is enabled in the
    /// targets toggle AND the static <see cref="CloudOverflowOptions.Enabled"/> switch is on.
    /// </summary>
    public bool IsAvailable() => IsEnabled && IsCloudOverflowEnabled();

    /// <summary>
    /// True when the static CloudOverflow.Enabled switch is on (the targets.json Cloud toggle is
    /// <see cref="IsEnabled"/>). The options are optional so existing tests that construct the
    /// handler without options keep the previous behavior (always enabled when the toggle is on).
    /// </summary>
    private bool IsCloudOverflowEnabled()
    {
        var opts = options?.Value;
        return opts is null || opts.CloudOverflow.Enabled;
    }

    public Task<bool> CanProcessAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            logger?.LogDebug("{Model}: Cloud target is disabled.", context.NormalizedModelName);
            return Task.FromResult(false);
        }

        if (!IsCloudOverflowEnabled())
        {
            logger?.LogDebug("{Model}: CloudOverflow is disabled.", context.NormalizedModelName);
            return Task.FromResult(false);
        }

        // Shared precedence (CloudRelayResolver): per-model CloudModel wins over
        // CloudOverflow.DefaultModel. A missing resolvable model means Cloud cannot serve.
        var opts = options?.Value;
        var cloudModel = opts is null
            ? context.Thresholds?.CloudModel
            : CloudRelayResolver.ResolveCloudModel(opts, context.ModelName);
        if (string.IsNullOrEmpty(cloudModel))
        {
            logger?.LogDebug("{Model}: No CloudModel configured for overflow.", context.NormalizedModelName);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}
