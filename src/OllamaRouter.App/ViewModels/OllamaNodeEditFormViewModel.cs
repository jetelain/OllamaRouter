using CommunityToolkit.Mvvm.ComponentModel;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// The "Edit Ollama Node" form (task #171): the dedicated EDIT form for Ollama-protocol
/// targets — Name (read-only identity), URL, Max Parallel, Hardware Hint, Power (W) and the
/// daemon-side hardware-probe ssh credential. Pure logic: validation +
/// <c>UpdateTargetRequest</c> construction; the
/// <see cref="Views.Dialogs.EditOllamaNodeDialog"/> view handles presentation.
/// <para>
/// Partial-update wire semantics (task #171 decisions D1/D3 + the task #144 sshUser
/// convention): <c>type</c>/<c>enabled</c> are NEVER sent (the protocol family cannot change
/// from the edit UI; enablement is the row toggle). <c>hardwareHint</c> / <c>sshUser</c>
/// blank → <c>""</c> (CLEAR), value → trimmed. <c>powerWatts</c> / <c>sshPort</c> blank →
/// null (NO CHANGE — neither wire can clear; clearing a set value requires editing
/// config/config.json), value → set. The roster is NOT offered, so it is preserved (null).
/// No apiKey/provider surface (an apiKey on a non-openai target is a server 400 anyway).
/// </para>
/// </summary>
public sealed partial class OllamaNodeEditFormViewModel : ObservableObject
{
    private string _url = string.Empty;
    private string _maxParallel = "1";
    private string _hardwareHint = string.Empty;
    private string _powerWatts = string.Empty;
    private string _sshUser = string.Empty;
    private string _sshPort = string.Empty;
    private string? _validationErrorMessage;

    /// <summary>
    /// Creates the form pre-filled from the row. <c>ConfigMaxParallel</c> is the PERSISTED
    /// budget (NOT the effective fleet value), and the ssh / power fields keep the
    /// null = unset distinction as blanks.
    /// </summary>
    public OllamaNodeEditFormViewModel(FleetTargetRow existing)
    {
        if (existing is null)
        {
            throw new ArgumentNullException(nameof(existing));
        }

        _url = existing.Url ?? string.Empty;
        _maxParallel = existing.ConfigMaxParallel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _hardwareHint = existing.HardwareHint ?? string.Empty;
        _powerWatts = existing.PowerWatts?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        _sshUser = existing.SshUser ?? string.Empty;
        _sshPort = existing.SshPort?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        Name = existing.Name;
    }

    /// <summary>The target name — READ-ONLY identity (the
    /// <c>PUT /api/router/targets/{name}</c> path parameter; the submit uses the row's name,
    /// never a user-edited value).</summary>
    public string Name { get; }

    /// <summary>The node base URL (absolute http(s)).</summary>
    public string Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value))
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

    /// <summary>The free-form hardware hint. Wire semantics on submit: blank → <c>""</c>
    /// (CLEAR — task #171 decision D1, the task #144 sshUser edit convention), value →
    /// trimmed.</summary>
    public string HardwareHint
    {
        get => _hardwareHint;
        set
        {
            if (SetProperty(ref _hardwareHint, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>The node power-draw override in watts. Wire semantics on submit: blank →
    /// null (NO CHANGE — the wire cannot clear; clearing a set override requires editing
    /// config/config.json), value → the parsed value.</summary>
    public string PowerWatts
    {
        get => _powerWatts;
        set
        {
            if (SetProperty(ref _powerWatts, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>
    /// The daemon-side hardware-probe ssh user (optional; the probe host is the target URL's
    /// host, key-based auth — no password). Wire semantics on submit: blank → <c>""</c>
    /// (CLEAR, the task #144 edit convention), value → trimmed.
    /// </summary>
    public string SshUser
    {
        get => _sshUser;
        set
        {
            if (SetProperty(ref _sshUser, value))
            {
                ClearError();
            }
        }
    }

    /// <summary>
    /// The daemon-side hardware-probe ssh port (optional; blank = the default port 22).
    /// Wire semantics on submit: blank → null (NO CHANGE — the port wire cannot clear;
    /// clearing a set port requires editing config/config.json), value → set.
    /// </summary>
    public string SshPort
    {
        get => _sshPort;
        set
        {
            if (SetProperty(ref _sshPort, value))
            {
                ClearError();
            }
        }
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
    /// Validates the form and builds the <c>PUT /api/router/targets/{name}</c> request with
    /// the partial-update wire semantics (type/enabled never sent; blank clearable strings
    /// sent as <c>""</c>; blank powerWatts/sshPort sent as null = no change; roster
    /// preserved as null).
    /// </summary>
    public (UpdateTargetRequest Request, string? Error) BuildUpdateRequest()
    {
        ValidationErrorMessage = null;
        if (!TargetFieldValidator.ValidateUrl(Url, out var error))
        {
            return (null!, error!);
        }

        if (!TargetFieldValidator.ValidateMaxParallel(MaxParallel, out error))
        {
            return (null!, error!);
        }

        if (!TargetFieldValidator.ValidatePowerWatts(PowerWatts, out error))
        {
            return (null!, error!);
        }

        if (!TargetFieldValidator.ValidateSshUser(SshUser.Trim(), out error))
        {
            return (null!, error!);
        }

        if (!TargetFieldValidator.ValidateSshPort(SshPort, out error))
        {
            return (null!, error!);
        }

        return (new UpdateTargetRequest(
            Url: TargetFieldValidator.NormalizeUrl(Url),
            // Enablement is the row toggle, not this dialog — never sent.
            Enabled: null,
            // D1 (task #171): blank = "" (CLEAR the stored hint), value = trimmed.
            HardwareHint: string.IsNullOrWhiteSpace(HardwareHint)
                ? string.Empty
                : HardwareHint.Trim(),
            MaxParallel: int.Parse(MaxParallel, System.Globalization.CultureInfo.InvariantCulture),
            // D3 (task #171): the edit form NEVER changes the protocol family.
            Type: null,
            // No key surface on this form (an apiKey on a non-openai target is a 400 anyway).
            ApiKey: null,
            Provider: null,
            // No roster surface on this form — the row's roster is PRESERVED (no change).
            Models: null,
            // Blank = null = NO CHANGE (the wire cannot clear a set override).
            PowerWatts: string.IsNullOrWhiteSpace(PowerWatts)
                ? null
                : double.Parse(PowerWatts, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture),
            // The task #144 edit convention: blank = "" (CLEAR), value = trimmed.
            SshUser: string.IsNullOrWhiteSpace(SshUser) ? string.Empty : SshUser.Trim(),
            // Blank = null = NO CHANGE (the port wire cannot clear).
            SshPort: TargetFieldValidator.ParseSshPort(SshPort)), null);
    }

    private void ClearError()
    {
        if (ValidationErrorMessage is not null)
        {
            ValidationErrorMessage = null;
        }
    }
}
