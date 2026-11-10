using OllamaRouter.Options;

namespace OllamaRouter.Services.Catalog;

/// <summary>
/// The shared catalog-check policy for non-Ollama fleet targets (task #61): a non-Ollama
/// target's model catalog is SYNTHETIC (there is no <c>/api/tags</c> to probe), sourced from
/// the target's declared <see cref="OllamaTargetOptions.Models"/> roster.
/// <list type="bullet">
/// <item>NON-EMPTY roster ⇒ STRICT membership: the decision service's Stage-1 catalog check
/// and the middleware's pinned-target capability gate query the synthetic roster (an unlisted
/// model disqualifies the target with the same "model not present in the target catalog"
/// reason as today).</item>
/// <item>EMPTY roster (the default) ⇒ PERMISSIVE: both call sites short-circuit the catalog
/// check to true via <see cref="IsPermissive"/> — mirroring the existing "degraded catalog ⇒
/// permissive" policy — so a minimal <c>{name, url, apiKey}</c> OpenAI target stays routable
/// for every requested model name.</item>
/// </list>
/// Both call sites (the routing decision service and the middleware) MUST use this helper so
/// the decision and the pin gate can never disagree about which models a target serves.
/// </summary>
public static class NonOllamaCatalogPolicy
{
    /// <summary>
    /// Returns true when the target's catalog check must be short-circuited to true:
    /// a NON-OLLAMA target with an EMPTY declared model roster (permissive synthetic catalog).
    /// Ollama targets (live <c>/api/tags</c> catalog), null targets and non-Ollama targets
    /// with a non-empty roster (strict membership) all return false.
    /// </summary>
    public static bool IsPermissive(OllamaTargetOptions? target)
        => target is { Type: not TargetType.Ollama, Models: not { Count: > 0 } };
}
