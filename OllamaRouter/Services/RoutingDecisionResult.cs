namespace OllamaRouter.Services;

/// <summary>
/// Detailed result of a routing decision, including whether an emergency fallback was applied.
/// </summary>
public sealed record RoutingDecisionResult(
    RoutingTarget Target,
    bool IsFallback = false,
    string? FallbackStrategy = null,
    int? TargetCMax = null);
