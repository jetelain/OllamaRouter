using System.Text.Json;
using Microsoft.Extensions.Logging;
using OllamaRouter.Serialization;

namespace OllamaRouter.Services;

/// <inheritdoc cref="ITargetAvailabilityService"/>
public sealed class TargetAvailabilityService : ITargetAvailabilityService
{
    private readonly string stateFilePath;
    private readonly ILogger<TargetAvailabilityService> logger;
    private readonly IReadOnlyList<RoutingTarget> targets;
    private readonly Dictionary<RoutingTarget, bool> flags = new();
    private readonly object gate = new();

    public TargetAvailabilityService(
        string stateFilePath,
        IReadOnlyList<RoutingTarget> targets,
        ILogger<TargetAvailabilityService> logger)
    {
        this.stateFilePath = stateFilePath;
        this.targets = DedupeCaseInsensitive(targets);
        this.logger = logger;

        // All targets default to enabled; the persisted state (if any) overrides them.
        var persisted = LoadPersistedState();
        foreach (var target in this.targets)
        {
            // Case-insensitive key match; unknown persisted keys are ignored, missing keys default to true.
            var match = persisted.FirstOrDefault(pair => string.Equals(pair.Key, target.Value, StringComparison.OrdinalIgnoreCase));
            flags[target] = match.Key is null ? true : match.Value;
        }
    }

    public bool IsEnabled(RoutingTarget target)
    {
        lock (gate)
        {
            return flags.GetValueOrDefault(target, true);
        }
    }

    public TargetAvailabilitySnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new TargetAvailabilitySnapshot(
                flags.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    public void Update(IReadOnlyDictionary<string, bool> flags)
    {
        lock (gate)
        {
            // Apply the matched flags in memory (the in-memory state is used by routing
            // decisions and the monitoring UI, and is always updated, even if the persistence
            // below fails: failsafe behavior). Unknown keys are ignored.
            foreach (var (name, value) in flags)
            {
                foreach (var target in this.targets)
                {
                    if (string.Equals(target.Value, name, StringComparison.OrdinalIgnoreCase))
                    {
                        this.flags[target] = value;
                    }
                }
            }

            // Best-effort persistence of the full state as a flat { [targetName]: bool } object.
            try
            {
                var directory = Path.GetDirectoryName(stateFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in this.targets)
                {
                    state[target.Value] = this.flags.TryGetValue(target, out var enabled) ? enabled : true;
                }

                File.WriteAllText(stateFilePath, JsonSerializer.Serialize(state, OllamaRouterJsonSerializerContext.Default.DictionaryStringBoolean));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist target availability to {StateFilePath}: {Flags}", stateFilePath, string.Join(", ", this.flags.Select(pair => $"{pair.Key}={pair.Value}")));
            }
        }

        logger.LogInformation("Targets updated via monitoring UI: {Flags}", string.Join(", ", flags.Select(pair => $"{pair.Key}={pair.Value}")));
    }

    // Case-insensitive dedup so a target configured with a different casing (or Cloud
    // appearing both in the list and as the pseudo-target) maps to a single slot.
    private static IReadOnlyList<RoutingTarget> DedupeCaseInsensitive(IEnumerable<RoutingTarget> targets) =>
        targets.DistinctBy(target => target.Value, StringComparer.OrdinalIgnoreCase).ToList();

    // Reads the persisted state file without touching the in-memory flags; a missing or
    // corrupt file yields an empty dictionary (all targets stay enabled: failsafe behavior).
    private Dictionary<string, bool> LoadPersistedState()
    {
        lock (gate)
        {
            try
            {
                if (File.Exists(stateFilePath))
                {
                    return JsonSerializer.Deserialize(File.ReadAllText(stateFilePath), OllamaRouterJsonSerializerContext.Default.DictionaryStringBoolean)
                        ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read target availability from {StateFilePath}; all targets will be considered enabled.", stateFilePath);
            }
        }

        return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }
}
