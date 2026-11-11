using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Preload;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// The Preload Model dialog view model (task #102, spec §5.3): the optional target pin,
/// keepAliveSeconds and contextIntentTokens inputs, the side-effect-free
/// <c>PreviewPreloadAsync</c> FIRST (candidate table), then <c>PreloadModelAsync</c> with the
/// full candidate evaluation + applied result (selected target, applied keep-alive / numCtx,
/// load acceptance, residency confirmation, duration). 404/503 surface as
/// <see cref="OllamaRouterApiException"/> error text.
/// </summary>
public sealed partial class PreloadDialogViewModel : ObservableObject
{
    private readonly Func<IOllamaRouterApiClient> _clientAccessor;

    private string _targetPin = string.Empty;
    private string _keepAliveSeconds = string.Empty;
    private string _contextIntentTokens = string.Empty;
    private string? _errorMessage;
    private bool _isBusy;

    /// <summary>
    /// Creates the dialog view model. <paramref name="contextIntentTokens"/> defaults to
    /// blank (= the model's recommended context window soft intent on the wire).
    /// </summary>
    public PreloadDialogViewModel(
        string modelName,
        Func<IOllamaRouterApiClient> clientAccessor,
        int? recommendedContextWindow = null)
    {
        ModelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
    }

    /// <summary>The model being preloaded.</summary>
    public string ModelName { get; }

    /// <summary>
    /// The target pin input: blank/"Auto" = auto-select; otherwise the pinned target name
    /// (the pin overrides SELECTION, not the safety gates).
    /// </summary>
    public string TargetPin
    {
        get => _targetPin;
        set => SetProperty(ref _targetPin, value);
    }

    /// <summary>The keepAliveSeconds input (blank = the target's effective class default;
    /// -1 = indefinitely; positive = seconds).</summary>
    public string KeepAliveSeconds
    {
        get => _keepAliveSeconds;
        set => SetProperty(ref _keepAliveSeconds, value);
    }

    /// <summary>The contextIntentTokens input (blank = the model's recommended context
    /// window).</summary>
    public string ContextIntentTokens
    {
        get => _contextIntentTokens;
        set => SetProperty(ref _contextIntentTokens, value);
    }

    /// <summary>The full preload response of the last PREVIEW (candidate table + projected
    /// selection).</summary>
    [ObservableProperty]
    private RouterModelPreloadResponse? _previewResult;

    /// <summary>The preview's projected selection (leaf for the compiled XAML bindings;
    /// null before the first preview).</summary>
    [ObservableProperty]
    private string? _previewSelectedTarget;

    /// <summary>The preview's human-readable reason (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private string? _previewReason;

    /// <summary>The preview's candidate rows (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private IReadOnlyList<RouterModelPreloadCandidate>? _previewCandidates;

    /// <summary>The full preload response of the last PRELOAD (applied result; null until a
    /// preload ran).</summary>
    [ObservableProperty]
    private RouterModelPreloadResponse? _preloadResult;

    /// <summary>The preload's selected target (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private string? _preloadSelectedTarget;

    /// <summary>The applied keep-alive in seconds (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private int? _preloadAppliedKeepAliveSeconds;

    /// <summary>The exact applied <c>num_ctx</c> (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private int? _preloadAppliedNumCtx;

    /// <summary>The load acceptance flag (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private bool? _preloadLoadAccepted;

    /// <summary>The residency confirmation flag (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private bool? _preloadResidencyConfirmed;

    /// <summary>The load-call wall-clock in ms (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private long? _preloadDurationMs;

    /// <summary>The preload's human-readable reason (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private string? _preloadReason;

    /// <summary>The error text (null while healthy).</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Whether a command is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// The side-effect-free preview (wire: <c>GET /api/router/preload/preview</c>): runs the
    /// full candidate evaluation and shows the projected selection WITHOUT loading anything.
    /// </summary>
    [RelayCommand]
    public async Task PreviewAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        if (!TryParseInputs(out var keepAlive, out var intent, out var error))
        {
            ErrorMessage = error;
            return;
        }

        IsBusy = true;
        try
        {
            var response = await _clientAccessor()
                .PreviewPreloadAsync(ModelName, keepAlive, intent, ResolveTargetPin(), ct)
                .ConfigureAwait(false);
            PreviewResult = response;
            PreviewSelectedTarget = response.SelectedTarget;
            PreviewReason = response.Reason;
            PreviewCandidates = response.Candidates;
            ErrorMessage = null;
        }
        catch (OllamaRouterApiException ex)
        {
            ErrorMessage = $"Router error {(int)ex.Status} {ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The actual preload (wire: <c>POST /api/router/models/{*modelName}/preload</c> with the
    /// full request body). The applied result (selected target, applied keep-alive / numCtx,
    /// loadAccepted, residencyConfirmed, preloadDurationMs, reason) is surfaced in
    /// <see cref="PreloadResult"/>.
    /// </summary>
    [RelayCommand]
    public async Task PreloadAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        if (!TryParseInputs(out var keepAlive, out var intent, out var error))
        {
            ErrorMessage = error;
            return;
        }

        IsBusy = true;
        try
        {
            var response = await _clientAccessor()
                .PreloadModelAsync(ModelName, new RouterModelPreloadRequest(
                    keepAlive, intent, ResolveTargetPin()), ct).ConfigureAwait(false);
            PreloadResult = response;
            PreloadSelectedTarget = response.SelectedTarget;
            PreloadAppliedKeepAliveSeconds = response.AppliedKeepAliveSeconds;
            PreloadAppliedNumCtx = response.AppliedNumCtx;
            PreloadLoadAccepted = response.LoadAccepted;
            PreloadResidencyConfirmed = response.ResidencyConfirmed;
            PreloadDurationMs = response.PreloadDurationMs;
            PreloadReason = response.Reason;
            ErrorMessage = null;
        }
        catch (OllamaRouterApiException ex)
        {
            ErrorMessage = $"Router error {(int)ex.Status} {ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string? ResolveTargetPin() =>
        string.IsNullOrWhiteSpace(TargetPin) ||
        string.Equals(TargetPin.Trim(), "Auto", System.StringComparison.OrdinalIgnoreCase)
            ? null
            : TargetPin.Trim();

    private bool TryParseInputs(out int? keepAliveSeconds, out int? contextIntentTokens, out string? error)
    {
        keepAliveSeconds = null;
        contextIntentTokens = null;
        error = null;

        if (!string.IsNullOrWhiteSpace(KeepAliveSeconds))
        {
            if (!int.TryParse(KeepAliveSeconds, out var keepAlive) || keepAlive < -1)
            {
                error = "keepAliveSeconds must be -1 (indefinitely), blank (default) or a positive number of seconds.";
                return false;
            }

            keepAliveSeconds = keepAlive;
        }

        if (!string.IsNullOrWhiteSpace(ContextIntentTokens))
        {
            if (!int.TryParse(ContextIntentTokens, out var intent) || intent <= 0)
            {
                error = "contextIntentTokens must be a positive integer (or blank).";
                return false;
            }

            contextIntentTokens = intent;
        }

        return true;
    }
}
