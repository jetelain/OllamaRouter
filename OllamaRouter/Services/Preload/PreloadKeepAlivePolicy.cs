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
/// <see cref="OllamaTargetOptions.GetEffectivePreloadKeepAliveSeconds(OllamaRouterOptions, Hardware.IFleetHardwareService?)"/>
/// resolver, the preload ranking (server-class preference) and the operator docs. Reuses
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
