using Microsoft.Extensions.Options;

namespace OllamaRouter.Options;

/// <summary>
/// <see cref="IPostConfigureOptions{TOptions}"/> that re-applies the bind-time normalization
/// clamps (<see cref="OllamaRouterOptions.Normalize()"/> +
/// <see cref="OllamaRouterOptions.DynamicContext"/>'s <c>Normalize()</c>) after every options
/// bind and every configuration reload (first access and each change-token rebind of
/// <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c>).
/// <para>
/// This matters for the layered user configuration (<c>config/config.json</c>,
/// <c>reloadOnChange</c>): a partial user-file update re-binds a FRESH instance per change, so
/// without this post-configuration the negative-value clamps (context fit margin,
/// auto-shrink cooldown/idle timeout, token-metrics CSV max files, preload keep-alives) would
/// not be re-applied to the reloaded instance.
/// </para>
/// <para>
/// Both <c>Normalize()</c> calls are idempotent: already-clamped values are below the clamp
/// thresholds' trigger conditions, so double invocation (first bind + each reload) never
/// discards a user-set value that was already normalized.
/// </para>
/// </summary>
public sealed class OllamaRouterOptionsNormalizer : IPostConfigureOptions<OllamaRouterOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, OllamaRouterOptions options)
    {
        options.Normalize();
        options.DynamicContext.Normalize();
    }
}
