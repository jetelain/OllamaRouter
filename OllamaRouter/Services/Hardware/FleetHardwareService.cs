using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;

namespace OllamaRouter.Services.Hardware;

/// <summary>
/// In-memory + persisted fleet hardware topology store with parallel peer-daemon refresh.
/// <para>
/// Read path (<see cref="GetFleetHardwareAsync"/>): for each effective target — the local
/// workstation target is served from <see cref="IHardwareProfileProvider"/> (never a network
/// call); a stored manual/persisted/peer profile is served immediately (a background peer
/// refresh is kicked when its 5-minute TTL expired); otherwise a single synchronous peer
/// probe (≤ 3 s, all targets in parallel) is attempted. Fail-soft everywhere: an
/// unreachable node reports <c>reachable: false</c> and its stored (or null) profile.
/// </para>
/// <para>
/// Persistence: <c>targets-hardware.json</c> as a flat
/// <c>{ targetName: { profile, updatedAt, source } }</c> dictionary serialized via the
/// AOT <see cref="OllamaRouterJsonSerializerContext"/>. A missing/corrupt file yields an
/// empty store (mirrors <see cref="TargetAvailabilityService"/>); the in-memory map always
/// wins even when a disk write fails.
/// </para>
/// </summary>
public sealed class FleetHardwareService : IFleetHardwareService, IHostedService
{
    private const string HardwarePath = "/api/router/hardware";
    private static readonly TimeSpan PeerProbeTtl = TimeSpan.FromDays(1);

    private readonly IOptions<OllamaRouterOptions> _options;
    private readonly IHardwareProfileProvider _localProvider;
    private readonly HttpClient _probeClient;
    private readonly ILogger<FleetHardwareService> _logger;
    private readonly string _stateFilePath;

    private readonly ConcurrentDictionary<string, HardwareProfileState> _profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Utc, bool Reachable)> _lastPeerProbeUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public FleetHardwareService(
        IOptions<OllamaRouterOptions> options,
        IHardwareProfileProvider localProvider,
        HttpClient probeClient,
        ILogger<FleetHardwareService> logger,
        string stateFilePath)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _localProvider = localProvider ?? throw new ArgumentNullException(nameof(localProvider));
        _probeClient = probeClient ?? throw new ArgumentNullException(nameof(probeClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateFilePath = stateFilePath ?? throw new ArgumentNullException(nameof(stateFilePath));

        LoadPersistedState();
    }

    public NodeHardwareProfile? GetProfile(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return null;
        }

        if (_profiles.TryGetValue(targetName, out var state) && state.Profile is not null)
        {
            return state.Profile;
        }

        // Cache miss: check if this is the local workstation target
        var match = _options.Value.GetEffectiveTargets()
            .FirstOrDefault(t => string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase));

        if (match is not null && IsLocalWorkstationTarget(match))
        {
            try
            {
                var localTask = _localProvider.GetLocalProfileAsync();
                if (localTask.IsCompletedSuccessfully && localTask.Result is { } localProfile)
                {
                    StoreProfile(match.Name!, localProfile, "local", persist: false);
                    return localProfile;
                }
            }
            catch
            {
                // Degrade safely on exception
            }
        }

        return null;
    }

    public int? GetEffectiveMaxVramMB(OllamaTargetOptions target)
    {
        if (target is null)
        {
            return null;
        }

        if (target.MaxVramMB.HasValue)
        {
            return target.MaxVramMB.Value;
        }

        if (!string.IsNullOrEmpty(target.Name))
        {
            var profile = GetProfile(target.Name);
            if (profile is not null && profile.TotalFastMemoryMB > 0)
            {
                return (int)profile.TotalFastMemoryMB;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<FleetHardwareNode>> GetFleetHardwareAsync(CancellationToken ct = default)
    {
        var targets = _options.Value.GetEffectiveTargets();
        var localProfile = await GetLocalProfileSafeAsync(ct).ConfigureAwait(false);

        var rows = new List<FleetHardwareNode>(targets.Count);
        // Rows that still need a peer probe: (row index, target).
        var pendingProbes = new List<(int RowIndex, OllamaTargetOptions Target)>();

        foreach (var target in targets)
        {
            var name = target.Name!;
            ct.ThrowIfCancellationRequested();

            if (IsLocalWorkstationTarget(target))
            {
                // Local workstation: served from the local provider, never a network call.
                var profile = localProfile;
                if (profile is not null &&
                    (_profiles.TryGetValue(name, out var stored) ? stored.Source != "manual" : true))
                {
                    // Keep the store warm for GetProfile consumers (peer refresh never
                    // overwrites manual entries).
                    StoreProfile(name, profile, "local", persist: false);
                }

                rows.Add(new FleetHardwareNode(name, "local", profile, profile is not null));
                continue;
            }

            if (_profiles.TryGetValue(name, out var existing) && existing.Profile is not null)
            {
                // A profile exists: serve it now (Source is "manual"/"peer"/"persisted").
                // Manual entries are authoritative — a peer refresh never overwrites them
                // (it only refreshes reachability); peer/persisted entries are replaced by
                // the probe result when the TTL expired.
                // Reachability: report the last observed probe outcome within the TTL
                // window (a stored profile right after a FAILED probe must not suddenly
                // claim the peer is reachable).
                var reachable = !_lastPeerProbeUtc.TryGetValue(name, out var lastProbe) || lastProbe.Reachable;
                rows.Add(new FleetHardwareNode(name, existing.Source, existing.Profile, reachable));
                if (PeerProbeDue(name))
                {
                    pendingProbes.Add((rows.Count - 1, target));
                }

                continue;
            }

            // No profile at all: one synchronous peer probe within the request budget.
            rows.Add(new FleetHardwareNode(name, "none", null, false));
            pendingProbes.Add((rows.Count - 1, target));
        }

        if (pendingProbes.Count > 0)
        {
            var probes = await Task.WhenAll(pendingProbes.Select(p => ProbePeerAsync(p.Target))).ConfigureAwait(false);
            for (var i = 0; i < pendingProbes.Count; i++)
            {
                var (rowIndex, target) = pendingProbes[i];
                var name = target.Name!;
                var result = probes[i];

                _lastPeerProbeUtc[name] = (DateTime.UtcNow, result.Reachable);

                var stored = _profiles.TryGetValue(name, out var current) ? current : null;
                var isManual = stored is not null && string.Equals(stored.Source, "manual", StringComparison.OrdinalIgnoreCase);

                if (result.Profile is not null)
                {
                    if (!isManual)
                    {
                        // Peer data replaces peer/persisted entries; manual entries are
                        // authoritative and are NEVER overwritten by a peer refresh.
                        StoreProfile(name, result.Profile, "peer", persist: true);
                        rows[rowIndex] = new FleetHardwareNode(name, "peer", result.Profile, true);
                    }
                    else
                    {
                        // Peer answered (or not), but the manual profile stays authoritative:
                        // only the reachability flag is refreshed from the probe.
                        rows[rowIndex] = new FleetHardwareNode(name, "manual", stored!.Profile, result.Reachable);
                    }
                }
                else if (stored is not null)
                {
                    // Probe failed: keep the stored profile, mark unreachable.
                    rows[rowIndex] = new FleetHardwareNode(name, stored.Source, stored.Profile, false);
                }
                else
                {
                    rows[rowIndex] = new FleetHardwareNode(name, "none", null, false);
                }
            }
        }

        return rows;
    }

    public Task<bool> UpsertProfileAsync(string targetName, NodeHardwareProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetName) || profile is null)
        {
            return Task.FromResult(false);
        }

        var match = _options.Value.GetEffectiveTargets()
            .FirstOrDefault(t => string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return Task.FromResult(false);
        }

        StoreProfile(match.Name!, profile, "manual", persist: true);
        return Task.FromResult(true);
    }

    // ------------------------------------------------------------------
    //  Peer probing
    // ------------------------------------------------------------------

    private readonly record struct ProbeResult(NodeHardwareProfile? Profile, bool Reachable);

    private async Task<ProbeResult> ProbePeerAsync(OllamaTargetOptions target)
    {
        try
        {
            var url = target.Url!.TrimEnd('/') + HardwarePath;
            using var response = await _probeClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(null, false);
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var profile = await JsonSerializer.DeserializeAsync(
                stream,
                OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile).ConfigureAwait(false);
            return new ProbeResult(profile, true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Peer hardware probe of {Target} ({Url}) failed.", target.Name, target.Url);
            return new ProbeResult(null, false);
        }
    }

    private bool PeerProbeDue(string targetName)
    {
        return !_lastPeerProbeUtc.TryGetValue(targetName, out var last) ||
               DateTime.UtcNow - last.Utc >= PeerProbeTtl;
    }

    // ------------------------------------------------------------------
    //  Store + persistence
    // ------------------------------------------------------------------

    private void StoreProfile(string targetName, NodeHardwareProfile profile, string source, bool persist)
    {
        lock (_gate)
        {
            _profiles[targetName] = new HardwareProfileState(profile, DateTimeOffset.UtcNow, source);

            if (!persist)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var state = new Dictionary<string, HardwareProfileState>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in _profiles)
                {
                    state[pair.Key] = pair.Value;
                }

                File.WriteAllText(
                    _stateFilePath,
                    JsonSerializer.Serialize(state, OllamaRouterJsonSerializerContext.Default.DictionaryStringHardwareProfileState));
            }
            catch (Exception ex)
            {
                // Failsafe: the in-memory state always wins even when the disk write fails.
                _logger.LogWarning(ex, "Failed to persist fleet hardware profiles to {StateFilePath}.", _stateFilePath);
            }
        }
    }

    private void LoadPersistedState()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var state = JsonSerializer.Deserialize(
                        File.ReadAllText(_stateFilePath),
                        OllamaRouterJsonSerializerContext.Default.DictionaryStringHardwareProfileState);
                if (state is not null)
                {
                    foreach (var (name, entry) in state)
                    {
                        // Reloaded persisted/peer profiles are re-labeled "persisted" (a peer
                        // refresh may overwrite them again; manual ones stay authoritative).
                        _profiles[name] = new HardwareProfileState(
                            entry.Profile,
                            entry.UpdatedAt,
                            string.Equals(entry.Source, "manual", StringComparison.OrdinalIgnoreCase)
                                ? "manual"
                                : "persisted");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt file → log + start empty (mirrors TargetAvailabilityService).
            _logger.LogWarning(ex, "Failed to read fleet hardware profiles from {StateFilePath}; starting with an empty store.", _stateFilePath);
        }
    }

    private async Task<NodeHardwareProfile?> GetLocalProfileSafeAsync(CancellationToken ct)
    {
        try
        {
            return await _localProvider.GetLocalProfileAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local hardware profile probe failed; serving null for the local target.");
            return null;
        }
    }

    /// <summary>
    /// A target IS this workstation iff its configured URL host is <c>127.0.0.1</c>/<c>localhost</c>
    /// (case-insensitive) or its <c>Name</c> equals <c>"Local"</c> (case-insensitive) —
    /// the same rule as the <c>/api/router/fleet</c> endpoint.
    /// </summary>
    private static bool IsLocalWorkstationTarget(OllamaTargetOptions target)
    {
        if (string.Equals(target.Name, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(target.Url) &&
               Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) &&
               (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Asynchronous boot refresh to prime the hardware cache (non-blocking)
        _ = Task.Run(async () =>
        {
            try
            {
                await GetFleetHardwareAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Initial fleet hardware probe encountered an error.");
            }
        }, CancellationToken.None);

        _loopTask = Task.Run(() => RunPeriodicLoopAsync(token), CancellationToken.None);

        _logger.LogInformation("Fleet hardware service started (peer probe TTL: {Ttl}).", PeerProbeTtl);
        return Task.CompletedTask;
    }

    private async Task RunPeriodicLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PeerProbeTtl);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
                await GetFleetHardwareAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Periodic fleet hardware refresh encountered an error.");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }
}

