using OllamaRouter.Options;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Preload;

/// <summary>
/// The preload keep-alive node class that drives the per-node default preload retention
/// convention for nodes without an explicit
/// <see cref="OllamaTargetOptions.KeepAliveSeconds"/> override: cloud and server-class
/// nodes stay resident indefinitely (<c>-1</c>), desktop/laptop-class nodes retain the
/// preload for 15 minutes (900 s), and unclassifiable nodes fall back to the router-level
/// <see cref="OllamaRouterOptions.PreloadDefaultKeepAliveSeconds"/>.
/// </summary>
public enum PreloadNodeClass
{
    /// <summary>Unknown VRAM and no cloud signal: the router-level default applies.</summary>
    Unknown = 0,

    /// <summary>Known VRAM below the server threshold (workstation / laptop class).</summary>
    Desktop,

    /// <summary>Effective VRAM ≥ <see cref="DynamicContextSizingEngine.ServerClassVramThresholdMB"/> (32,768 MB).</summary>
    Server,

    /// <summary>Cloud-class node (<see cref="TargetLocationClassifier"/>: explicit Location override or URL classification).</summary>
    Cloud
}

/// <summary>
/// Pure, synchronous classifier for the preload keep-alive convention (server/cloud = -1
/// indefinitely, desktop = 15 min): the single definition shared by the
/// <see cref="ResolvePreloadKeepAlive(OllamaTargetOptions, OllamaRouterOptions, Hardware.IFleetHardwareService?)"/>
/// resolver (and, through it, the
/// <see cref="OllamaTargetOptions.GetEffectivePreloadKeepAliveSeconds(OllamaRouterOptions, Hardware.IFleetHardwareService?)"/>
/// read site), the preload ranking (server-class preference) and the operator docs. Reuses
/// <see cref="TargetLocationClassifier"/> (the cloud signal) and
/// <see cref="DynamicContextSizingEngine.ServerClassVramThresholdMB"/> (the server threshold)
/// — it does NOT move or duplicate either.
/// </summary>
internal static class PreloadKeepAlivePolicy
{
    /// <summary>Cloud and server-class default: resident indefinitely (Ollama <c>keep_alive: -1</c>).</summary>
    public const int CloudAndServerKeepAliveSeconds = -1;

    /// <summary>Desktop/laptop-class default: 15 minutes (900 s) of preload retention.</summary>
    public const int DesktopClassKeepAliveSeconds = 900;

    /// <summary>
    /// Wire vocabulary of the fleet/monitor <c>keepAliveSource</c> discriminator: the value came
    /// from the per-node <see cref="OllamaTargetOptions.KeepAliveSeconds"/> override.
    /// </summary>
    public const string SourceExplicit = "explicit";

    /// <summary>
    /// Wire vocabulary of the fleet/monitor <c>keepAliveSource</c> discriminator: the value came
    /// from the documented node-class convention (cloud / server-class = -1, desktop-class = 900).
    /// </summary>
    public const string SourceClass = "class";

    /// <summary>
    /// Wire vocabulary of the fleet/monitor <c>keepAliveSource</c> discriminator: the value is the
    /// router-level <see cref="OllamaRouterOptions.PreloadDefaultKeepAliveSeconds"/> fallback for an
    /// unclassifiable node (unknown VRAM, no cloud location signal).
    /// </summary>
    public const string SourceDefault = "default";

    /// <summary>
    /// The resolved effective preload keep-alive for one fleet target: the effective
    /// <see cref="Seconds"/> policy plus the <see cref="Source"/> discriminator naming which rule
    /// produced it (<see cref="SourceExplicit"/> / <see cref="SourceClass"/> /
    /// <see cref="SourceDefault"/> — the fleet/monitor <c>keepAliveSource</c> wire vocabulary).
    /// This record is the SINGLE source of truth for both the preload load path (via
    /// <see cref="OllamaTargetOptions.GetEffectivePreloadKeepAliveSeconds(OllamaRouterOptions, Hardware.IFleetHardwareService?)"/>
    /// , which delegates to <see cref="Seconds"/>) and the fleet/monitor residency-visibility
    /// surfaces (which serialize both fields), so the two can never drift.
    /// </summary>
    /// <param name="Seconds">Effective keep-alive in seconds (<c>-1</c> = resident indefinitely).</param>
    /// <param name="Source">One of <see cref="SourceExplicit"/>, <see cref="SourceClass"/>, <see cref="SourceDefault"/>.</param>
    public sealed record PreloadKeepAliveResolution(int Seconds, string Source);

    /// <summary>
    /// Resolves the effective preload keep-alive AND its source discriminator for a fleet target:
    /// an explicit, valid <see cref="OllamaTargetOptions.KeepAliveSeconds"/> (<c>-1</c> or
    /// <c>&gt; 0</c>) wins verbatim (including -1 pinned on a desktop-class node →
    /// <see cref="SourceExplicit"/>); otherwise the documented class convention applies via
    /// <see cref="GetPreloadNodeClass(OllamaTargetOptions, int?)"/>: Cloud / Server →
    /// (-1, <see cref="SourceClass"/>), Desktop → (900, <see cref="SourceClass"/>), and an
    /// unclassifiable node → (router <see cref="OllamaRouterOptions.PreloadDefaultKeepAliveSeconds"/>,
    /// <see cref="SourceDefault"/>). Invalid explicit values (0, ≤ -2) that
    /// <see cref="OllamaRouterOptions.Normalize"/> has reset to null simply fall through to the
    /// convention — this method NEVER throws.
    /// </summary>
    public static PreloadKeepAliveResolution ResolvePreloadKeepAlive(
        OllamaTargetOptions target,
        OllamaRouterOptions routerOptions,
        Hardware.IFleetHardwareService? fleetHardware = null)
    {
        if (target.KeepAliveSeconds is { } explicitKeepAlive && (explicitKeepAlive == -1 || explicitKeepAlive > 0))
        {
            return new PreloadKeepAliveResolution(explicitKeepAlive, SourceExplicit);
        }

        var nodeClass = GetPreloadNodeClass(target, target.GetEffectiveMaxVramMB(fleetHardware));
        return nodeClass switch
        {
            PreloadNodeClass.Cloud => new PreloadKeepAliveResolution(CloudAndServerKeepAliveSeconds, SourceClass),
            PreloadNodeClass.Server => new PreloadKeepAliveResolution(CloudAndServerKeepAliveSeconds, SourceClass),
            PreloadNodeClass.Desktop => new PreloadKeepAliveResolution(DesktopClassKeepAliveSeconds, SourceClass),
            _ => new PreloadKeepAliveResolution(routerOptions.PreloadDefaultKeepAliveSeconds, SourceDefault)
        };
    }

    /// <summary>
    /// Classifies a fleet target for the preload keep-alive convention: cloud class
    /// (<see cref="TargetLocationClassifier.Classify(OllamaTargetOptions?)"/> ==
    /// <see cref="TargetLocation.Cloud"/>) wins outright, then a known
    /// <paramref name="vramMB"/> at/above the server threshold is Server and below it
    /// Desktop; an unknown VRAM on a non-cloud node is <see cref="PreloadNodeClass.Unknown"/>.
    /// </summary>
    public static PreloadNodeClass GetPreloadNodeClass(OllamaTargetOptions target, int? vramMB)
    {
        if (TargetLocationClassifier.Classify(target) == TargetLocation.Cloud)
        {
            return PreloadNodeClass.Cloud;
        }

        if (vramMB is { } vram)
        {
            return vram >= DynamicContextSizingEngine.ServerClassVramThresholdMB
                ? PreloadNodeClass.Server
                : PreloadNodeClass.Desktop;
        }

        return PreloadNodeClass.Unknown;
    }
}
