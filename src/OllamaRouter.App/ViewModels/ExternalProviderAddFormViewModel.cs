using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// One quick-prefill preset of the "Add External Provider" dialog (task #149): the base
/// URL + provider label of a common OpenAI-compatible endpoint (the URLs are the
/// wizard-step-2 precedent).
/// </summary>
public sealed record ProviderPreset(string Id, string Label, string BaseUrl)
{
    /// <summary>
    /// The preset table (chip-row order; "custom"/blank is NOT a preset — it is the absence
    /// of <see cref="ExternalProviderAddFormViewModel.ActivePresetId"/>).
    /// </summary>
    public static readonly IReadOnlyList<ProviderPreset> All =
    [
        new("openrouter", "OpenRouter", "https://openrouter.ai/api"),
        new("groq", "Groq", "https://api.groq.com/openai"),
        new("openai", "OpenAI", "https://api.openai.com/v1"),
    ];

    /// <summary>The preset of <paramref name="id"/> (null when blank or unknown).</summary>
    public static ProviderPreset? Find(string? id) =>
        string.IsNullOrEmpty(id)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The "Add External Provider" form (task #149): the dedicated from-scratch add form for
/// OpenAI-compatible endpoints — Name, Base URL (full URL OR bare <c>host[:port]</c>),
/// masked API key and the informational provider label, with quick prefill presets
/// (OpenRouter / Groq / OpenAI / Custom). Pure logic: validation + request construction +
/// preset prefill; the <see cref="Views.Dialogs.AddExternalProviderDialog"/> view handles
/// presentation.
/// <para>
/// The protocol type is PINNED to <c>"openai"</c>. The key is OPTIONAL on the wire
/// (blank → null; routing behavior requires it — the box watermark stays
/// "required for OpenAI-compatible providers"). No SSH fields (daemon-side probing is
/// Ollama-protocol-only per <see cref="FleetTargetRow.IsSshProbeable"/>) and no MaxParallel
/// (wire default 1, editable later via Edit).
/// </para>
/// </summary>
public sealed partial class ExternalProviderAddFormViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _baseUrl = string.Empty;
    private string _apiKey = string.Empty;
    private string _provider = string.Empty;
    private bool _testConnectionFirst = true;
    private string? _activePresetId;
    private string? _validationErrorMessage;

    /// <summary>The provider name (≤ 64 chars, no / \ :, not "Cloud").</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The base URL: a full http(s) URL OR a bare <c>host[:port]</c> (normalized
    /// to <c>http://host[:port]</c> on submit).</summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value))
            {
                ClearError();
                DropStalePresetHighlight();
            }
        }
    }

    /// <summary>The upstream API key (masked in the view; optional on the wire — blank →
    /// null, routing behavior requires it).</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The informational provider label.</summary>
    public string Provider
    {
        get => _provider;
        set
        {
            if (SetProperty(ref _provider, value))
            {
                ClearError();
                DropStalePresetHighlight();
            }
        }
    }

    /// <summary>Whether an immediate liveness probe runs before persisting (default true —
    /// probe failure → 422 with a retry hint; the same semantics as the shared form).</summary>
    public bool TestConnectionFirst
    {
        get => _testConnectionFirst;
        set => SetProperty(ref _testConnectionFirst, value);
    }

    /// <summary>The id of the applied preset (null = custom/none) — drives the chip-row
    /// highlighting in the dialog.</summary>
    public string? ActivePresetId
    {
        get => _activePresetId;
        set => SetProperty(ref _activePresetId, value);
    }

    /// <summary>Whether the form currently has a client-side validation error.</summary>
    public bool HasValidationError => ValidationErrorMessage is not null;

    /// <summary>The client-side validation error (null when the form is valid).</summary>
    public string? ValidationErrorMessage
    {
        get => _validationErrorMessage;
        private set
        {
            if (SetProperty(ref _validationErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    /// <summary>Reports a submit-time validation error on the form (re-shows the message
    /// banner; the dialog stays open).</summary>
    public void ReportError(string? error) => ValidationErrorMessage = error;

    /// <summary>
    /// Applies a preset by id (the "Custom" chip passes blank/null): prefill Base URL +
    /// provider label and suggest the provider name — the suggestion tracks the preset ONLY
    /// while the name is blank or still shows the previously applied preset's label (a
    /// user-typed name is NEVER overwritten). Blank/null id = custom/blank: clears Base URL
    /// + provider, drops the highlight, leaves Name/ApiKey untouched. Prefilled values stay
    /// fully editable; editing Base URL/Provider away from the active preset's pair drops
    /// the highlight (<see cref="ActivePresetId"/> → null). Applying a preset also clears
    /// any validation error.
    /// </summary>
    [RelayCommand]
    public void ApplyPreset(string? presetId)
    {
        var preset = ProviderPreset.Find(presetId);
        var previousPreset = ProviderPreset.Find(ActivePresetId);

        if (preset is not null)
        {
            var nameSuggested = string.IsNullOrWhiteSpace(Name) ||
                (previousPreset is not null &&
                 string.Equals(Name, previousPreset.Label, StringComparison.Ordinal));

            BaseUrl = preset.BaseUrl;
            Provider = preset.Label;
            ActivePresetId = preset.Id;
            if (nameSuggested)
            {
                Name = preset.Label;
            }
        }
        else
        {
            // Custom/blank: clear the prefills (Name / ApiKey stay untouched).
            BaseUrl = string.Empty;
            Provider = string.Empty;
            ActivePresetId = null;
        }

        ValidationErrorMessage = null;
    }

    /// <summary>
    /// Validates the form and builds the <c>POST /api/router/targets</c> request with
    /// <c>type="openai"</c> pinned (blank key/provider → null; MaxParallel = wire default 1;
    /// bare <c>host[:port]</c> base URLs normalized to <c>http://…</c>).
    /// </summary>
    public (AddTargetRequest Request, string? Error) BuildAddRequest()
    {
        ValidationErrorMessage = null;
        if (!TargetFieldValidator.ValidateName(Name, out var error))
        {
            return (null!, error!);
        }

        var url = NormalizeBaseUrl();
        if (!TargetFieldValidator.ValidateUrl(url, out error))
        {
            return (null!, error!);
        }

        return (new AddTargetRequest(
            Name: Name.Trim(),
            Url: url,
            // The dialog exposes no MaxParallel field — the wire default of 1 applies
            // (editable later via Edit).
            MaxParallel: 1,
            TestConnectionFirst: TestConnectionFirst,
            Type: "openai",
            ApiKey: string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,
            Provider: string.IsNullOrWhiteSpace(Provider) ? null : Provider.Trim(),
            HardwareHint: null,
            Models: null,
            PowerWatts: null,
            SshUser: null,
            SshPort: null), null);
    }

    /// <summary>
    /// Trims the base URL; bare <c>host[:port]</c> input (no <c>://</c>) is normalized to
    /// <c>http://{value}</c>; full URLs pass through; the trailing <c>/</c> is stripped.
    /// (Blank / garbage inputs then fail <see cref="TargetFieldValidator.ValidateUrl"/>.)
    /// </summary>
    private string NormalizeBaseUrl()
    {
        var trimmed = _baseUrl.Trim();
        if (!trimmed.Contains("://"))
        {
            trimmed = $"http://{trimmed}";
        }

        return TargetFieldValidator.NormalizeUrl(trimmed);
    }

    /// <summary>
    /// After a manual Base URL / Provider edit, the active preset's pair no longer matches
    /// the form → the chip highlight drops (null = custom).
    /// </summary>
    private void DropStalePresetHighlight()
    {
        if (ActivePresetId is null)
        {
            return;
        }

        var preset = ProviderPreset.Find(ActivePresetId);
        if (preset is null ||
            !string.Equals(_baseUrl, preset.BaseUrl, StringComparison.Ordinal) ||
            !string.Equals(_provider, preset.Label, StringComparison.Ordinal))
        {
            ActivePresetId = null;
        }
    }

    private void ClearError()
    {
        if (ValidationErrorMessage is not null)
        {
            ValidationErrorMessage = null;
        }
    }
}
