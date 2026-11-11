using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Config;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// The DynamicContext settings form (task #102, spec §5.4). The wire fields are
/// non-nullable ints (≥ 0), so the inputs are always populated from the live config and
/// must parse on save.
/// </summary>
public sealed partial class SettingsDynamicContextForm : ObservableObject
{
    private bool _enabled;
    private bool _denseClampEnabled;
    private string _autoShrinkCooldownTurns = string.Empty;
    private string _autoShrinkIdleTimeoutSeconds = string.Empty;

    /// <summary><c>enabled</c>.</summary>
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    /// <summary><c>denseClampEnabled</c>.</summary>
    public bool DenseClampEnabled { get => _denseClampEnabled; set => SetProperty(ref _denseClampEnabled, value); }

    /// <summary><c>autoShrinkCooldownTurns</c> (int ≥ 0).</summary>
    public string AutoShrinkCooldownTurns
    {
        get => _autoShrinkCooldownTurns;
        set => SetProperty(ref _autoShrinkCooldownTurns, value);
    }

    /// <summary><c>autoShrinkIdleTimeoutSeconds</c> (int ≥ 0).</summary>
    public string AutoShrinkIdleTimeoutSeconds
    {
        get => _autoShrinkIdleTimeoutSeconds;
        set => SetProperty(ref _autoShrinkIdleTimeoutSeconds, value);
    }

    /// <summary>Populates the form from the wire section.</summary>
    public void Populate(RouterDynamicContextConfigDto dto)
    {
        Enabled = dto.Enabled;
        DenseClampEnabled = dto.DenseClampEnabled;
        AutoShrinkCooldownTurns = dto.AutoShrinkCooldownTurns.ToString(CultureInfo.InvariantCulture);
        AutoShrinkIdleTimeoutSeconds = dto.AutoShrinkIdleTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Builds the wire section (validation: ints ≥ 0).</summary>
    public (RouterDynamicContextConfigDto Dto, string? Error) Build()
    {
        if (!int.TryParse(AutoShrinkCooldownTurns, out var cooldown) || cooldown < 0)
        {
            return (null!, "DynamicContext: autoShrinkCooldownTurns must be an integer of 0 or greater.");
        }

        if (!int.TryParse(AutoShrinkIdleTimeoutSeconds, out var idleTimeout) || idleTimeout < 0)
        {
            return (null!, "DynamicContext: autoShrinkIdleTimeoutSeconds must be an integer of 0 or greater.");
        }

        return (new RouterDynamicContextConfigDto(
            Enabled, DenseClampEnabled, cooldown, idleTimeout), null);
    }
}

/// <summary>
/// The Resilience settings form (task #102, spec §5.4): the failure-streak threshold
/// (0 = streak skipping disabled) and the retry-streak token tolerance (0 = exact match
/// required).
/// </summary>
public sealed partial class SettingsResilienceForm : ObservableObject
{
    private string _maxConsecutiveFailures = string.Empty;
    private string _consecutiveFailuresTokenTolerance = string.Empty;

    /// <summary><c>maxConsecutiveFailures</c> (int ≥ 0; 0 = disabled).</summary>
    public string MaxConsecutiveFailures
    {
        get => _maxConsecutiveFailures;
        set => SetProperty(ref _maxConsecutiveFailures, value);
    }

    /// <summary><c>consecutiveFailuresTokenTolerance</c> (int ≥ 0; 0 = exact match required).</summary>
    public string ConsecutiveFailuresTokenTolerance
    {
        get => _consecutiveFailuresTokenTolerance;
        set => SetProperty(ref _consecutiveFailuresTokenTolerance, value);
    }

    /// <summary>Populates the form from the wire section.</summary>
    public void Populate(RouterResilienceConfigDto dto)
    {
        MaxConsecutiveFailures = dto.MaxConsecutiveFailures.ToString(CultureInfo.InvariantCulture);
        ConsecutiveFailuresTokenTolerance =
            dto.ConsecutiveFailuresTokenTolerance.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Builds the wire section (validation: ints ≥ 0).</summary>
    public (RouterResilienceConfigDto Dto, string? Error) Build()
    {
        if (!int.TryParse(MaxConsecutiveFailures, out var maxFailures) || maxFailures < 0)
        {
            return (null!, "Resilience: maxConsecutiveFailures must be an integer of 0 or greater.");
        }

        if (!int.TryParse(ConsecutiveFailuresTokenTolerance, out var tolerance) || tolerance < 0)
        {
            return (null!, "Resilience: consecutiveFailuresTokenTolerance must be an integer of 0 or greater.");
        }

        return (new RouterResilienceConfigDto(maxFailures, tolerance), null);
    }
}

/// <summary>
/// The Pricing settings form (task #102, spec §5.4): the per-million-token rates (blank =
/// null = rate not configured; negative values are rejected CLIENT-SIDE), the display
/// currency and the electricity-cost leg (blank = server defaults 0.20/kWh / 150 W).
/// </summary>
public sealed partial class SettingsPricingForm : ObservableObject
{
    private string _promptPricePerMillion = string.Empty;
    private string _completionPricePerMillion = string.Empty;
    private string _pricePerMillion = string.Empty;
    private string _cachedPromptPricePerMillion = string.Empty;
    private string _currency = "$";
    private string _electricityPricePerKwh = string.Empty;
    private string _defaultNodePowerWatts = string.Empty;

    /// <summary><c>promptPricePerMillion</c> (blank = null).</summary>
    public string PromptPricePerMillion
    {
        get => _promptPricePerMillion;
        set => SetProperty(ref _promptPricePerMillion, value);
    }

    /// <summary><c>completionPricePerMillion</c> (blank = null).</summary>
    public string CompletionPricePerMillion
    {
        get => _completionPricePerMillion;
        set => SetProperty(ref _completionPricePerMillion, value);
    }

    /// <summary><c>pricePerMillion</c> (flat rate; blank = null).</summary>
    public string PricePerMillion
    {
        get => _pricePerMillion;
        set => SetProperty(ref _pricePerMillion, value);
    }

    /// <summary><c>cachedPromptPricePerMillion</c> (blank = null = not differentiated).</summary>
    public string CachedPromptPricePerMillion
    {
        get => _cachedPromptPricePerMillion;
        set => SetProperty(ref _cachedPromptPricePerMillion, value);
    }

    /// <summary><c>currency</c> (display symbol).</summary>
    public string Currency
    {
        get => _currency;
        set => SetProperty(ref _currency, value);
    }

    /// <summary><c>electricityPricePerKwh</c> (blank = null ⇒ 0.20 effective).</summary>
    public string ElectricityPricePerKwh
    {
        get => _electricityPricePerKwh;
        set => SetProperty(ref _electricityPricePerKwh, value);
    }

    /// <summary><c>defaultNodePowerWatts</c> (blank = null ⇒ 150 W effective).</summary>
    public string DefaultNodePowerWatts
    {
        get => _defaultNodePowerWatts;
        set => SetProperty(ref _defaultNodePowerWatts, value);
    }

    /// <summary>Populates the form from the wire section (null → blank).</summary>
    public void Populate(RouterPricingConfigDto dto)
    {
        PromptPricePerMillion = Format(dto.PromptPricePerMillion);
        CompletionPricePerMillion = Format(dto.CompletionPricePerMillion);
        PricePerMillion = Format(dto.PricePerMillion);
        CachedPromptPricePerMillion = Format(dto.CachedPromptPricePerMillion);
        Currency = dto.Currency;
        ElectricityPricePerKwh = Format(dto.ElectricityPricePerKwh);
        DefaultNodePowerWatts = Format(dto.DefaultNodePowerWatts);
    }

    /// <summary>Builds the wire section (validation: numbers ≥ 0 when present).</summary>
    public (RouterPricingConfigDto Dto, string? Error) Build()
    {
        var prompt = ParseRate(PromptPricePerMillion, out var error);
        if (prompt is null && error is not null)
        {
            return (null!, $"Pricing: {error}");
        }

        var completion = ParseRate(CompletionPricePerMillion, out error);
        if (completion is null && error is not null)
        {
            return (null!, $"Pricing: {error}");
        }

        var flat = ParseRate(PricePerMillion, out error);
        if (flat is null && error is not null)
        {
            return (null!, $"Pricing: {error}");
        }

        var cached = ParseRate(CachedPromptPricePerMillion, out error);
        if (cached is null && error is not null)
        {
            return (null!, $"Pricing: {error}");
        }

        var electricity = ParseRate(ElectricityPricePerKwh, out error);
        if (electricity is null && error is not null)
        {
            return (null!, $"Pricing: {error}");
        }

        var watts = ParseRate(DefaultNodePowerWatts, out error);
        if (watts is null && error is not null)
        {
            return (null!, $"Pricing: {error}");
        }

        return (new RouterPricingConfigDto(
            prompt, completion, flat,
            string.IsNullOrWhiteSpace(Currency) ? "$" : Currency.Trim(),
            cached, electricity, watts), null);
    }

    private static string Format(double? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static double? ParseRate(string raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            error = $"\"{raw}\" must be a non-negative number (or blank).";
            return null;
        }

        return value;
    }
}

/// <summary>
/// The Cloud section form (task #102, spec §5.4). Wire semantics of
/// <c>defaultModel</c> / <c>preferredRelayTarget</c> on save: null/absent = no change,
/// <c>""</c> = clear.
/// </summary>
public sealed partial class SettingsCloudForm : ObservableObject
{
    private bool _enabled;
    private string _defaultModel = string.Empty;
    private string _preferredRelayTarget = string.Empty;
    private bool _overflowConfigured;

    /// <summary><c>enabled</c> (always applied on save).</summary>
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    /// <summary><c>defaultModel</c> (null/blank = no change; explicit "" clears).</summary>
    public string DefaultModel
    {
        get => _defaultModel;
        set => SetProperty(ref _defaultModel, value);
    }

    /// <summary><c>preferredRelayTarget</c> (null/blank = no change; explicit "" clears to
    /// "Local").</summary>
    public string PreferredRelayTarget
    {
        get => _preferredRelayTarget;
        set => SetProperty(ref _preferredRelayTarget, value);
    }

    /// <summary>Read-only badge: whether the cloud relay (ollama.com sign-in) is configured.</summary>
    public bool OverflowConfigured { get => _overflowConfigured; set => SetProperty(ref _overflowConfigured, value); }

    /// <summary>Populates the form from the wire section.</summary>
    public void Populate(RouterCloudConfigDto dto)
    {
        Enabled = dto.Enabled;
        DefaultModel = dto.DefaultModel ?? string.Empty;
        PreferredRelayTarget = dto.PreferredRelayTarget ?? string.Empty;
        OverflowConfigured = dto.OverflowConfigured;
    }

    /// <summary>
    /// Builds the PUT wire section: <c>defaultModel</c> / <c>preferredRelayTarget</c> are sent
    /// exactly as typed (the wire distinguishes null = no change from "" = clear).
    /// </summary>
    public UpdateCloudConfigRequest Build() => new(
        Enabled,
        string.IsNullOrWhiteSpace(DefaultModel) ? null : DefaultModel.Trim(),
        string.IsNullOrWhiteSpace(PreferredRelayTarget) ? null : PreferredRelayTarget.Trim());
}

/// <summary>
/// The Settings &amp; API Keys tab view model (task #102, spec §5.4): the
/// DynamicContext / Resilience / Pricing / Cloud forms saved via
/// <c>PUT /api/router/config</c> (ALL FOUR sections always sent; the response echoes the full
/// config → re-populate), the read-only <c>ApiSecurity</c> display and the
/// <c>/api/router/api-keys</c> management (list, create with optional expiry, one-time raw
/// key display, delete, default-key rotation with immediate-invalidation warning).
/// No polling (load on demand + after save).
/// </summary>
public sealed partial class SettingsViewModel : RouterTabViewModel
{
    public SettingsViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<TimeSpan, CancellationToken, Task>? pollDelay = null)
        : base(clientAccessor, pollDelay)
    {
    }

    /// <inheritdoc />
    protected override TimeSpan PollInterval => TimeSpan.Zero;

    /// <summary>The DynamicContext form.</summary>
    public SettingsDynamicContextForm DynamicContext { get; } = new();

    /// <summary>The Resilience form.</summary>
    public SettingsResilienceForm Resilience { get; } = new();

    /// <summary>The Pricing form.</summary>
    public SettingsPricingForm Pricing { get; } = new();

    /// <summary>The Cloud form.</summary>
    public SettingsCloudForm Cloud { get; } = new();

    // -- Read-only ApiSecurity display (NOT part of the PUT body) ---------------

    /// <summary>ApiSecurity: <c>requireKeyForAllEndpoints</c> (read-only).</summary>
    [ObservableProperty]
    private bool _requireKeyForAllEndpoints;

    /// <summary>ApiSecurity: <c>defaultKeyRotationHours</c> (read-only).</summary>
    [ObservableProperty]
    private int _defaultKeyRotationHours;

    // -- API keys ----------------------------------------------------------------

    /// <summary>The masked user key rows (name / createdAt / expiresAt — no key material).</summary>
    public ObservableCollection<ApiKeyInfoResponse> ApiKeys { get; } = [];

    /// <summary>The new key's name (one-time panel).</summary>
    [ObservableProperty]
    private string? _createdKeyName;

    /// <summary>The NEW raw API key (shown EXACTLY ONCE — only its hash is persisted).</summary>
    [ObservableProperty]
    private string? _createdKey;

    /// <summary>Whether the one-time created-key panel is visible.</summary>
    [ObservableProperty]
    private bool _isCreatedKeyPanelVisible;

    /// <summary>The NEW raw default key after rotation (shown EXACTLY ONCE).</summary>
    [ObservableProperty]
    private string? _rotatedKey;

    /// <summary>Whether the one-time rotated-key panel is visible.</summary>
    [ObservableProperty]
    private bool _isRotatedKeyPanelVisible;

    /// <summary>True after the initial load (disables the Save form until data exists).</summary>
    [ObservableProperty]
    private bool _isLoaded;

    /// <inheritdoc />
    protected override async Task RefreshCoreAsync(CancellationToken ct)
    {
        var client = ClientAccessor();
        var config = await client.GetConfigAsync(ct).ConfigureAwait(false);
        var keys = await client.GetApiKeysAsync(ct).ConfigureAwait(false);

        DynamicContext.Populate(config.DynamicContext);
        Resilience.Populate(config.Resilience);
        Pricing.Populate(config.Pricing);
        Cloud.Populate(config.Cloud);

        RequireKeyForAllEndpoints = config.ApiSecurity.RequireKeyForAllEndpoints;
        DefaultKeyRotationHours = config.ApiSecurity.DefaultKeyRotationHours;

        ApiKeys.Clear();
        foreach (var key in keys.Keys)
        {
            ApiKeys.Add(key);
        }

        IsLoaded = true;
    }

    /// <summary>
    /// Saves all four config sections (wire: <c>PUT /api/router/config</c> — all four always
    /// sent). On success the echoed full config re-populates the forms; on 400 the parsed
    /// <c>message</c> is surfaced in <see cref="RouterTabViewModel.LastError"/>.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var (dynamicContext, dynamicError) = DynamicContext.Build();
        var (resilience, resilienceError) = Resilience.Build();
        var (pricing, pricingError) = Pricing.Build();
        var firstError = dynamicError ?? resilienceError ?? pricingError;
        if (firstError is not null)
        {
            SetError(firstError);
            return;
        }

        try
        {
            var response = await ClientAccessor().UpdateConfigAsync(new UpdateRouterConfigRequest(
                Cloud: Cloud.Build(),
                DynamicContext: dynamicContext,
                Resilience: resilience,
                Pricing: pricing), ct).ConfigureAwait(false);

            if (response.Config is not null)
            {
                DynamicContext.Populate(response.Config.DynamicContext);
                Resilience.Populate(response.Config.Resilience);
                Pricing.Populate(response.Config.Pricing);
                Cloud.Populate(response.Config.Cloud);
                RequireKeyForAllEndpoints = response.Config.ApiSecurity.RequireKeyForAllEndpoints;
                DefaultKeyRotationHours = response.Config.ApiSecurity.DefaultKeyRotationHours;
            }

            SetError(null);
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

    /// <summary>The new-key form: the key name (≤ 64 chars, no whitespace / slash / colon).</summary>
    [ObservableProperty]
    private string _createKeyName = string.Empty;

    /// <summary>The new-key form: the optional validity in hours (blank = no expiry; ≥ 1).</summary>
    [ObservableProperty]
    private string _createKeyHours = string.Empty;

    /// <summary>
    /// Creates a user API key from the form fields (wire: <c>POST /api/router/api-keys</c>):
    /// the raw key is shown EXACTLY ONCE in <see cref="CreatedKey"/> (the panel) and only its
    /// hash is persisted.
    /// </summary>
    [RelayCommand]
    public async Task CreateKeyAsync(CancellationToken ct = default)
    {
        if (!ValidateKeyName(CreateKeyName, out var error))
        {
            SetError(error!);
            return;
        }

        int? validForHours = null;
        if (!string.IsNullOrWhiteSpace(CreateKeyHours))
        {
            if (!int.TryParse(CreateKeyHours, out var hours) || hours < 1)
            {
                SetError("The key validity must be 1 hour or more (or blank for no expiry).");
                return;
            }

            validForHours = hours;
        }

        try
        {
            var created = await ClientAccessor()
                .CreateApiKeyAsync(new ApiKeyCreateRequest(CreateKeyName.Trim(), validForHours), ct)
                .ConfigureAwait(false);
            CreatedKeyName = created.Name;
            CreatedKey = created.Key;
            IsCreatedKeyPanelVisible = true;
            SetError(null);
            await RefreshKeysOnlyAsync(ct).ConfigureAwait(false);
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
    /// Deletes a user API key (wire: <c>DELETE /api/router/api-keys/{name}</c>) and refreshes
    /// the key list.
    /// </summary>
    [RelayCommand]
    public async Task DeleteKeyAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The key name must not be empty.", nameof(name));
        }

        try
        {
            await ClientAccessor().DeleteApiKeyAsync(name, ct).ConfigureAwait(false);
            SetError(null);
            await RefreshKeysOnlyAsync(ct).ConfigureAwait(false);
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
    /// Rotates the default key (wire: <c>POST /api/router/api-keys/default/rotate</c>): the
    /// NEW raw key is shown EXACTLY ONCE in <see cref="RotatedKey"/>; the PREVIOUS key stopped
    /// working immediately (no grace period). Local auth survives transparently: the
    /// <c>DefaultKeyReader</c> re-reads <c>default-key.dat</c> per request and the server
    /// rewrites it on rotation.
    /// </summary>
    [RelayCommand]
    public async Task RotateDefaultKeyAsync(CancellationToken ct = default)
    {
        try
        {
            var rotated = await ClientAccessor().RotateDefaultKeyAsync(ct).ConfigureAwait(false);
            RotatedKey = rotated.Key;
            IsRotatedKeyPanelVisible = true;
            SetError(null);
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

    /// <summary>Re-reads only the API-key list (cheaper than a full config refresh).</summary>
    private async Task RefreshKeysOnlyAsync(CancellationToken ct)
    {
        var keys = await ClientAccessor().GetApiKeysAsync(ct).ConfigureAwait(false);
        ApiKeys.Clear();
        foreach (var key in keys.Keys)
        {
            ApiKeys.Add(key);
        }
    }

    internal static bool ValidateKeyName(string? name, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "The key name must not be empty.";
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 64)
        {
            error = "The key name must be 64 characters or fewer.";
            return false;
        }

        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c) || c == '/' || c == ':')
            {
                error = "The key name must not contain whitespace, '/' or ':'.";
                return false;
            }
        }

        return true;
    }
}
