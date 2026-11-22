using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.Logging;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Targets;
using Serilog;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// Step 3 — Cloud &amp; External Providers (task #151): the Ollama Cloud (ollama.com)
/// overflow configuration — the cloud switch (task #161: an OPTIMISTIC click flip that the
/// wire response confirms and a failed toggle reverts to the previous value), the default
/// cloud model dropdown (fed by the cloud models visible in the fleet catalog,
/// <c>GET /api/router/models</c>, the ONLY wire surface that exposes cloud model names) and
/// the relay sign-in hint — plus the main app's dedicated "Add Provider" dialog for
/// external (OpenAI-compatible) providers.
/// <para>
/// Entry-refresh only (no poller, task #151): the state is config + catalog driven, and
/// toggle/model changes trigger an immediate refetch, so the dropdown never goes stale after
/// a user action. All through the EXISTING client surface (no new SDK methods).
/// </para>
/// </summary>
public sealed partial class WizardCloudStepViewModel : ObservableObject, IWizardStep
{
    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    // Single-writer gate (task #151): the entry refresh and the toggle/model-persist commands
    // can overlap on the same state writes — serialize them the way WizardTargetsStepViewModel
    // and WizardBenchmarkStepViewModel do (task #130 addendum bug class). The gate wait is
    // deliberately NON-CANCELLABLE (the endpoint reads inside the gate are what get cancelled).
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    // Set while a refresh/toggle applies REMOTE values to the dropdown/flags: suppresses the
    // selection-change persist (the binding re-seeds SelectedCloudModel after every refresh).
    private bool _applyingRemoteState;
    // Task #159: the resolved logger (the seam defaults to the static Serilog.Log.Logger).
    private readonly Serilog.ILogger _logger;
    private bool _disposed;

    public WizardCloudStepViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Serilog.ILogger? logger = null)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _logger = logger ?? Serilog.Log.Logger;
    }

    /// <summary>The cloud-model dropdown items (fleet-catalog order, deduped).</summary>
    public ObservableCollection<string> CloudModels { get; } = [];

    /// <summary>The router's cloud-overflow switch (task #161: flipped OPTIMISTICALLY by the
    /// toggle click, then confirmed by the wire response — a failed toggle reverts to the
    /// previous value under the single-writer gate).</summary>
    [ObservableProperty]
    private bool _cloudEnabled;

    /// <summary>The last PERSISTED <c>CloudOverflow.DefaultModel</c> (null = unset).</summary>
    [ObservableProperty]
    private string? _cloudDefaultModel;

    /// <summary>The ComboBox selection (the dropdown's source of truth while the user
    /// interacts; re-seeded from <see cref="CloudDefaultModel"/> on every refresh).</summary>
    [ObservableProperty]
    private string? _selectedCloudModel;

    /// <summary><c>config.Cloud.OverflowConfigured</c> — the relay sign-in hint (a fleet node
    /// is signed in to ollama.com).</summary>
    [ObservableProperty]
    private bool _cloudOverflowConfigured;

    /// <summary>Drives the empty-state TextBlock (no cloud models in the fleet catalog —
    /// the expected state while the cloud switch is OFF, since the catalog only carries
    /// cloud syntheses while cloud is enabled).</summary>
    [ObservableProperty]
    private bool _showEmptyModelsNote;

    /// <summary>Drives the "configured default not in catalog" note.</summary>
    [ObservableProperty]
    private bool _showConfiguredNote;

    /// <summary>The "configured default not in catalog" note text (raises PropertyChanged below).</summary>
    public string ConfiguredDefaultNote => $"Configured default model: {CloudDefaultModel}";

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Whether the model dropdown is interactive (models exist and not busy).</summary>
    public bool CanSelectCloudModel => CloudModels.Count > 0 && !IsBusy;

    partial void OnCloudDefaultModelChanged(string? value)
    {
        OnPropertyChanged(nameof(ConfiguredDefaultNote));
    }

    partial void OnShowConfiguredNoteChanged(bool value)
    {
        OnPropertyChanged(nameof(ConfiguredDefaultNote));
    }

    partial void OnShowEmptyModelsNoteChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectCloudModel));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectCloudModel));
        ToggleCloudCommand.NotifyCanExecuteChanged();
        AddTargetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// A fleet-catalog model name is a CLOUD model iff it contains <c>:</c> and the part
    /// after the LAST <c>:</c> ends with <c>cloud</c> (OrdinalIgnoreCase) — the
    /// <c>CloudModel</c> config convention (<c>glm-5.3-flash:cloud</c>,
    /// <c>deepseek-v3.1:671b-cloud</c>), which excludes ordinary tags (<c>llama3:8b</c>,
    /// <c>qwen2.5:7b-instruct</c>).
    /// </summary>
    internal static bool IsCloudModelName(string name)
    {
        var index = name.LastIndexOf(':');
        return index >= 0
            && name.Length - index > 1
            && name[(index + 1)..].EndsWith("cloud", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The on-entry refresh (task #151 — no poller: this step is config + catalog driven):
    /// reflects the ACTUAL current cloud configuration (enabled + DefaultModel) and re-seeds
    /// the dropdown from the fleet catalog's cloud models. The router may be DOWN (the
    /// canonical first-run state before Step 1 has started it): failures surface as an
    /// <see cref="Error"/> instead of throwing, because this refresh is awaited by the root
    /// navigation command (<c>OnStepEnteredAsync</c>) — an escaping exception would fault the
    /// async-void command bridge and crash the app. A failure leaves <see cref="CloudModels"/>
    /// with whatever already completed (no partial clobber).
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var client = _clientAccessor();

            var config = await client.GetConfigAsync(ct).ConfigureAwait(false);
            _applyingRemoteState = true;
            try
            {
                CloudEnabled = config.Cloud.Enabled;
                CloudDefaultModel = config.Cloud.DefaultModel;
                CloudOverflowConfigured = config.Cloud.OverflowConfigured;
            }
            finally
            {
                _applyingRemoteState = false;
            }

            var models = await client.GetModelsAsync(ct).ConfigureAwait(false);
            var cloudModels = new List<string>();
            foreach (var model in models.Models)
            {
                if (IsCloudModelName(model.Name) && !cloudModels.Contains(model.Name))
                {
                    cloudModels.Add(model.Name);
                }
            }

            CloudModels.Clear();
            foreach (var name in cloudModels)
            {
                CloudModels.Add(name);
            }

            _applyingRemoteState = true;
            try
            {
                ShowEmptyModelsNote = cloudModels.Count == 0;
                SelectedCloudModel = CloudDefaultModel is { } defaultModel && cloudModels.Contains(defaultModel)
                    ? defaultModel
                    : null;
            }
            finally
            {
                _applyingRemoteState = false;
            }

            ShowConfiguredNote = CloudDefaultModel is { } configured && !cloudModels.Contains(configured);
            Error = null;
            // Task #159: entry-only refresh (no poller) — plain Debug on success.
            _logger.Debug("Wizard step 3: refresh ok ({Count} cloud model(s))", cloudModels.Count);
        }
        catch (OllamaRouterApiException ex)
        {
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
            _logger.Warning("Wizard step 3: refresh failed: {Error}", Error);
        }
        catch (System.Net.Http.HttpRequestException)
        {
            // The router is DOWN (the canonical first-run state before Step 1 has started it).
            Error = "Connection failed — start the router in Step 1 first.";
            _logger.Warning("Wizard step 3: refresh failed: {Error}", Error);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Flips the router's cloud-overflow switch OPTIMISTICALLY (the interactive CheckBox's
    /// click flips <see cref="CloudEnabled"/> before the wire call), POSTs the new value,
    /// reflects the response (the wire response is the source of truth for the confirmed
    /// state) and re-fetches the config + catalog: the fleet catalog GAINS/LOSES the cloud
    /// syntheses with the switch (they are appended only while cloud is enabled), so the
    /// dropdown must re-populate/empty accordingly. On failure the optimistic flip is
    /// CONDITIONALLY reverted (only while the state still equals the optimistic value — a
    /// concurrent refresh's fresh remote value wins) and the error is surfaced.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ToggleCloud()
    {
        var previous = CloudEnabled;
        var next = !previous;
        IsBusy = true;
        try
        {
            _logger.Information("Wizard step 3: cloud overflow → {Enabled}", next ? "on" : "off");
            CloudEnabled = next; // OPTIMISTIC flip (UI thread, BEFORE the wire call)
            var response = await _clientAccessor().UpdateConfigAsync(
                    new UpdateRouterConfigRequest(Cloud: new UpdateCloudConfigRequest(Enabled: next)))
                .ConfigureAwait(false);

            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                CloudEnabled = response.Config?.Cloud.Enabled ?? next;
                CloudDefaultModel = response.Config?.Cloud.DefaultModel ?? CloudDefaultModel;
                Error = null;
                Message = $"Cloud overflow (ollama.com) {(CloudEnabled ? "enabled" : "disabled")}.";
                _logger.Information("Wizard step 3: cloud overflow → {Enabled}", CloudEnabled ? "on" : "off");
            }
            finally
            {
                _refreshGate.Release();
            }

            // The catalog gains/loses the cloud syntheses with the switch: refetch so the
            // dropdown re-populates/empties immediately.
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
            _logger.Warning("Wizard step 3: cloud toggle failed: {Error}", Error);
            await RevertCloudFlipAsync(previous, next).ConfigureAwait(false);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Error = $"Connection failed: {ex.Message}";
            _logger.Warning("Wizard step 3: cloud toggle failed: {Error}", Error);
            await RevertCloudFlipAsync(previous, next).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The single-writer-gated CONDITIONAL revert of a failed optimistic cloud toggle
    /// (task #130 gate invariant): restores <paramref name="previous"/> ONLY while the state
    /// still equals the optimistic <paramref name="next"/> — if a concurrent refresh reflected
    /// a fresh remote value in the meantime, that fresh value wins. The assignment takes the
    /// same <see cref="_refreshGate"/> as the success-path reflection and is always
    /// <c>await</c>ed (never a blocking <c>Wait()</c> on the UI thread).
    /// </summary>
    private async Task RevertCloudFlipAsync(bool previous, bool next)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (CloudEnabled == next)
            {
                CloudEnabled = previous;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// The cloud-model selection-change persist (task #151). The Avalonia 12 ComboBox has no
    /// Command property, so <see cref="Views.Wizard.WizardCloudStepView"/> drives this command
    /// from the control's SelectionChanged event. No-op while the VM applies remote state (a
    /// refresh re-seeding the dropdown), on a null selection, or when the selection already
    /// equals the persisted <see cref="CloudDefaultModel"/>; otherwise PUTs the cloud
    /// <c>defaultModel</c> (value = set; clearing is not a wizard feature) and reflects the
    /// result — a failed PUT reverts the dropdown to the previous <see cref="CloudDefaultModel"/>
    /// so the UI never desyncs from the config.
    /// </summary>
    [RelayCommand]
    internal async Task SelectCloudModel(string? model)
    {
        if (_applyingRemoteState || model is null || model == CloudDefaultModel)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _logger.Information("Wizard step 3: cloud default model → '{Model}'", model);
            await _clientAccessor().UpdateConfigAsync(
                    new UpdateRouterConfigRequest(
                        Cloud: new UpdateCloudConfigRequest(Enabled: CloudEnabled, DefaultModel: model)))
                .ConfigureAwait(false);

            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                CloudDefaultModel = model;
                ShowConfiguredNote = false;
                Error = null;
                Message = $"Cloud default model set to '{model}'.";
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        catch (OllamaRouterApiException ex)
        {
            Error = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
            _logger.Warning("Wizard step 3: persisting cloud default model '{Model}' failed: {Error}", model, Error);
            RevertSelection();
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Error = $"Connection failed: {ex.Message}";
            _logger.Warning("Wizard step 3: persisting cloud default model '{Model}' failed: {Error}", model, Error);
            RevertSelection();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reverts the dropdown to the last PERSISTED default (under the remote-state
    /// guard so the revert itself does not re-trigger a persist).</summary>
    private void RevertSelection()
    {
        _applyingRemoteState = true;
        try
        {
            SelectedCloudModel = CloudDefaultModel;
        }
        finally
        {
            _applyingRemoteState = false;
        }
    }

    /// <summary>
    /// Adds a provider target through the main app's dedicated dialog (task #151 — the
    /// <see cref="AddTargetRequest"/> arrives from <see cref="Views.Wizard.WizardDialogLauncher"/>;
    /// the same dialogs/forms as the Fleet tab), then re-fetches the config + catalog (the
    /// catalog can grow with the new provider's models). The Step 2 fleet table itself
    /// updates on its next step entry / poll tick.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task AddTarget(AddTargetRequest request)
    {
        IsBusy = true;
        try
        {
            _logger.Information("Wizard step 3: adding provider '{ProviderName}'", request.Name);
            await _clientAccessor().AddTargetAsync(request).ConfigureAwait(false);
            _logger.Information("Wizard step 3: provider '{ProviderName}' added", request.Name);
            Error = null;
            Message = $"Provider '{request.Name}' added — it appears in the Step 2 fleet table.";
        }
        catch (OllamaRouterApiException ex)
        {
            Error = WizardTargetErrors.MapAddTarget(ex);
            _logger.Warning("Wizard step 3: adding provider '{ProviderName}' failed: {Error}", request.Name, Error);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Error = WizardTargetErrors.MapAddTarget(ex);
            _logger.Warning("Wizard step 3: adding provider '{ProviderName}' failed: {Error}", request.Name, Error);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool CanAct() => !IsBusy;

    /// <summary>Step lifecycle: refresh on entry (config + catalog; no poller).</summary>
    public Task OnStepEnteredAsync(CancellationToken ct = default) => RefreshAsync(ct);

    /// <summary>Step lifecycle: nothing to stop (no poller).</summary>
    public Task OnStepLeftAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _refreshGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
