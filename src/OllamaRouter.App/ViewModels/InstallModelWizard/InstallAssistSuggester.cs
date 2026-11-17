using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Services.Benchmarking;

namespace OllamaRouter.App.ViewModels.InstallModelWizard;

/// <summary>
/// One per-target context suggestion of the benchmark-assisted install mode (task #125):
/// the suggested <see cref="Context"/> (null = no data — the user must set the context
/// manually) and the human-readable <see cref="Provenance"/> line shown under the input.
/// </summary>
public sealed record ContextSuggestion(int? Context, string Provenance);

/// <summary>
/// One quant/tag suggestion of the install wizard (task #125): the suggested registry tag
/// (null = none) and its provenance (<c>"user-specified"</c> / <c>"fleet catalog"</c> /
/// <c>"no data — registry default"</c>).
/// </summary>
public sealed record TagSuggestion(string? Tag, string Provenance);

/// <summary>
/// The PURE assistance computation of the benchmark-assisted install mode (task #125) — the
/// same setup-wizard-style "assist + user confirmation" pattern as the role-recommendation
/// step, computed CLIENT-SIDE from the existing read-only data (benchmark results, fleet
/// catalog model detail, cartography). No network, no UI: unit-tested directly.
/// <para>
/// Context precedence (per target, for the EXACT model name): (1) the LARGEST viable
/// benchmark <c>numCtx</c> row (measured decode throughput provenance); (2) the fleet
/// catalog per-target <c>contextCeiling</c> when the target is capable; (3) null + hint
/// (a not-capable target surfaces its <c>vetoReason</c> instead of the generic hint).
/// </para>
/// </summary>
public static class InstallAssistSuggester
{
    /// <summary>
    /// Suggests the context (tokens) for one (model, target) pair (see the class summary for
    /// the precedence). <paramref name="benchmarkRows"/> may be null (no benchmark data) or
    /// contain the rows for the EXACT model name on that target (all viability flags as
    /// reported by the benchmark engine).
    /// </summary>
    public static ContextSuggestion SuggestContext(
        string targetName,
        IReadOnlyList<BenchmarkContextResult>? benchmarkRows,
        bool catalogCapable,
        int? catalogCeiling,
        string? vetoReason)
    {
        var best = benchmarkRows?
            .Where(row => row.Viable && row.NumCtx > 0)
            .OrderByDescending(row => row.NumCtx)
            .FirstOrDefault();
        if (best is not null)
        {
            return new ContextSuggestion(
                best.NumCtx,
                $"{best.NumCtx} tok — benchmark: viable @ {best.DecodeTokensPerSecond:0.0} t/s");
        }

        if (catalogCapable && catalogCeiling is { } ceiling and > 0)
        {
            return new ContextSuggestion(ceiling, $"{ceiling} tok — fleet catalog ceiling on {targetName}");
        }

        if (vetoReason is not null)
        {
            return new ContextSuggestion(null, vetoReason);
        }

        return new ContextSuggestion(null, "no benchmark/catalog data — set the context manually");
    }

    /// <summary>
    /// Suggests the registry tag (the quantization selector) of the install: the user's
    /// explicit tag is AUTHORITATIVE; else the fleet-catalog tag of a model with the exact
    /// base name (e.g. catalog row <c>llama3.2:13b-q4_K_M</c> → tag <c>13b-q4_K_M</c> for
    /// <c>llama3.2</c>); else the registry default <c>latest</c> with a no-data provenance.
    /// </summary>
    public static TagSuggestion SuggestTag(string? userTag, string? catalogModelName, string baseName)
    {
        if (!string.IsNullOrWhiteSpace(userTag))
        {
            return new TagSuggestion(userTag, "user-specified");
        }

        if (catalogModelName is not null &&
            catalogModelName.StartsWith(baseName + ":", StringComparison.OrdinalIgnoreCase) &&
            catalogModelName.Length > baseName.Length + 1)
        {
            return new TagSuggestion(catalogModelName[(baseName.Length + 1)..], "fleet catalog");
        }

        return new TagSuggestion("latest", "no data — registry default");
    }

    /// <summary>
    /// The pull plan of the wizard: one entry per selected target with its (PER-TARGET)
    /// model reference, its effective context (null = alias OFF) and its precomputed
    /// context alias (null when the alias is OFF or the context is missing/invalid — the
    /// server would then 400, but the wizard gate prevents reaching that state).
    /// </summary>
    public static (string Target, string Model, int? Context, string? Alias, bool Present) PlanTarget(
        string target, string modelReference, int? context, bool present)
    {
        var alias = context is { } ctx && ModelPullNaming.TryComputeAliasName(modelReference, ctx, out var computed)
            ? computed
            : null;
        return (target, modelReference, context, alias, present);
    }
}
