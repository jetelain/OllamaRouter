using CommunityToolkit.Mvvm.ComponentModel;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// The "Add Ollama Node" form (task #149): the dedicated from-scratch add form for
/// Ollama-protocol targets — Name, URL, Max Parallel, and the optional daemon-side
/// hardware-probe ssh credential. Pure logic: validation + request construction; the
/// <see cref="Views.Dialogs.AddOllamaNodeDialog"/> view handles presentation.
/// <para>
/// The protocol type is PINNED to <c>"ollama"</c> and the openai-only / shared-only fields
/// (<c>apiKey</c>, <c>provider</c>, <c>models</c>, <c>powerWatts</c>, <c>hardwareHint</c>)
/// are explicitly nulled — an apiKey on a non-openai target is a server 400, so it is
/// never sent from this form.
/// </para>
/// </summary>
public sealed partial class OllamaNodeAddFormViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _url = string.Empty;
    private string _maxParallel = "1";
    private string _sshUser = string.Empty;
    private string _sshPort = string.Empty;
    private bool _testConnectionFirst = true;
    private string? _validationErrorMessage;

    /// <summary>The node name (≤ 64 chars, no / \ :, not "Cloud").</summary>
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

    /// <summary>
    /// The daemon-side hardware-probe ssh user (optional; the probe host is the target URL's
    /// host, key-based auth — no password). Wire semantics on submit: blank → null (unset).
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
    /// Wire semantics on submit: blank → null (unset), value → set.
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

    /// <summary>Whether an immediate liveness probe runs before persisting (default true —
    /// probe failure → 422 with a retry hint; the same semantics as the shared form).</summary>
    public bool TestConnectionFirst
    {
        get => _testConnectionFirst;
        set => SetProperty(ref _testConnectionFirst, value);
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
    /// Validates the form and builds the <c>POST /api/router/targets</c> request with
    /// <c>type="ollama"</c> pinned (blank ssh fields → null).
    /// </summary>
    public (AddTargetRequest Request, string? Error) BuildAddRequest()
    {
        ValidationErrorMessage = null;
        if (!TargetFieldValidator.ValidateName(Name, out var error))
        {
            return (null!, error!);
        }

        if (!TargetFieldValidator.ValidateUrl(Url, out error))
        {
            return (null!, error!);
        }

        if (!TargetFieldValidator.ValidateMaxParallel(MaxParallel, out error))
        {
            return (null!, error!);
        }

        if (!TargetFieldValidator.ValidateSshUser(SshUser, out error))
        {
            return (null!, error!);
        }

        if (!TargetFieldValidator.ValidateSshPort(SshPort, out error))
        {
            return (null!, error!);
        }

        return (new AddTargetRequest(
            Name: Name.Trim(),
            Url: TargetFieldValidator.NormalizeUrl(Url),
            MaxParallel: int.Parse(MaxParallel, System.Globalization.CultureInfo.InvariantCulture),
            TestConnectionFirst: TestConnectionFirst,
            Type: "ollama",
            // An Ollama node carries NO api key / provider / roster / power override /
            // hardware hint: an apiKey on a non-openai target is a server 400 — never send it.
            HardwareHint: null,
            ApiKey: null,
            Provider: null,
            Models: null,
            PowerWatts: null,
            SshUser: string.IsNullOrWhiteSpace(SshUser) ? null : SshUser,
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
