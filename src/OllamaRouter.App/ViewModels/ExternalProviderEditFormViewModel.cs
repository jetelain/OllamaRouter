using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// The "Edit External Provider" form (task #171): the dedicated EDIT form for
/// OpenAI-compatible endpoints — Name (read-only identity), Base URL (full URL OR bare
/// <c>host[:port]</c>), the informational Provider label, Max Parallel, the masked upstream
/// API key (3-way wire semantics) and the declared-models roster. Pure logic: validation +
/// <c>UpdateTargetRequest</c> construction; the
/// <see cref="Views.Dialogs.EditExternalProviderDialog"/> view handles presentation.
/// <para>
/// Partial-update wire semantics (task #171 decisions D1/D2/D3 + the edit-mode API-key
/// contract): <c>type</c> / <c>enabled</c> are NEVER sent (the protocol family cannot
/// change from the edit UI; enablement is the row toggle). <c>apiKey</c> — untouched →
/// null (KEEP the stored key), explicitly emptied → <c>""</c> (CLEAR), value → the trimmed
/// value (REPLACE). <c>provider</c> blank → <c>""</c> (CLEAR — D1), value → trimmed.
/// Roster: non-empty → the list (REPLACE, blanks → null per entry); empty → <c>[]</c>
/// (CLEAR) only when the row HAD a roster at open (D2), else null (NO CHANGE — no
/// config.json diff on an untouched save). No SSH / power / hardware-hint surface (those
/// are Ollama-node-only).
/// </para>
/// </summary>
public sealed partial class ExternalProviderEditFormViewModel : ObservableObject
{
    private string _baseUrl = string.Empty;
    private string _maxParallel = "1";
    private string _provider = string.Empty;
    private string _apiKey = string.Empty;
    private bool _apiKeyTouched;
    private string? _validationErrorMessage;

    /// <summary>
    /// Creates the form pre-filled from the row. The roster is captured entry-for-entry
    /// (blanks → empty strings) and <see cref="ExistingApiKeyConfigured"/> is read from the
    /// row here (the view no longer assigns it); whether the row HAD a roster at open is
    /// captured for the remove-all wire decision (task #171 decision D2).
    /// </summary>
    public ExternalProviderEditFormViewModel(FleetTargetRow existing)
    {
        if (existing is null)
        {
            throw new ArgumentNullException(nameof(existing));
        }

        _baseUrl = existing.Url ?? string.Empty;
        _maxParallel = existing.ConfigMaxParallel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _provider = existing.Provider ?? string.Empty;
        ExistingApiKeyConfigured = existing.ApiKeyConfigured;
        _hadRosterAtOpen = (existing.Models?.Count ?? 0) > 0;
        foreach (var model in existing.Models ?? (IReadOnlyList<TargetModelDto>)Array.Empty<TargetModelDto>())
        {
            Models.Add(new ModelRosterEntry
            {
                Name = model.Name,
                ContextLength = model.ContextLength?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty,
                Quantization = model.Quantization ?? string.Empty,
            });
        }

        Name = existing.Name;
    }

    /// <summary>Whether the row had a declared-models roster at open (task #171 decision D2:
    /// an emptied roster sends <c>[]</c> = CLEAR only for rows that had one).</summary>
    private readonly bool _hadRosterAtOpen;

    /// <summary>The target name — READ-ONLY identity (the
    /// <c>PUT /api/router/targets/{name}</c> path parameter; the submit uses the row's name,
    /// never a user-edited value).</summary>
    public string Name { get; }

    /// <summary>The base URL: a full http(s) URL OR a bare <c>host[:port]</c> (normalized to
    /// <c>http://host[:port]</c> on submit, the provider ADD form's rule).</summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The maxParallel budget (must parse to an int ≥ 1).</summary>
    public string MaxParallel
    {
        get => _maxParallel;
        set
        {
            if (SetProperty(ref _maxParallel, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The informational provider label. Wire semantics on submit: blank → <c>""</c>
    /// (CLEAR — task #171 decision D1), value → trimmed.</summary>
    public string Provider
    {
        get => _provider;
        set
        {
            if (SetProperty(ref _provider, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The upstream (masked) target API key input. Wire semantics on submit:
    /// untouched → null (NO CHANGE — the stored key is kept), explicitly emptied → <c>""</c>
    /// (CLEAR), value → the trimmed value (REPLACE).</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                _apiKeyTouched = true;
                ClearError();
            }
        }
    }

    /// <summary>Whether the user interacted with the API key box (untouched = no change,
    /// emptied = clear).</summary>
    public bool IsApiKeyTouched => _apiKeyTouched;

    /// <summary>Whether the upstream API key is already configured (read from the row in the
    /// constructor; key material is never shown).</summary>
    public bool ExistingApiKeyConfigured { get; }

    /// <summary>The static API-key status line below the masked box (replaces the retired
    /// dynamic watermark): explains the blank = keep vs new value = replace semantics for
    /// the row's configured state.</summary>
    public string ApiKeyStatusText =>
        ExistingApiKeyConfigured
            ? "A key is stored — leave blank to keep it, or enter a new key to replace it."
            : "No key stored — enter one to set it (blank leaves it unset).";

    /// <summary>The declared models roster editor rows.</summary>
    public ObservableCollection<ModelRosterEntry> Models { get; } = [];

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

    /// <summary>Adds an empty roster row.</summary>
    [RelayCommand]
    public void AddModelRow() => Models.Add(new ModelRosterEntry());

    /// <summary>Removes a roster row (task #171 decision D2: removing ALL rows and saving
    /// sends <c>[]</c> = CLEAR when the row had a roster at open, else null = no change).</summary>
    [RelayCommand]
    public void RemoveModelRow(ModelRosterEntry? entry)
    {
        if (entry is not null)
        {
            Models.Remove(entry);
        }
    }

    /// <summary>
    /// Validates the form and builds the <c>PUT /api/router/targets/{name}</c> request with
    /// the partial-update wire semantics (type/enabled never sent; 3-way apiKey semantics;
    /// blank provider = <c>""</c> clear; roster replace / conditional clear / no change).
    /// </summary>
    public (UpdateTargetRequest Request, string? Error) BuildUpdateRequest()
    {
        ValidationErrorMessage = null;
        var url = NormalizeBaseUrl();
        if (!TargetFieldValidator.ValidateUrl(url, out var error))
        {
            return (null!, error!);
        }

        if (!TargetFieldValidator.ValidateMaxParallel(MaxParallel, out error))
        {
            return (null!, error!);
        }

        var updateModels = BuildModels(); // null = empty roster OR roster validation error
        if (updateModels is null && Models.Count > 0)
        {
            return (null!, ValidationErrorMessage);
        }

        return (new UpdateTargetRequest(
            Url: url,
            // Enablement is the row toggle, not this dialog — never sent.
            Enabled: null,
            HardwareHint: null, // field not offered → NO CHANGE.
            MaxParallel: int.Parse(MaxParallel, System.Globalization.CultureInfo.InvariantCulture),
            // D3 (task #171): the edit form NEVER changes the protocol family.
            Type: null,
            // 3-way key semantics: untouched = null (KEEP), emptied = "" (CLEAR),
            // value = trimmed (REPLACE).
            ApiKey: ApiKeyWireValue(),
            // D1 (task #171): blank = "" (CLEAR the stored label), value = trimmed.
            Provider: string.IsNullOrWhiteSpace(Provider) ? string.Empty : Provider.Trim(),
            // Non-empty = replace (validated above); empty = [] (CLEAR) only when the row
            // HAD a roster at open (D2), else null (NO CHANGE — no config.json diff on an
            // untouched save).
            Models: updateModels ?? (_hadRosterAtOpen ? Array.Empty<TargetModelDto>() : null),
            PowerWatts: null, // field not offered → NO CHANGE.
            SshUser: null, // field not offered → NO CHANGE.
            SshPort: null), null); // field not offered → NO CHANGE.
    }

    /// <summary>
    /// The base URL (the provider ADD form's rule): trims; bare <c>host[:port]</c> input
    /// (no <c>://</c>) is normalized to <c>http://{value}</c>; the trailing <c>/</c> is
    /// stripped. (Blank / garbage inputs then fail
    /// <see cref="TargetFieldValidator.ValidateUrl"/>.)
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

    /// <summary>The API key wire value: null (no change — keep the stored key) when the box
    /// is untouched, <c>""</c> (clear) when explicitly emptied, the trimmed value
    /// (replace) otherwise.</summary>
    private string? ApiKeyWireValue()
    {
        if (!_apiKeyTouched)
        {
            return null; // No change.
        }

        var value = _apiKey.Trim();
        return value.Length == 0 ? string.Empty : value; // "" = clear, else set.
    }

    /// <summary>
    /// Builds the roster wire list (null on a validation error or an empty roster): each
    /// entry's name is trimmed (required), contextLength is blank → null / a positive int,
    /// quantization blank → null (the shared-form contract).
    /// </summary>
    private IReadOnlyList<TargetModelDto>? BuildModels()
    {
        if (Models.Count == 0)
        {
            return null;
        }

        var entries = new List<TargetModelDto>(Models.Count);
        foreach (var entry in Models)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                ValidationErrorMessage = "Every roster model needs a name.";
                return null!;
            }

            int? contextLength = null;
            if (!string.IsNullOrWhiteSpace(entry.ContextLength))
            {
                if (!int.TryParse(entry.ContextLength, out var parsedContext) || parsedContext <= 0)
                {
                    ValidationErrorMessage = $"Model \"{entry.Name.Trim()}\": contextLength must be a positive integer (or blank).";
                    return null!;
                }

                contextLength = parsedContext;
            }

            entries.Add(new TargetModelDto(
                entry.Name.Trim(),
                contextLength,
                string.IsNullOrWhiteSpace(entry.Quantization) ? null : entry.Quantization.Trim()));
        }

        return entries;
    }

    private void ClearError()
    {
        if (ValidationErrorMessage is not null)
        {
            ValidationErrorMessage = null;
        }
    }
}
