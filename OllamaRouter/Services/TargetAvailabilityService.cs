using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OllamaRouter.Services;

/// <inheritdoc cref="ITargetAvailabilityService"/>
public sealed class TargetAvailabilityService : ITargetAvailabilityService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string stateFilePath;
    private readonly ILogger<TargetAvailabilityService> logger;
    private readonly object gate = new();
    private bool local = true;
    private bool remote = true;
    private bool cloud = true;

    public TargetAvailabilityService(string stateFilePath, ILogger<TargetAvailabilityService> logger)
    {
        this.stateFilePath = stateFilePath;
        this.logger = logger;
        Load();
    }

    public bool IsEnabled(RoutingTarget target)
    {
        lock (gate)
        {
            return target switch
            {
                RoutingTarget.Local => local,
                RoutingTarget.Remote => remote,
                RoutingTarget.Cloud => cloud,
                _ => true
            };
        }
    }

    public TargetAvailabilitySnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new TargetAvailabilitySnapshot(local, remote, cloud);
        }
    }

    public void Update(bool local, bool remote, bool cloud)
    {
        lock (gate)
        {
            // Failsafe: the in-memory state (used by routing decisions and the monitoring UI)
            // is always updated; persisting to the state file is best-effort only.
            try
            {
                var directory = Path.GetDirectoryName(stateFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(stateFilePath, JsonSerializer.Serialize(new TargetsState(local, remote, cloud), SerializerOptions));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist target availability to {StateFilePath}: Local={Local}, Remote={Remote}, Cloud={Cloud}", stateFilePath, local, remote, cloud);
            }

            this.local = local;
            this.remote = remote;
            this.cloud = cloud;
        }

        logger.LogInformation("Targets updated via monitoring UI: Local={Local}, Remote={Remote}, Cloud={Cloud}", local, remote, cloud);
    }

    private void Load()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(stateFilePath))
                {
                    return;
                }

                var state = JsonSerializer.Deserialize<TargetsState>(File.ReadAllText(stateFilePath), SerializerOptions);
                if (state is null)
                {
                    return;
                }

                local = state.Local;
                remote = state.Remote;
                cloud = state.Cloud;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read target availability from {StateFilePath}; all targets will be considered enabled.", stateFilePath);
            }
        }
    }

    private sealed record TargetsState(bool Local, bool Remote, bool Cloud);
}
