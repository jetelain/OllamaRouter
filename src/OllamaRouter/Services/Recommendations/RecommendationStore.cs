using System.Text.Json;
using Microsoft.Extensions.Logging;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Recommendations;

/// <inheritdoc cref="IRecommendationStore"/>
public sealed class RecommendationStore : IRecommendationStore
{
    /// <summary>
    /// The stable empty matrix: <c>generatedAt</c> null, no roles, no models — the shape
    /// <c>GET /api/router/recommendations</c> serves when nothing is active (stable for the
    /// wizard to poll).
    /// </summary>
    public static readonly FleetRecommendations Empty = new(
        null,
        System.Collections.ObjectModel.ReadOnlyDictionary<string, RoleRecommendation>.Empty,
        System.Collections.ObjectModel.ReadOnlyDictionary<string, ModelNodeProfile>.Empty);

    private readonly ILogger<RecommendationStore>? _logger;
    private readonly object _gate = new();

    private volatile FleetRecommendations _matrix = Empty;

    /// <summary>The on-disk <c>recommended.json</c> path (used by the generate endpoint's atomic write).</summary>
    public string FilePath { get; }

    /// <summary>
    /// Creates the store over <paramref name="filePath"/> and performs the fail-soft
    /// startup load (missing file → empty matrix; corrupt file → warn + empty matrix —
    /// the router must never fail to start because of a stale file).
    /// </summary>
    public RecommendationStore(string filePath, ILogger<RecommendationStore>? logger = null)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger;
        _matrix = LoadFromFile(failSoft: true) ?? Empty;
    }

    /// <inheritdoc/>
    public FleetRecommendations GetMatrix() => _matrix;

    /// <inheritdoc/>
    public RoleRecommendation? GetRole(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        var matrix = _matrix;
        return matrix.Roles.TryGetValue(alias, out var role) ? role : null;
    }

    /// <inheritdoc/>
    public bool TryGetNodeContextCeiling(string targetName, string modelName, out int ceiling)
    {
        ceiling = 0;
        if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        var matrix = _matrix;
        var requested = RoutingDecisionService.NormalizeModelName(modelName);
        var profile = FindModel(matrix, requested);
        if (profile is null)
        {
            return false;
        }

        foreach (var node in profile.Nodes)
        {
            if (string.Equals(node.Target, targetName, StringComparison.OrdinalIgnoreCase) && node.ContextCeiling > 0)
            {
                ceiling = node.ContextCeiling;
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public bool TryGetModelProfile(string normalizedModelName, out ModelNodeProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(normalizedModelName))
        {
            profile = null;
            return false;
        }

        var found = FindModel(_matrix, RoutingDecisionService.NormalizeModelName(normalizedModelName));
        profile = found;
        return found is not null;
    }

    /// <summary>
    /// The tag-stripped (OrdinalIgnoreCase) model match against the matrix's
    /// <see cref="FleetRecommendations.Models"/> index: BOTH the requested name and each
    /// index key are normalized with <c>RoutingDecisionService.NormalizeModelName</c>, so
    /// <c>qwen3.8:27b</c>, <c>qwen3.8</c> and <c>QWEN3.8:27B</c> all resolve to the same
    /// profile keyed by the native name.
    /// </summary>
    private static ModelNodeProfile? FindModel(FleetRecommendations matrix, string requestedNormalized)
    {
        foreach (var (key, modelProfile) in matrix.Models)
        {
            if (string.Equals(RoutingDecisionService.NormalizeModelName(key), requestedNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return modelProfile;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public void Activate(FleetRecommendations matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        lock (_gate)
        {
            _matrix = matrix;
        }
    }

    /// <inheritdoc/>
    public bool TryActivateFromFile(out FleetRecommendations matrix)
    {
        var loaded = LoadFromFile(failSoft: true);
        if (loaded is null)
        {
            matrix = _matrix;
            return false;
        }

        Activate(loaded);
        matrix = loaded;
        return true;
    }

    /// <summary>
    /// Reads <c>recommended.json</c>; null when the file is missing/empty or unparseable
    /// (corrupt → warn-log, fail-soft). Deserializes through the source-generated
    /// <see cref="OllamaRouterJsonSerializerContext"/> (AOT-safe).
    /// </summary>
    private FleetRecommendations? LoadFromFile(bool failSoft)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var matrix = JsonSerializer.Deserialize(json, OllamaRouterJsonSerializerContext.Default.FleetRecommendations);
            if (matrix is null || (matrix.Roles.Count == 0 && matrix.Models.Count == 0))
            {
                return null;
            }

            return new FleetRecommendations(
                matrix.GeneratedAt,
                new System.Collections.Generic.Dictionary<string, RoleRecommendation>(matrix.Roles, StringComparer.OrdinalIgnoreCase),
                new System.Collections.Generic.Dictionary<string, ModelNodeProfile>(matrix.Models, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (failSoft)
        {
            _logger?.LogWarning(ex,
                "Failed to read recommendation matrix from {Path}; continuing with an empty matrix.", FilePath);
            return null;
        }
    }
}
