using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.ViewModels;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// One node hardware row of Step 3 (task #103, spec §3.5): from <c>GET
/// /api/router/fleet/hardware</c> — the GPU detail, the fast-memory total and the estimated
/// bandwidth, plus a note when no hardware profile was detected.
/// </summary>
public sealed class BenchmarkHardwareRow
{
    public required string Target { get; init; }
    public string Source { get; init; } = "unknown";
    public bool Reachable { get; init; }
    public string? GpuName { get; init; }
    public string? GpuVendor { get; init; }
    public int? VramMib { get; init; }
    public long? TotalFastMemoryMib { get; init; }
    public double? BandwidthMibPerSec { get; init; }
    public string? Note { get; init; }

    public string VramText => VramMib is { } v ? $"VRAM: {v} MB" : string.Empty;
    public string BandwidthText => BandwidthMibPerSec is { } b ? $"BW: {b:0} MB/s" : string.Empty;
}

/// <summary>
/// One target's benchmark-run row of Step 3 (task #103): the lifecycle status and the
/// start/finish stamps.
/// </summary>
public sealed class BenchmarkRow
{
    public required string Target { get; init; }
    public BenchmarkRunStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    public string StatusText => Status.ToString();
}

/// <summary>
/// One (target × model × context) result row of Step 3 (task #103): the measured decode
/// throughput, the viability decision (≥ <c>20</c> t/s) and the non-viable reason.
/// </summary>
public sealed class BenchmarkResultRow
{
    public required string Target { get; init; }
    public required string Model { get; init; }
    public string Role { get; init; } = string.Empty;
    public int NumCtx { get; init; }
    public double DecodeTokensPerSecond { get; init; }
    public bool Viable { get; init; }
    public string? Reason { get; init; }

    public string ThroughputText => $"{DecodeTokensPerSecond:0.0} t/s";
    public string ContextText => $"ctx {NumCtx}";
}

/// <summary>
/// Step 3 — Micro-Benchmark (task #103, spec §3.5): the per-node hardware table, the benchmark
/// trigger (<c>POST /api/router/benchmark</c> with <c>null</c> = all active non-Cloud targets) and
/// the live 2 s results poll (per-context tokens/s + viability), self-stopping when every queued
/// target is terminal.
/// </summary>
public sealed partial class WizardBenchmarkStepViewModel : ObservableObject, IWizardStep
{
    /// <summary>The viability floor (mirrors
    /// <c>CandidateModelCatalogOptions.MinViableTokensPerSecond</c>) for the display decision.</summary>
    internal const double MinViableTokensPerSecond = 20.0;

    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private readonly DashboardPoller _poller;
    private List<string> _queuedTargets = [];
    private bool _disposed;

    public WizardBenchmarkStepViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<CancellationToken, Task> pollerTick,
        Func<TimeSpan, CancellationToken, Task>? pollerDelay = null)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _poller = new DashboardPoller(pollerTick ?? throw new ArgumentNullException(nameof(pollerTick)),
            TimeSpan.FromSeconds(2), pollerDelay);
    }

    /// <summary>The per-node hardware table.</summary>
    public ObservableCollection<BenchmarkHardwareRow> HardwareRows { get; } = [];

    /// <summary>One row per benchmarked target (status + stamps).</summary>
    public ObservableCollection<BenchmarkRow> BenchmarkRows { get; } = [];

    /// <summary>The per-(target × model × context) result rows (all targets, last results).</summary>
    public ObservableCollection<BenchmarkResultRow> ResultRows { get; } = [];

    /// <summary>The targets queued by the last <c>RunBenchmarks</c> (drives the self-stop).</summary>
    public IReadOnlyList<string> QueuedTargets => _queuedTargets;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Queues a benchmark over all active non-Cloud targets and starts the live poll.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunBenchmarks()
    {
        IsBusy = true;
        try
        {
            var response = await _clientAccessor().StartBenchmarkAsync(null).ConfigureAwait(false);
            _queuedTargets = response.QueuedTargets.ToList();
            Error = null;
            Message = _queuedTargets.Count == 0
                ? "No active non-Cloud targets to benchmark — add a node in Step 2 first."
                : $"Benchmarking {_queuedTargets.Count} target(s)…";
            await _poller.StartAsync().ConfigureAwait(false);
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Error = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Clears the stored benchmark results (in-memory + persisted).</summary>
    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearResults()
    {
        IsBusy = true;
        try
        {
            await _clientAccessor().ClearBenchmarkResultsAsync().ConfigureAwait(false);
            _queuedTargets = [];
            BenchmarkRows.Clear();
            ResultRows.Clear();
            Message = "Benchmark results cleared.";
        }
        catch (OllamaRouterApiException ex)
        {
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool CanRun() => !IsBusy;

    private bool CanClear() => !IsBusy;

    /// <summary>
    /// The poller tick / on-demand refresh: re-reads the hardware table (best-effort) and the
    /// benchmark results, then self-stops the poller when every queued target is terminal.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var client = _clientAccessor();

        try
        {
            var hardware = await client.GetFleetHardwareAsync(ct).ConfigureAwait(false);
            LoadHardware(hardware);
        }
        catch (OllamaRouterApiException)
        {
            // Hardware is a best-effort supplement; results still refresh below.
        }
        catch (System.Net.Http.HttpRequestException)
        {
            // Same — a connection failure surfaces via the results fetch below.
        }

        try
        {
            var results = await client.GetBenchmarkResultsAsync(ct).ConfigureAwait(false);
            LoadResults(results);
            Error = null;
            await MaybeSelfStopAsync().ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Error = $"Connection failed: {ex.Message}";
        }
    }

    /// <summary>Step lifecycle: start the 2 s poller (the first tick runs immediately).</summary>
    public Task OnStepEnteredAsync(CancellationToken ct = default) => _poller.StartAsync();

    /// <summary>Step lifecycle: stop the poller (no orphan work).</summary>
    public Task OnStepLeftAsync() => _poller.StopAsync();

    private void LoadHardware(RouterFleetHardwareResponse hardware)
    {
        HardwareRows.Clear();
        foreach (var node in hardware.Nodes)
        {
            var gpu = node.Profile?.Gpus.FirstOrDefault();
            HardwareRows.Add(new BenchmarkHardwareRow
            {
                Target = node.Target,
                Source = node.Source,
                Reachable = node.Reachable,
                GpuName = gpu?.ModelName,
                GpuVendor = gpu?.Vendor,
                VramMib = gpu?.VramMB,
                TotalFastMemoryMib = node.Profile?.TotalFastMemoryMB,
                BandwidthMibPerSec = node.Profile is { } profile && profile.EffectiveBandwidthGBs > 0
                    ? profile.EffectiveBandwidthGBs * 1024.0
                    : null,
                Note = node.Source.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    ? "No hardware profile detected"
                    : null,
            });
        }
    }

    private void LoadResults(RouterBenchmarkResultsResponse results)
    {
        BenchmarkRows.Clear();
        ResultRows.Clear();
        foreach (var target in results.Targets)
        {
            BenchmarkRows.Add(new BenchmarkRow
            {
                Target = target.Target,
                Status = target.Status,
                StartedAt = target.StartedAt,
                FinishedAt = target.FinishedAt,
            });

            foreach (var result in target.Results)
            {
                ResultRows.Add(new BenchmarkResultRow
                {
                    Target = target.Target,
                    Model = result.Model,
                    Role = result.Role.ToString(),
                    NumCtx = result.NumCtx,
                    DecodeTokensPerSecond = result.DecodeTokensPerSecond,
                    Viable = result.DecodeTokensPerSecond >= MinViableTokensPerSecond,
                    Reason = result.Reason,
                });
            }
        }
    }

    /// <summary>
    /// The self-stop (task #103): once every queued target is terminal, stop the poller —
    /// fire-and-forget (<see cref="Task.Run"/>) so <c>StopAsync</c> is NEVER awaited from within
    /// its own tick (a self-await would deadlock the poller loop).
    /// </summary>
    private Task MaybeSelfStopAsync()
    {
        if (_queuedTargets.Count == 0)
        {
            return Task.CompletedTask;
        }

        var allTerminal = _queuedTargets.All(name =>
            BenchmarkRows.FirstOrDefault(r => r.Target == name) is { } row && IsTerminal(row.Status));
        if (allTerminal)
        {
            var poller = _poller;
            _ = Task.Run(() => poller.StopAsync());
        }

        return Task.CompletedTask;
    }

    private static bool IsTerminal(BenchmarkRunStatus status) =>
        status is BenchmarkRunStatus.Succeeded
            or BenchmarkRunStatus.PartiallySucceeded
            or BenchmarkRunStatus.Failed
            or BenchmarkRunStatus.Cancelled;

    partial void OnIsBusyChanged(bool value)
    {
        RunBenchmarksCommand.NotifyCanExecuteChanged();
        ClearResultsCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _poller.DisposeAsync().ConfigureAwait(false);
    }
}
