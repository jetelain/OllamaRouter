using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Services.Recommendations;

/// <summary>
/// The native role recommendation engine (task #66): a pure, deterministic, unit-testable
/// (no I/O) function over the micro-benchmark results (task #28) and the fleet hardware
/// profiles that assembles the role → native model assignment matrix with the per-node
/// context ceilings. See <see cref="RecommendationEngine"/> for the full algorithm.
/// </summary>
public interface IRecommendationEngine
{
    /// <summary>
    /// Generates the fleet recommendation matrix:
    /// <paramref name="catalog"/> is the code-defined seed catalog
    /// (<c>CandidateModelCatalogOptions.DefaultModels</c>), and the nullable
    /// <paramref name="hardwareProfileLookup"/> supplies the per-target fast-memory size
    /// (fail-open: null profile → <c>VramMB = null</c>). <paramref name="generatedAt"/> is
    /// the endpoint-supplied stamp (null → <c>DateTimeOffset.UtcNow</c>); tests pass a
    /// fixed value for determinism.
    /// </summary>
    FleetRecommendations Generate(
        IReadOnlyList<BenchmarkTargetResult> benchmarkResults,
        IReadOnlyList<CandidateModel> catalog,
        Func<string, NodeHardwareProfile?>? hardwareProfileLookup = null,
        DateTimeOffset? generatedAt = null);
}
