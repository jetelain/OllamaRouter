using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Models;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// One target row of the Uninstall Model dialog (task #126): the target where the model is
/// PRESENT (pre-checked by default), the execution outcome (null until the uninstall ran:
/// <see cref="Succeeded"/> and <see cref="ResultText" /> — "deleted" on success, the
/// daemon's fail-soft error text on failure) and the per-row selection checkbox.
/// </summary>
public sealed partial class UninstallTargetRowVm : ObservableObject
{
    public UninstallTargetRowVm(string target)
    {
        Target = target;
    }

    /// <summary>The canonical target name.</summary>
    public string Target { get; }

    /// <summary>Whether this target is part of the request (ALL present targets
    /// pre-selected by default).</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>The execution outcome (null until the uninstall command ran).</summary>
    [ObservableProperty]
    private bool? _succeeded;

    /// <summary>The per-target result text (null until the command ran: "deleted" on
    /// success, the daemon's error text on failure).</summary>
    [ObservableProperty]
    private string? _resultText;
}

/// <summary>
/// The Uninstall Model dialog view model (task #126, report item #7): the list of targets
/// where the model is PRESENT (each with a pre-checked checkbox), the destructive-action
/// confirmation (deliberately VIEW-LEVEL in <c>UninstallDialog.axaml.cs</c> via the shared
/// <c>Confirm</c> dialog — this VM stays host-free and unit-testable) and the ONE
/// <c>UninstallModelAsync</c> call with the checked targets only, mapping the per-target
/// results back onto the rows (200 fail-soft: success vs. the daemon error text per row).
/// 404/400 from the router (unknown model / target / body) surface as
/// <see cref="OllamaRouterApiException"/> error text with no partial per-target list.
/// </summary>
public sealed partial class UninstallDialogViewModel : ObservableObject
{
    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private string? _errorMessage;
    private bool _isBusy;
    private bool _hasResults;

    /// <summary>
    /// Creates the dialog view model for the targets where the model is present.
    /// Null/empty <paramref name="presentTargets"/> is a caller bug (the row's Uninstall
    /// button is hidden when no target is present) → <see cref="ArgumentException"/>.
    /// </summary>
    public UninstallDialogViewModel(
        string modelName, IReadOnlyList<string> presentTargets, Func<IOllamaRouterApiClient> clientAccessor)
    {
        ModelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        if (presentTargets is null)
        {
            throw new ArgumentNullException(nameof(presentTargets));
        }

        if (presentTargets.Count == 0)
        {
            throw new ArgumentException("The model is present on no target.", nameof(presentTargets));
        }

        foreach (var target in presentTargets)
        {
            Targets.Add(new UninstallTargetRowVm(target));
        }
    }

    /// <summary>The model being uninstalled.</summary>
    public string ModelName { get; }

    /// <summary>The target rows (targets where the model is present, pre-checked).</summary>
    public ObservableCollection<UninstallTargetRowVm> Targets { get; } = [];

    /// <summary>The error text (null while healthy).</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Whether the uninstall command is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>True once the command ran (drives the result panel + the Close button swap
    /// in the dialog).</summary>
    public bool HasResults
    {
        get => _hasResults;
        private set => SetProperty(ref _hasResults, value);
    }

    /// <summary>
    /// The uninstall (wire: <c>POST /api/router/models/uninstall</c>): sends the model name
    /// + ONLY the checked target names (unchecked targets are absent from the payload; a
    /// zero-selection is a validation error with NO client call). On the 200 response each
    /// per-target row is mapped by name (case-insensitive) to its success / error text.
    /// The confirmation step (enumerating the exact model + target list) is view-level.
    /// </summary>
    [RelayCommand]
    public async Task UninstallAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        var selected = Targets.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ErrorMessage = "Select at least one target to uninstall.";
            return;
        }

        IsBusy = true;
        try
        {
            var response = await _clientAccessor()
                .UninstallModelAsync(ModelName, selected.Select(t => t.Target).ToList(), ct)
                .ConfigureAwait(false);
            foreach (var row in Targets)
            {
                var result = response.Targets.FirstOrDefault(t =>
                    string.Equals(t.Target, row.Target, StringComparison.OrdinalIgnoreCase));
                row.Succeeded = result is { } r ? r.Success : false;
                row.ResultText = result is null
                    ? "no result returned by the router"
                    : result.Success ? "deleted" : (result.Error ?? "deletion failed");
            }

            HasResults = true;
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
}
