using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.ViewModels;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// One fleet-target row of Step 2 (task #103, reworked by task #151): the identity, liveness,
/// enablement and hardware (GPU name / VRAM / bandwidth) of a target as reported by
/// <c>GET /api/router/fleet</c> — the Cloud pseudo-target row is EXCLUDED by the view model
/// (the fleet wire DTO carries no type/provider, so none here either).
/// </summary>
public sealed class WizardTargetRow
{
    public required string Name { get; init; }
    public string? Url { get; init; }
    public string Liveness { get; init; } = "Unknown";
    public long? LatencyMs { get; init; }
    public bool Enabled { get; init; }
    public string? GpuName { get; init; }
    public int? VramMb { get; init; }
    public double? BandwidthGBs { get; init; }

    public string VramText => VramMb is { } v ? $"{v} MB" : string.Empty;
    public string BandwidthText => BandwidthGBs is { } b ? $"{b:0.##} GB/s" : string.Empty;
}

/// <summary>
/// Step 2 — Fleet Targets (task #103; reworked by task #151): the live fleet table (2 s poll,
/// hardware columns, the Cloud pseudo-target EXCLUDED), the idempotent auto-add of the Local
/// node on step entry, and the main app's dedicated add dialogs ("Add Ollama Node" /
/// "Add Provider", opened view-side via <see cref="Views.Wizard.WizardDialogLauncher"/>).
/// All through the EXISTING client surface (no new SDK methods).
/// </summary>
public sealed partial class WizardTargetsStepViewModel : ObservableObject, IWizardStep
{
    private const string LocalTargetName = "Local";
    private const string LocalTargetUrl = "http://127.0.0.1:11435";

    /// <summary>The wire name of the Cloud pseudo-target the server appends to the fleet
    /// (always LAST; <c>RoutingTarget.Cloud.Value</c>): excluded from the table — the same
    /// convention as <see cref="CartographyViewModel.CloudPseudoTargetName"/>.</summary>
    private const string CloudPseudoTargetName = "Cloud";

    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private readonly Func<Uri> _daemonBaseUrl;
    private readonly DashboardPoller _poller;
    // Single-writer gate (task #151 — the task #130 addendum bug class, Explorer bug #7): the
    // poller tick (thread pool) and the command-side refreshes (UI thread — after an add, a
    // step re-entry) both rebuild Rows via Clear()/Add; without serialization the overlapping
    // rebuilds interleave and DUPLICATE rows. Same pattern as
    // WizardBenchmarkStepViewModel._refreshGate / RouterTabViewModel._refreshGate.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _disposed;

    public WizardTargetsStepViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<CancellationToken, Task> pollerTick,
        Func<TimeSpan, CancellationToken, Task>? pollerDelay = null,
        Func<Uri>? daemonBaseUrl = null)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        // The connection-profile daemon base URL (task #158) — a Func (not a snapshot), so a
        // later SwitchConnectionAsync profile swap is reflected at failure time without
        // rebuilding the view model (the same accessor pattern as _clientAccessor).
        _daemonBaseUrl = daemonBaseUrl ?? (() => OllamaRouterDaemonLauncher.DefaultBaseUrl);
        _poller = new DashboardPoller(pollerTick ?? throw new ArgumentNullException(nameof(pollerTick)),
            TimeSpan.FromSeconds(2), pollerDelay);
    }

    /// <summary>
    /// The single actionable daemon-down message of Step 2 (task #158 — the shared text of
    /// the fleet poll, the Local auto-add and the add-target transport failures), with the
    /// endpoint derived from the connection profile at failure time.
    /// </summary>
    private string DaemonDownMessage() => WizardTargetErrors.DaemonDownMessage(_daemonBaseUrl());

    /// <summary>
    /// The no-churn failure-message guard (task #158 — sub-task 3): assigns <see cref="Message"/>
    /// ONLY when it differs from the current value, so the 2 s poll re-setting the SAME
    /// daemon-down text is a no-op (no <c>PropertyChanged("Message")</c>, no UI flicker) while
    /// a NEW failure type (a different string) always wins. (The generated setter equality
    /// guard already prevents redundant raises; this makes the invariant explicit and
    /// directly unit-testable.)
    /// </summary>
    private void SetFailureMessage(string message)
    {
        if (!string.Equals(Message, message, StringComparison.Ordinal))
        {
            Message = message;
        }
    }

    /// <summary>The live fleet rows (the Cloud pseudo-target excluded; refreshed from
    /// <c>GET /api/router/fleet</c>).</summary>
    public ObservableCollection<WizardTargetRow> Rows { get; } = [];

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Adds a target from the main app's dedicated dialog (task #151 — the
    /// <see cref="AddTargetRequest"/> is built by the SAME form the Fleet tab uses, via
    /// <see cref="Views.Wizard.WizardDialogLauncher"/>), then refreshes the fleet table so the
    /// new target appears on the next poll. Errors map to the shared readable messages
    /// (<see cref="WizardTargetErrors.MapAddTarget"/>); nothing here throws (an exception
    /// escaping a command faults the async-void command bridge and crashes the app).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddTarget))]
    private async Task AddTarget(AddTargetRequest request)
    {
        IsBusy = true;
        try
        {
            await _clientAccessor().AddTargetAsync(request).ConfigureAwait(false);
            Message = $"Target '{request.Name}' added.";
        }
        catch (OllamaRouterApiException ex)
        {
            SetFailureMessage(WizardTargetErrors.MapAddTarget(ex, _daemonBaseUrl()));
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            SetFailureMessage(WizardTargetErrors.MapAddTarget(ex, _daemonBaseUrl()));
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool CanAddTarget() => !IsBusy;

    /// <summary>
    /// The poller tick / on-demand refresh (task #151): re-reads the fleet (live liveness +
    /// hardware) and rebuilds the rows — the Cloud pseudo-target is EXCLUDED. Single-writer
    /// (task #151 / the task #130 addendum): overlapping refreshes serialize on
    /// <c>_refreshGate</c> so the row Clear()/Add rebuilds never interleave. The gate wait is
    /// deliberately NON-CANCELLABLE: a self-stopped tick's cancellation cancels the endpoint
    /// reads INSIDE the gate, not the serialization wait (a cancellable wait drops the first
    /// tick out before it ever runs). The router may be DOWN (the canonical first-run state
    /// before Step 1 has started it): failures surface as a <see cref="Message"/> instead of
    /// throwing, because an exception escaping a command faults the async-void command bridge
    /// and crashes the app.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>The refresh core (called under <c>_refreshGate</c> — single writer).</summary>
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var client = _clientAccessor();

        try
        {
            var fleet = await client.GetFleetAsync(ct).ConfigureAwait(false);
            var rows = new List<WizardTargetRow>(fleet.Targets.Count);
            foreach (var target in fleet.Targets)
            {
                // The server ALWAYS appends the Cloud pseudo-target last (RouterFleetEndpoints):
                // it is not a real fleet node — excluded by NAME (the Cartography convention).
                if (string.Equals(target.Name, CloudPseudoTargetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rows.Add(new WizardTargetRow
                {
                    Name = target.Name,
                    Url = target.Url,
                    Liveness = target.Liveness,
                    LatencyMs = target.LatencyMs,
                    Enabled = target.Enabled,
                    GpuName = target.Hardware.GpuName,
                    VramMb = target.Hardware.MaxVramMB,
                    BandwidthGBs = target.Hardware.BandwidthGBs,
                });
            }

            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }
        }
        catch (OllamaRouterApiException ex)
        {
            Message = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (System.Net.Http.HttpRequestException)
        {
            // The router is DOWN (the canonical first-run state before Step 1 has started
            // it): the shared actionable daemon-down message (task #158) — never the raw
            // transport text (which leaked "…actively refused it. (127.0.0.1:11434)").
            SetFailureMessage(DaemonDownMessage());
        }
    }

    /// <summary>
    /// Auto-adds the standard local node on step entry (task #151 — idempotent): a target
    /// named <c>Local</c> (ANY URL) is a silent no-op, so re-entry never re-adds; an absent
    /// one is added with <c>testConnectionFirst</c> (server-probed). Runs under the same
    /// <c>_refreshGate</c> as the refreshes (holding the gate across the bounded network calls
    /// is the established pattern). Every failure is a readable <see cref="Message"/> — never
    /// a throw and never a duplicate:
    /// <list type="bullet">
    /// <item>409 Conflict — the concurrent-add race: a "Local" appeared between the config
    /// check and the add; left unchanged.</item>
    /// <item>422 — Local Ollama is unreachable (fix Step 1 and retry).</item>
    /// <item>transport failure — the router is DOWN (the canonical first-run state): the
    /// shared daemon-down message (task #158); the next step entry retries.</item>
    /// <item>any other router error — the daemon responded (not a down-state): the legacy
    /// actionable text (unchanged by task #158); the next step entry retries.</item>
    /// </list>
    /// </summary>
    private async Task EnsureLocalAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var client = _clientAccessor();
            try
            {
                var config = await client.GetConfigAsync(ct).ConfigureAwait(false);
                if (config.Targets.Any(t =>
                        string.Equals(t.Name, LocalTargetName, StringComparison.OrdinalIgnoreCase)))
                {
                    return; // Present (any URL): idempotent no-op — re-entry never re-adds.
                }

                await client.AddTargetAsync(
                        new AddTargetRequest(LocalTargetName, LocalTargetUrl, TestConnectionFirst: true))
                    .ConfigureAwait(false);
                Message = "Local node added.";
            }
            catch (OllamaRouterApiException ex) when (ex.Status == HttpStatusCode.Conflict)
            {
                Message = "A target named 'Local' already exists — left unchanged.";
            }
            catch (OllamaRouterApiException ex) when (ex.Status == HttpStatusCode.UnprocessableEntity)
            {
                Message = "Local Ollama is unreachable — fix Step 1 and retry.";
            }
            catch (OllamaRouterApiException)
            {
                // The daemon RESPONDED with a router error (not a down-state): the legacy
                // actionable text (unchanged by task #158).
                Message = "Connection failed — start the router in Step 1 first.";
            }
            catch (System.Net.Http.HttpRequestException)
            {
                // The router is DOWN (the canonical first-run state before Step 1 has
                // started it): the shared actionable daemon-down message (task #158),
                // identical to the fleet poll's — no throw, the next step entry retries.
                SetFailureMessage(DaemonDownMessage());
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Step lifecycle (task #151): auto-add the Local node (AWAITED, so navigation
    /// deterministically completes it), THEN start the 2 s poller (the first tick runs
    /// immediately).
    /// </summary>
    public async Task OnStepEnteredAsync(CancellationToken ct = default)
    {
        await EnsureLocalAsync(ct).ConfigureAwait(false);
        await _poller.StartAsync().ConfigureAwait(false);
    }

    /// <summary>Step lifecycle: stop the poller (no orphan work).</summary>
    public Task OnStepLeftAsync() => _poller.StopAsync();

    partial void OnIsBusyChanged(bool value) => AddTargetCommand.NotifyCanExecuteChanged();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _poller.DisposeAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
    }
}
