using OllamaRouter.Services;

namespace OllamaRouter.Middleware;

/// <summary>
/// The resolved <c>X-OllamaRouter-Target</c> client pin, in one of two mutually exclusive
/// forms:
/// <list type="bullet">
/// <item><see cref="ExactTarget"/> — the header named an existing configured target (or the
/// <c>Cloud</c> pseudo-target); the pinned-management and completions pipelines behave
/// exactly as before the location-scope extension.</item>
/// <item><see cref="AllowedLocations"/> — the header carried a location scope (bare keywords
/// <c>local</c> / <c>remote</c> / <c>cloud</c> or a comma-separated allow-list such as
/// <c>local,remote</c>; each token trimmed, matched OrdinalIgnoreCase, duplicates collapsed)
/// selecting every fleet target of an allowed <see cref="TargetLocation"/> plus, when
/// <see cref="TargetLocation.Cloud"/> is in the set, the Cloud overflow pseudo-target.</item>
/// </list>
/// </summary>
public sealed record PinnedTargetPin(RoutingTarget? ExactTarget, IReadOnlySet<TargetLocation>? AllowedLocations);

/// <summary>
/// Maps the <c>X-OllamaRouter-Target</c> location-scope keywords (<c>local</c>,
/// <c>remote</c>, <c>cloud</c> — case-insensitive) to <see cref="TargetLocation"/> values.
/// Unknown tokens return false (the whole header is then treated as unrecognized and the
/// existing dynamic/pinned-management fallback applies).
/// </summary>
internal static class PinnedTargetLocationKeyword
{
    public static bool TryMap(string token, out TargetLocation location)
    {
        location = TargetLocation.Cloud;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (string.Equals(trimmed, "local", StringComparison.OrdinalIgnoreCase))
        {
            location = TargetLocation.Local;
            return true;
        }

        if (string.Equals(trimmed, "remote", StringComparison.OrdinalIgnoreCase))
        {
            location = TargetLocation.Remote;
            return true;
        }

        if (string.Equals(trimmed, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            location = TargetLocation.Cloud;
            return true;
        }

        return false;
    }
}
