using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.ViewModels;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// One fleet-target row of Step 2 (task #103, spec §3.5): the identity, liveness and enablement
/// of a target as reported by <c>GET /api/router/fleet</c>.
/// </summary>
public sealed class WizardTargetRow
{
    public required string Name { get; init; }
    public string? Url { get; init; }
    public string Type { get; init; } = "ollama";
    public string? Provider { get; init; }
    public string Liveness { get; init; } = "Unknown";
    public string? LivenessMessage { get; init; }
    public long? LatencyMs { get; init; }
    public bool Enabled { get; init; }
}

/// <summary>
/// Step 2 — Fleet Targets (task #103, spec §3.5): the live fleet list (2 s poll), the Local-node
/// add, the remote / OpenAI-compatible-provider add form (+ presets), and the Cloud-overflow
/// toggle — all through the EXISTING client surface (no new SDK methods).
/// </summary>
public sealed partial class WizardTargetsStepViewModel : ObservableObject, IWizardStep
{
    private const string LocalTargetName = "Local";
    private const string LocalTargetUrl = "http://127.0.0.1:11435";

    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private readonly DashboardPoller _poller;
    private bool _disposed;

    public WizardTargetsStepViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<CancellationToken, Task> pollerTick,
        Func<TimeSpan, CancellationToken, Task>? pollerDelay = null)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _poller = new DashboardPoller(pollerTick ?? throw new ArgumentNullException(nameof(pollerTick)),
            TimeSpan.FromSeconds(2), pollerDelay);
    }

    /// <summary>The live fleet rows (seeded/refreshed from <c>GET /api/router/fleet</c>).</summary>
    public ObservableCollection<WizardTargetRow> Rows { get; } = [];

    /// <summary>Whether the built-in Cloud (ollama.com) overflow target is enabled.</summary>
    [ObservableProperty]
    private bool _cloudEnabled;

    [ObservableProperty]
    private string _remoteName = string.Empty;

    [ObservableProperty]
    private string _remoteUrl = string.Empty;

    [ObservableProperty]
    private string _remoteApiKey = string.Empty;

    [ObservableProperty]
    private string? _remoteProvider;

    [ObservableProperty]
    private string _remoteType = "ollama";

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Adds the standard local node <c>Local → http://127.0.0.1:11435</c> (server-probed).</summary>
    [RelayCommand]
    private async Task AddLocal()
    {
        IsBusy = true;
        try
        {
            var request = new AddTargetRequest(LocalTargetName, LocalTargetUrl, TestConnectionFirst: true);
            await _clientAccessor().AddTargetAsync(request).ConfigureAwait(false);
            Message = "Local node added.";
        }
        catch (OllamaRouterApiException ex)
        {
            Message = MapAddTargetError(ex);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Message = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Adds a remote target from the form (name + url + optional key/provider/type).</summary>
    [RelayCommand(CanExecute = nameof(CanAddRemote))]
    private async Task AddRemote()
    {
        IsBusy = true;
        try
        {
            var request = new AddTargetRequest(
                RemoteName.Trim(),
                RemoteUrl.Trim(),
                TestConnectionFirst: true,
                Type: string.Equals(RemoteType, "openai", StringComparison.OrdinalIgnoreCase) ? "openai" : null,
                ApiKey: string.IsNullOrWhiteSpace(RemoteApiKey) ? null : RemoteApiKey.Trim(),
                Provider: string.IsNullOrWhiteSpace(RemoteProvider) ? null : RemoteProvider.Trim());
            await _clientAccessor().AddTargetAsync(request).ConfigureAwait(false);
            Message = $"Target '{RemoteName.Trim()}' added.";
            RemoteName = string.Empty;
            RemoteUrl = string.Empty;
            RemoteApiKey = string.Empty;
            RemoteProvider = null;
            RemoteType = "ollama";
        }
        catch (OllamaRouterApiException ex)
        {
            Message = MapAddTargetError(ex);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Message = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Toggles the built-in Cloud (ollama.com) overflow target; reflects the response.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleCloud))]
    private async Task ToggleCloud()
    {
        IsBusy = true;
        try
        {
            var response = await _clientAccessor().UpdateConfigAsync(
                new UpdateRouterConfigRequest(Cloud: new UpdateCloudConfigRequest(Enabled: !CloudEnabled)))
                .ConfigureAwait(false);
            CloudEnabled = response.Config?.Cloud.Enabled ?? !CloudEnabled;
            Message = $"Cloud overflow (ollama.com) {(CloudEnabled ? "enabled" : "disabled")}.";
        }
        catch (OllamaRouterApiException ex)
        {
            Message = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Message = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>OpenRouter preset: prefills the remote form (openai-compatible).</summary>
    [RelayCommand]
    private void UseOpenRouter() => ApplyPreset("OpenRouter", "https://openrouter.ai/api");

    /// <summary>Groq preset: prefills the remote form (openai-compatible).</summary>
    [RelayCommand]
    private void UseGroq() => ApplyPreset("Groq", "https://api.groq.com/openai");

    /// <summary>OpenAI preset: prefills the remote form (openai-compatible).</summary>
    [RelayCommand]
    private void UseOpenAi() => ApplyPreset("OpenAI", "https://api.openai.com/v1");

    private void ApplyPreset(string provider, string url)
    {
        RemoteName = provider;
        RemoteUrl = url;
        RemoteType = "openai";
        RemoteProvider = provider;
        Message = null;
        AddRemoteCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddRemote() => !IsBusy &&
        !string.IsNullOrWhiteSpace(RemoteName) && !string.IsNullOrWhiteSpace(RemoteUrl);

    private bool CanToggleCloud() => !IsBusy;

    private static string MapAddTargetError(OllamaRouterApiException ex) => ex.Status switch
    {
        HttpStatusCode.Conflict => $"Target already exists.",
        HttpStatusCode.UnprocessableEntity => "unreachable — fix Step 1 and retry",
        HttpStatusCode.BadRequest => ex.Error ?? "Invalid target.",
        _ => $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}",
    };

    /// <summary>
    /// The poller tick / on-demand refresh: re-reads the fleet (live liveness) and the Cloud
    /// overflow flag. The router may be DOWN (the canonical first-run state before Step 1 has
    /// started it): failures surface as a <see cref="Message"/> instead of throwing, because an
    /// exception escaping a command faults the async-void command bridge and crashes the app.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var client = _clientAccessor();

        try
        {
            var fleet = await client.GetFleetAsync(ct).ConfigureAwait(false);
            var rows = new List<WizardTargetRow>(fleet.Targets.Count);
            foreach (var target in fleet.Targets)
            {
                rows.Add(new WizardTargetRow
                {
                    Name = target.Name,
                    Url = target.Url,
                    Type = "ollama",
                    Provider = null,
                    Liveness = target.Liveness,
                    LivenessMessage = null,
                    LatencyMs = target.LatencyMs,
                    Enabled = target.Enabled,
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
        catch (System.Net.Http.HttpRequestException ex)
        {
            Message = $"Connection failed: {ex.Message}";
        }

        try
        {
            var config = await client.GetConfigAsync(ct).ConfigureAwait(false);
            CloudEnabled = config.Cloud.Enabled;
        }
        catch (OllamaRouterApiException)
        {
            // The Cloud flag is a best-effort supplement; fleet rows already refreshed.
        }
        catch (System.Net.Http.HttpRequestException)
        {
            // Same — the daemon is down (the fleet block already surfaced the message).
        }
    }

    /// <summary>Step lifecycle: start the 2 s poller (the first tick runs immediately).</summary>
    public Task OnStepEnteredAsync(CancellationToken ct = default) => _poller.StartAsync();

    /// <summary>Step lifecycle: stop the poller (no orphan work).</summary>
    public Task OnStepLeftAsync() => _poller.StopAsync();

    partial void OnRemoteNameChanged(string value) => AddRemoteCommand.NotifyCanExecuteChanged();

    partial void OnRemoteUrlChanged(string value) => AddRemoteCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        AddRemoteCommand.NotifyCanExecuteChanged();
        ToggleCloudCommand.NotifyCanExecuteChanged();
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
