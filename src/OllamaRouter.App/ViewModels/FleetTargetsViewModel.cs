using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// One merged fleet-target row of the Fleet &amp; Cloud Targets tab (task #102, spec §5.2):
/// <c>GET /api/router/fleet</c> (liveness, effective hardware, execution, effective preload
/// policy) + <c>GET /api/router/config</c> targets (editable configuration) +
/// <c>GET /api/router/fleet/hardware</c> (per-node hardware profiles).
/// <para>
/// The EFFECTIVE <see cref="SpeedFactor"/> / <see cref="MaxVramMB"/> /
/// <see cref="EffectiveKeepAliveSeconds"/> (+ <see cref="KeepAliveSource"/>) are READ-ONLY
/// values reported by the fleet endpoint (API-bound scope: no wire API persists them).
/// </para>
/// </summary>
public sealed partial class FleetTargetRow
{
    /// <summary>The canonical target name.</summary>
    public required string Name { get; init; }

    /// <summary>The target base URL.</summary>
    public string? Url { get; init; }

    /// <summary>The protocol family ("ollama" / "openai" / "osnative").</summary>
    public string Type { get; init; } = "ollama";

    /// <summary>The informational provider label (e.g. "OpenRouter", "Groq").</summary>
    public string? Provider { get; init; }

    /// <summary>The PERSISTED administrative enablement (config flag; distinct from the
    /// Monitor tab's runtime toggle).</summary>
    public bool Enabled { get; init; }

    /// <summary>The liveness state ("Online" / "Offline" / "Probing" / "Unknown").</summary>
    public string Liveness { get; init; } = "Unknown";

    /// <summary>The last probe latency in milliseconds (null when unknown).</summary>
    public long? LatencyMs { get; init; }

    /// <summary>The location classification ("local" / "remote" / "cloud").</summary>
    public string Location { get; init; } = "remote";

    // -- Effective hardware (READ-ONLY, from GET /api/router/fleet) -------------

    /// <summary>The effective max VRAM in MB (read-only; no wire API to persist it).</summary>
    public int? MaxVramMB { get; init; }

    /// <summary>The effective speed factor (read-only; no wire API to persist it).</summary>
    public double? SpeedFactor { get; init; }

    /// <summary>The effective parallel budget (read-only).</summary>
    public int MaxParallel { get; init; } = 1;

    /// <summary>Whether the node reports unified memory.</summary>
    public bool IsUnifiedMemory { get; init; }

    /// <summary>The system RAM in MB (when reported).</summary>
    public long? SystemRamMB { get; init; }

    /// <summary>The memory technology label (when reported).</summary>
    public string? MemoryType { get; init; }

    /// <summary>The effective bandwidth in GB/s (when reported).</summary>
    public double? BandwidthGBs { get; init; }

    /// <summary>The GPU name (when reported).</summary>
    public string? GpuName { get; init; }

    /// <summary>The GPU vendor (when reported).</summary>
    public string? GpuVendor { get; init; }

    /// <summary>The EFFECTIVE configured preload keep-alive in seconds (-1 = indefinitely;
    /// null for Cloud; read-only).</summary>
    public int? EffectiveKeepAliveSeconds { get; init; }

    /// <summary>Which rule produced <see cref="EffectiveKeepAliveSeconds"/>
    /// ("explicit" / "class" / "default").</summary>
    public string? KeepAliveSource { get; init; }

    // -- Editable configuration (from GET /api/router/config targets) -----------

    /// <summary>The free-form hardware hint (display-only in v1).</summary>
    public string? HardwareHint { get; init; }

    /// <summary>The persisted maxParallel budget.</summary>
    public int ConfigMaxParallel { get; init; } = 1;

    /// <summary>Whether an upstream API key is configured (bool flag; key material is never
    /// exposed).</summary>
    public bool ApiKeyConfigured { get; init; }

    /// <summary>The node power-draw override in watts (null = unset; blank = no change on
    /// edit — clearing a set override is a documented API limitation).</summary>
    public double? PowerWatts { get; init; }

    /// <summary>The declared models roster (null/empty when none declared).</summary>
    public IReadOnlyList<TargetModelDto>? Models { get; init; }

    // -- Execution (from GET /api/router/fleet) ---------------------------------

    /// <summary>The in-flight request count.</summary>
    public int InFlightRequests { get; init; }

    /// <summary>The first resident (active) model (when loaded).</summary>
    public string? ActiveModel { get; init; }

    /// <summary>The full resident model list (empty when idle).</summary>
    public IReadOnlyList<string> ResidentModels { get; init; } = [];

    // -- Hardware profile inspector (from GET /api/router/fleet/hardware) -------

    /// <summary>The profile provenance ("local" / "peer" / "persisted" / "manual" / "none").</summary>
    public string? HardwareSource { get; init; }

    /// <summary>Whether the node was reachable for the hardware probe.</summary>
    public bool? HardwareReachable { get; init; }

    /// <summary>The full hardware profile (null when unknown).</summary>
    public NodeHardwareProfile? HardwareProfile { get; init; }

    // -- Display leaves (compiled XAML bindings cannot traverse non-observable wire
    //    DTOs, so the nullable sub-paths are flattened here — read-only projections) ----

    /// <summary>The declared models roster count (null when no roster is declared).</summary>
    public int? ModelsCount => Models?.Count;

    /// <summary>The hardware profile OS label (null when the profile is unknown).</summary>
    public string? HardwareOs => HardwareProfile?.Os;

    /// <summary>The hardware profile architecture (null when the profile is unknown).</summary>
    public string? HardwareArchitecture => HardwareProfile?.Architecture;

    /// <summary>The hardware profile system RAM in MB (null when the profile is unknown).</summary>
    public long? HardwareSystemRamMB => HardwareProfile?.SystemRamMB;

    /// <summary>The hardware profile total fast memory in MB (null when the profile is
    /// unknown).</summary>
    public long? HardwareTotalFastMemoryMB => HardwareProfile?.TotalFastMemoryMB;

    /// <summary>The hardware profile effective bandwidth in GB/s (null when the profile is
    /// unknown).</summary>
    public double? HardwareEffectiveBandwidthGBs => HardwareProfile?.EffectiveBandwidthGBs;

    /// <summary>The hardware profile GPU devices (null when the profile is unknown).</summary>
    public IReadOnlyList<GpuDeviceProfile>? Gpus => HardwareProfile?.Gpus;
}

/// <summary>
/// The Fleet &amp; Cloud Targets tab view model (task #102, spec §5.2): the merged
/// fleet + config + hardware view (1 tick = 3 GETs, 5 s poll), the add/edit/delete target
/// operations (with the masked-API-key wire semantics) and the persisted enablement toggle.
/// </summary>
public sealed partial class FleetTargetsViewModel : RouterTabViewModel
{
    /// <summary>The Fleet tab polling interval (spec §4).</summary>
    internal static readonly TimeSpan FleetInterval = TimeSpan.FromSeconds(5);

    public FleetTargetsViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<TimeSpan, CancellationToken, Task>? pollDelay = null)
        : base(clientAccessor, pollDelay)
    {
    }

    /// <inheritdoc />
    protected override TimeSpan PollInterval => FleetInterval;

    /// <summary>The merged fleet target rows (config order; Cloud pseudo-target excluded by
    /// the server).</summary>
    public ObservableCollection<FleetTargetRow> Rows { get; } = [];

    private static async Task RunOnUiAsync(Action action)
    {
        try
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
            }
        }
        catch
        {
            action();
        }
    }

    /// <inheritdoc />
    protected override async Task RefreshCoreAsync(CancellationToken ct)
    {
        var client = ClientAccessor();
        var fleet = await client.GetFleetAsync(ct).ConfigureAwait(false);
        var config = await client.GetConfigAsync(ct).ConfigureAwait(false);
        var hardware = await client.GetFleetHardwareAsync(ct).ConfigureAwait(false);

        var fleetByName = fleet.Targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var configByName = config.Targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var hardwareByName = hardware.Nodes.ToDictionary(n => n.Target, StringComparer.OrdinalIgnoreCase);

        await RunOnUiAsync(() =>
        {
            Rows.Clear();
            // Config order is the canonical row order.
            foreach (var target in config.Targets)
            {
                fleetByName.TryGetValue(target.Name, out var fleetItem);
                hardwareByName.TryGetValue(target.Name, out var node);
                var hw = fleetItem?.Hardware;
                var loc = fleetItem?.Location ?? "remote";
                Views.TargetLocationVisuals.Register(target.Name, loc);
                Rows.Add(new FleetTargetRow
                {
                    Name = target.Name,
                    Url = target.Url,
                    Type = target.Type,
                    Provider = target.Provider,
                    Enabled = target.Enabled,
                    Liveness = fleetItem?.Liveness ?? "Unknown",
                    LatencyMs = fleetItem?.LatencyMs,
                    Location = loc,
                    MaxVramMB = hw?.MaxVramMB,
                    SpeedFactor = hw?.SpeedFactor,
                    MaxParallel = hw?.MaxParallel ?? target.MaxParallel,
                    IsUnifiedMemory = hw?.IsUnifiedMemory ?? false,
                    SystemRamMB = hw?.SystemRamMB,
                    MemoryType = hw?.MemoryType,
                    BandwidthGBs = hw?.BandwidthGBs,
                    GpuName = hw?.GpuName,
                    GpuVendor = hw?.GpuVendor,
                    EffectiveKeepAliveSeconds = fleetItem?.Preload?.KeepAliveSeconds,
                    KeepAliveSource = fleetItem?.Preload?.KeepAliveSource,
                    HardwareHint = target.HardwareHint,
                    ConfigMaxParallel = target.MaxParallel,
                    ApiKeyConfigured = target.ApiKeyConfigured,
                    PowerWatts = target.PowerWatts,
                    Models = target.Models,
                    InFlightRequests = fleetItem?.Execution.InFlightRequests ?? 0,
                    ActiveModel = fleetItem?.Execution.ActiveModel,
                    ResidentModels = fleetItem?.Execution.ResidentModels
                        ?? (IReadOnlyList<string>)Array.Empty<string>(),
                    HardwareSource = node?.Source,
                    HardwareReachable = node?.Reachable,
                    HardwareProfile = node?.Profile,
                });
            }
        });
    }

    /// <summary>
    /// Adds a fleet target (wire: <c>POST /api/router/targets</c>) and refreshes on 201.
    /// Server-side validation failures (400 invalid name/type / 409 duplicate / 422 probe
    /// failed / 400 apiKey on non-openai) surface in
    /// <see cref="RouterTabViewModel.LastError"/> with the parsed error text.
    /// </summary>
    [RelayCommand]
    public async Task AddTargetAsync(AddTargetRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        try
        {
            await ClientAccessor().AddTargetAsync(request, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            SetError(ex.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Updates a fleet target (wire: <c>PUT /api/router/targets/{name}</c>) and refreshes.
    /// The wire semantics of <c>apiKey</c> apply: null/absent = no change, <c>""</c> = clear.
    /// </summary>
    [RelayCommand]
    public async Task EditTargetAsync((string Name, UpdateTargetRequest Request) edit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(edit.Name))
        {
            throw new ArgumentException("The target name must not be empty.", nameof(edit));
        }

        if (edit.Request is null)
        {
            throw new ArgumentNullException(nameof(edit));
        }

        try
        {
            await ClientAccessor().UpdateTargetAsync(edit.Name, edit.Request, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            SetError(ex.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a fleet target (wire: <c>DELETE /api/router/targets/{name}</c>) and refreshes
    /// (the row is gone from the next data source read).
    /// </summary>
    [RelayCommand]
    public async Task DeleteTargetAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The target name must not be empty.", nameof(name));
        }

        try
        {
            await ClientAccessor().DeleteTargetAsync(name, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            SetError(ex.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Toggles the PERSISTED administrative enablement flag (wire:
    /// <c>PUT /api/router/targets/{name}</c> with <c>enabled</c> only) — distinct from the
    /// Monitor tab's RUNTIME toggle (<c>POST /targets</c>).
    /// </summary>
    [RelayCommand]
    public async Task TogglePersistedEnabledAsync(FleetTargetRow row, CancellationToken ct = default)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        try
        {
            await ClientAccessor()
                .UpdateTargetAsync(row.Name, new UpdateTargetRequest(Enabled: !row.Enabled), ct)
                .ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaRouterApiException ex)
        {
            SetError(ex.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Opens the shared target form for ADD mode (the view wires the form to the dialog).
    /// </summary>
    public TargetEditFormViewModel CreateAddForm() => new(isEdit: false);

    /// <summary>
    /// Opens the shared target form for EDIT mode, pre-filled from the row (the API key box
    /// means "no change" when untouched, "clear" when explicitly emptied).
    /// </summary>
    public TargetEditFormViewModel CreateEditForm(FleetTargetRow row) =>
        new(isEdit: true, existing: row ?? throw new ArgumentNullException(nameof(row)));
}

/// <summary>
/// One editable row of the target models roster editor (name / contextLength /
/// quantization; blanks → null on submit).
/// </summary>
public sealed partial class ModelRosterEntry : ObservableObject
{
    /// <summary>The model name (required, non-blank).</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The declared context length in tokens (blank = null; must be &gt; 0).</summary>
    [ObservableProperty]
    private string _contextLength = string.Empty;

    /// <summary>The declared quantization label (blank = null).</summary>
    [ObservableProperty]
    private string _quantization = string.Empty;
}

/// <summary>
/// The shared add/edit target form (task #102, spec §5.2 — one form behind the
/// <c>TargetEditDialog</c> for both modes). Pure logic: validation + request construction;
/// the dialog view handles presentation.
/// </summary>
public sealed partial class TargetEditFormViewModel : ObservableObject
{
    private readonly bool _isEdit;

    /// <summary>The protocol family options of the type dropdown.</summary>
    public static readonly IReadOnlyList<string> TypeOptions = ["ollama", "openai", "osnative"];

    private string _name = string.Empty;
    private string _url = string.Empty;
    private string _type = "ollama";
    private string _provider = string.Empty;
    private string _maxParallel = "1";
    private string _hardwareHint = string.Empty;
    private string _apiKey = string.Empty;
    private string _powerWatts = string.Empty;
    private bool _testConnectionFirst = true;
    private bool _apiKeyTouched;
    private string? _validationErrorMessage;

    /// <summary>
    /// Creates the form. In edit mode the row's values are pre-filled and the API key box
    /// starts "untouched" (= "no change" on submit; the configured state is shown as a badge,
    /// never the key material).
    /// </summary>
    public TargetEditFormViewModel(bool isEdit, FleetTargetRow? existing = null)
    {
        _isEdit = isEdit;
        if (isEdit)
        {
            if (existing is null)
            {
                throw new ArgumentNullException(nameof(existing));
            }

            _name = existing.Name;
            _url = existing.Url ?? string.Empty;
            _type = existing.Type;
            _provider = existing.Provider ?? string.Empty;
            _maxParallel = existing.ConfigMaxParallel.ToString();
            _hardwareHint = existing.HardwareHint ?? string.Empty;
            _powerWatts = existing.PowerWatts?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty;
            ExistingApiKeyConfigured = existing.ApiKeyConfigured;
            foreach (var model in existing.Models ?? (IReadOnlyList<TargetModelDto>)Array.Empty<TargetModelDto>())
            {
                Models.Add(new ModelRosterEntry
                {
                    Name = model.Name,
                    ContextLength = model.ContextLength?.ToString() ?? string.Empty,
                    Quantization = model.Quantization ?? string.Empty,
                });
            }
        }
    }

    /// <summary>True in EDIT mode (a different request type is built on submit).</summary>
    public bool IsEdit => _isEdit;

    /// <summary>The protocol family options.</summary>
    public IReadOnlyList<string> TypeChoices => TypeOptions;

    /// <summary>The target name (≤ 64 chars, no / \ :, not "Cloud").</summary>
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

    /// <summary>The target base URL (absolute http(s)).</summary>
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

    /// <summary>The protocol family ("ollama" / "openai" / "osnative").</summary>
    public string Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
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

    /// <summary>The free-form hardware hint.</summary>
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

    /// <summary>
    /// The upstream (masked) target API key input. Wire semantics on submit: ADD mode — the
    /// value (blank → null); EDIT mode — untouched → null (NO CHANGE), explicitly emptied →
    /// <c>""</c> (CLEAR), filled → the value.
    /// </summary>
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

    /// <summary>Whether the user interacted with the API key box (edit mode: untouched = no
    /// change, emptied = clear).</summary>
    public bool IsApiKeyTouched => _apiKeyTouched;

    /// <summary>The node power-draw override in watts (blank = null = no change).</summary>
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

    /// <summary>Whether an immediate liveness probe runs before persisting (add mode; default
    /// true — probe failure → 422 with a retry hint).</summary>
    public bool TestConnectionFirst
    {
        get => _testConnectionFirst;
        set => SetProperty(ref _testConnectionFirst, value);
    }

    /// <summary>The declared models roster editor rows.</summary>
    public ObservableCollection<ModelRosterEntry> Models { get; } = [];

    /// <summary>Whether the form currently has a client-side validation error.</summary>
    public bool HasValidationError => ValidationErrorMessage is not null;

    /// <summary>The masked API key box watermark (edit vs add hint).</summary>
    public string ApiKeyWatermark =>
        IsEdit
            ? (ExistingApiKeyConfigured
                ? "configured — leave blank = no change, empty = clear"
                : "blank = no change")
            : "required for OpenAI-compatible providers";

    /// <summary>Reports a submit-time validation error on the form (re-shows the message
    /// banner; the dialog stays open).</summary>
    public void ReportError(string? error) => ValidationErrorMessage = error;

    /// <summary>The client-side validation error (null when the form is valid).</summary>
    public string? ValidationErrorMessage
    {
        get => _validationErrorMessage;
        private set
        {
            if (SetProperty(ref _validationErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ApiKeyWatermark));
            }
        }
    }

    /// <summary>Whether the upstream API key is already configured (edit mode badge; key
    /// material is never shown).</summary>
    public bool ExistingApiKeyConfigured { get; set; }

    /// <summary>Adds an empty roster row.</summary>
    [RelayCommand]
    public void AddModelRow() => Models.Add(new ModelRosterEntry());

    /// <summary>Removes a roster row.</summary>
    [RelayCommand]
    public void RemoveModelRow(ModelRosterEntry? entry)
    {
        if (entry is not null)
        {
            Models.Remove(entry);
        }
    }

    /// <summary>
    /// Validates the form and builds the <c>POST /api/router/targets</c> request (add mode).
    /// </summary>
    public (AddTargetRequest Request, string? Error) BuildAddRequest()
    {
        ValidationErrorMessage = null;
        if (!ValidateCommon(out var error))
        {
            return (null!, error!);
        }

        var addModels = BuildModels(); // null = empty roster OR roster validation error
        if (addModels is null && Models.Count > 0)
        {
            return (null!, ValidationErrorMessage);
        }

        return (new AddTargetRequest(
            Name: Name.Trim(),
            Url: NormalizeUrl(),
            MaxParallel: int.Parse(MaxParallel),
            TestConnectionFirst: TestConnectionFirst,
            HardwareHint: string.IsNullOrWhiteSpace(HardwareHint) ? null : HardwareHint.Trim(),
            Type: Type,
            ApiKey: string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,
            Provider: string.IsNullOrWhiteSpace(Provider) ? null : Provider.Trim(),
            Models: addModels,
            PowerWatts: ParseWatts()), null);
    }

    /// <summary>
    /// Validates the form and builds the <c>PUT /api/router/targets/{name}</c> request (edit
    /// mode; all fields are sent — the API-key no-change/clear semantics apply).
    /// </summary>
    public (UpdateTargetRequest Request, string? Error) BuildUpdateRequest()
    {
        ValidationErrorMessage = null;
        if (!ValidateCommon(out var error))
        {
            return (null!, error!);
        }

        var updateModels = BuildModels(); // null = empty roster OR roster validation error
        if (updateModels is null && Models.Count > 0)
        {
            return (null!, ValidationErrorMessage);
        }

        return (new UpdateTargetRequest(
            Url: NormalizeUrl(),
            Enabled: null,
            HardwareHint: string.IsNullOrWhiteSpace(HardwareHint) ? null : HardwareHint.Trim(),
            MaxParallel: int.Parse(MaxParallel),
            Type: Type,
            // Wire semantics: null = no change, "" = clear, value = set.
            ApiKey: ApiKeyWireValue(),
            Provider: string.IsNullOrWhiteSpace(Provider) ? null : Provider.Trim(),
            Models: updateModels,
            // Blank = null = NO CHANGE (clearing a previously set override is a documented
            // API limitation — editing config/config.json removes the key).
            PowerWatts: ParseWatts()), null);
    }

    private void ClearError()
    {
        if (ValidationErrorMessage is not null)
        {
            ValidationErrorMessage = null;
        }
    }

    private bool ValidateCommon(out string? error)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "The target name must not be empty.";
            return false;
        }

        var trimmed = Name.Trim();
        if (trimmed.Length > 64)
        {
            error = "The target name must be 64 characters or fewer.";
            return false;
        }

        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains(':'))
        {
            error = "The target name must not contain '/', '\\' or ':'.";
            return false;
        }

        if (string.Equals(trimmed, "Cloud", System.StringComparison.OrdinalIgnoreCase))
        {
            error = "\"Cloud\" is reserved for the cloud overflow pseudo-target.";
            return false;
        }

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "The target URL must be an absolute http(s) URI.";
            return false;
        }

        if (!int.TryParse(MaxParallel, out var maxParallel) || maxParallel < 1)
        {
            error = "maxParallel must be an integer of 1 or greater.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(PowerWatts) &&
            (!double.TryParse(PowerWatts, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var watts) || watts < 0))
        {
            error = "powerWatts must be a non-negative number (or blank).";
            return false;
        }

        error = null;
        return true;
    }

    private string NormalizeUrl() =>
        Url.Trim().TrimEnd('/');

    private double? ParseWatts() =>
        string.IsNullOrWhiteSpace(PowerWatts)
            ? null
            : double.Parse(PowerWatts, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>
    /// The API key wire value in EDIT mode: null (no change) when the box is untouched,
    /// <c>""</c> (clear) when explicitly emptied, the value otherwise.
    /// </summary>
    private string? ApiKeyWireValue()
    {
        if (!_isEdit)
        {
            return null;
        }

        if (!_apiKeyTouched)
        {
            return null; // No change.
        }

        return string.IsNullOrEmpty(_apiKey) ? string.Empty : _apiKey; // "" = clear, else set.
    }
}
