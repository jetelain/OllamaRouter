using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Models;

namespace OllamaRouter.App.ViewModels.InstallModelWizard;

/// <summary>
/// Step 0 — Model of the Install Model wizard (task #125): the mode (Manual /
/// Benchmark-assisted), the free-form <c>name[:tag]</c> model-reference input with the
/// parsed display (name, effective tag = the quantization selector) + quick-pick common
/// quant-tag suggestions, and — in assisted mode — the setup-wizard-style fetch-on-entry
/// assistance (the model's fleet-known state: catalog quant + recommended context, or
/// "not in the fleet yet" for a registry-only model) with a tag prefill when the user left
/// the tag blank. Validation is pure (<see cref="ModelPullNaming"/> — the SAME rules the
/// server endpoint enforces, so the gate can never disagree with the wire).
/// </summary>
public sealed partial class InstallModelStepViewModel : ObservableObject, IWizardStep
{
    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private bool _assisting;

    public InstallModelStepViewModel(Func<IOllamaRouterApiClient> clientAccessor)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        QuickPickTagCommand = new RelayCommand<string>(ApplyQuickPickTag, tag =>
            !string.IsNullOrWhiteSpace(tag) && !Assisted);
    }

    /// <summary>The quick-pick common quant tags (suggestions ONLY — appended to the parsed
    /// name as the reference's tag; user-editable afterwards; disabled in assisted mode
    /// where the tag comes from the catalog).</summary>
    public string[] QuickPickTags { get; } = ["latest", "q4_K_M", "q8_0", "q5_K_M", "q6_K"];

    public IRelayCommand<string> QuickPickTagCommand { get; }

    /// <summary>The wizard mode: false = Manual, true = Benchmark-assisted (setup-wizard-style
    /// assistance prefills the tag + per-target contexts with user confirmation).</summary>
    [ObservableProperty]
    private bool _assisted;

    /// <summary>The free-form model reference input (<c>name[:tag]</c>).</summary>
    [ObservableProperty]
    private string _modelReference = string.Empty;

    /// <summary>Whether the current reference parses (the Next gate).</summary>
    public bool ParseValid { get; private set; }

    /// <summary>The parsed model name (without the tag).</summary>
    public string ParsedName { get; private set; } = string.Empty;

    /// <summary>The effective tag = the quantization selector (defaults to <c>latest</c>).</summary>
    public string? ParsedTag { get; private set; }

    /// <summary>The parse hint (null while the reference is empty or valid).</summary>
    public string? ParseError { get; private set; }

    /// <summary>The assisted mode's fleet-known state line (null in manual mode / before the
    /// first fetch).</summary>
    [ObservableProperty]
    private string? _fleetMessage;

    /// <summary>The assisted mode's fetch error (null while healthy).</summary>
    [ObservableProperty]
    private string? _fleetError;

    /// <summary>The fleet-catalog quantization of the model (assisted mode; display + the
    /// prefill provenance).</summary>
    [ObservableProperty]
    private string? _catalogQuant;

    /// <summary>The assisted mode's tag suggestion provenance (assisted mode only).</summary>
    [ObservableProperty]
    private string? _tagProvenance;

    /// <summary>The Next gate: a valid model reference.</summary>
    public bool IsValid => ParseValid;

    /// <summary>The current model reference (read by the step 1/2 step view models).</summary>
    public string ModelRef => ModelReference;

    partial void OnModelReferenceChanged(string value)
    {
        if (ModelPullNaming.TryParseModelReference(value, out var name, out string? tag))
        {
            ParseValid = true;
            ParseError = null;
            ParsedName = name;
            ParsedTag = tag ?? "latest";
        }
        else
        {
            ParseValid = false;
            ParseError = value.Length == 0 ? null : "enter a valid model reference (name or name:tag, e.g. llama3.2:13b-q4_K_M)";
            ParsedName = string.Empty;
            ParsedTag = null;
        }

        OnPropertyChanged(nameof(ParseValid));
        OnPropertyChanged(nameof(ParsedName));
        OnPropertyChanged(nameof(ParsedTag));
        OnPropertyChanged(nameof(ParseError));
        OnPropertyChanged(nameof(IsValid));

        // Assisted mode: re-run the fleet-known fetch when the reference changes (debounced
        // by the in-flight guard; the user can still type over the prefill).
        if (Assisted)
        {
            _ = RefreshAssistAsync();
        }
    }

    partial void OnAssistedChanged(bool value)
    {
        OnPropertyChanged(nameof(Manual));
        QuickPickTagCommand.NotifyCanExecuteChanged();
        if (!value)
        {
            FleetMessage = null;
            FleetError = null;
            CatalogQuant = null;
            TagProvenance = null;
        }
        else
        {
            _ = RefreshAssistAsync();
        }
    }

    /// <summary>Whether the manual mode is selected (radio binding; the assisted radio binds
    /// <see cref="Assisted"/> directly).</summary>
    public bool Manual => !Assisted;

    private void ApplyQuickPickTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || !ModelPullNaming.TryParseModelReference(ModelReference, out var name, out _))
        {
            return;
        }

        ModelReference = $"{name}:{tag}";
    }

    /// <summary>
    /// The assisted mode's fetch (setup-wizard style, try/catch → hint text): the model's
    /// fleet-known state (<c>GET /api/router/models/{name}</c>: found on N node(s) +
    /// recommended context) + the fleet-catalog quant/tag of the exact model
    /// (<c>GET /api/router/cartography</c>). A 404 → "not in the fleet yet — will be pulled
    /// from the Ollama registry" (NOT an error — a registry-only model is the normal first
    /// install). When the user left the tag blank, the catalog tag is PREFILLED into the
    /// reference (user-editable — the assistance never overrides an explicit tag).
    /// </summary>
    internal async Task RefreshAssistAsync(CancellationToken ct = default)
    {
        if (!Assisted || _assisting || !ModelPullNaming.TryParseModelReference(ModelReference, out var name, out var userTag))
        {
            return;
        }

        _assisting = true;
        try
        {
            var client = _clientAccessor();
            var detail = await client.GetModelDetailAsync(ModelReference, ct).ConfigureAwait(false);
            var catalog = await client.GetCartographyAsync(ct).ConfigureAwait(false);
            var row = catalog.Models.FirstOrDefault(m =>
                string.Equals(m.Name, ModelReference, StringComparison.OrdinalIgnoreCase) ||
                m.Name.StartsWith(ModelReference + ":", StringComparison.OrdinalIgnoreCase));
            var quant = row?.Quantization ?? row?.Deployments.FirstOrDefault(d => d.Quantization is not null)?.Quantization;
            CatalogQuant = quant;

            FleetMessage = detail.Targets.Count == 0
                ? "Known to the fleet catalog (no target hosts it yet) — will be pulled from the Ollama registry."
                : $"Found on {detail.Targets.Count} node(s); recommended context {detail.RecommendedContextWindow.ToString("N0", CultureInfo.InvariantCulture)} tokens."
                    + (quant is null ? string.Empty : $" catalog quant '{quant}'");
            FleetError = null;

            // Tag prefill: ONLY when the user left the tag blank AND the catalog suggests a
            // specific one (the 'latest' default is not an actionable suggestion).
            var suggestion = InstallAssistSuggester.SuggestTag(userTag, row?.Name, name);
            TagProvenance = suggestion.Provenance;
            if (userTag is null && suggestion.Tag is not null &&
                !string.Equals(suggestion.Tag, "latest", StringComparison.OrdinalIgnoreCase))
            {
                ModelReference = $"{name}:{suggestion.Tag}";
            }
        }
        catch (OllamaRouterApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            CatalogQuant = null;
            TagProvenance = "no data — registry default";
            FleetMessage = "Not in the fleet yet — will be pulled from the Ollama registry.";
            FleetError = null;
        }
        catch (OllamaRouterApiException ex)
        {
            FleetMessage = null;
            FleetError = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (Exception ex)
        {
            FleetMessage = null;
            FleetError = ex.Message;
        }
        finally
        {
            _assisting = false;
        }
    }

    /// <inheritdoc />
    public Task OnStepEnteredAsync(CancellationToken ct = default)
    {
        if (Assisted)
        {
            return RefreshAssistAsync(ct);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnStepLeftAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
