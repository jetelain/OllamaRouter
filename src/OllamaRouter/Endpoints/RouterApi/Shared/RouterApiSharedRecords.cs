using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;

namespace OllamaRouter.Endpoints.RouterApi.Shared;

/// <summary>
/// Shared model-summary computation result of <c>ComputeModelSummaryAsync</c>: the public
/// summary plus the primary (first hoster with model_info) fleet metadata and the raw
/// per-target metadata pairs (empty when the model is present on no fleet target) so the
/// cartography endpoint can build its deployment rows without re-discovering.
/// </summary>
internal sealed record ModelSummaryData(
    RouterModelSummary Summary,
    ModelMetadata? PrimaryMetadata,
    List<(OllamaTargetOptions Target, ModelMetadata Metadata)> RawTargetMetadata,
    bool PresentOnFleet);

/// <summary>
/// Per-target qualification core shared by the <c>GET /api/router/models/{name}</c> matrix and
/// the cartography deployment rows: presence, capability, veto, context ceiling, residency,
/// in-flight count and the (merged, effective) metadata used for the derivation.
/// </summary>
internal sealed record TargetQualification(
    string Target,
    bool Present,
    bool Capable,
    string? VetoReason,
    int ContextCeiling,
    bool IsResident,
    int InFlightRequests,
    ModelMetadata? EffectiveMetadata,
    ModelMetadata? TargetMetadata = null)
{
    /// <summary>Projects onto the <c>GET /api/router/models/{name}</c> wire row (byte-identical to the pre-refactor builder).</summary>
    public RouterModelTargetCompatibility ToCompatibilityRow() => new(
        Target: Target,
        Capable: Capable,
        ContextCeiling: ContextCeiling,
        IsResident: IsResident,
        InFlightRequests: InFlightRequests,
        VetoReason: VetoReason);
}
