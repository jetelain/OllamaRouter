using OllamaRouter.App.ViewModels;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Config;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="SettingsViewModel"/> tests (task #102, spec §7): load populates all four forms
/// + the ApiSecurity read-only display + the masked key list; save issues an
/// <c>UpdateRouterConfigRequest</c> with ALL FOUR sections and re-populates from the echoed
/// config; negative prices are rejected client-side (no API call); the API-key create
/// (one-time raw key), delete and rotate flows.
/// </summary>
public sealed class SettingsViewModelTests : IAsyncLifetime
{
    private readonly FakeRouterApiClient _client = new();
    private readonly SettingsViewModel _viewModel;

    public SettingsViewModelTests()
    {
        _viewModel = new SettingsViewModel(() => _client);
    }

    internal static void SeedConfig(FakeRouterApiClient client)
    {
        client.ConfigResponse = client.ConfigResponse with
        {
            Cloud = new RouterCloudConfigDto(true, "gpt-4o-mini", "Local", true),
            DynamicContext = new RouterDynamicContextConfigDto(false, true, 5, 600),
            Resilience = new RouterResilienceConfigDto(0, 2000),
            Pricing = new RouterPricingConfigDto(
                3.0, 15.0, null, "$", 0.75, 0.28, 200.0),
            ApiSecurity = new ApiSecurityConfigDto(true, 48),
        };
        client.ApiKeys.Add(new ApiKeyInfoResponse(
            "ci-key", DateTimeOffset.UtcNow.AddDays(-2), null));
    }

    [Fact]
    public async Task Load_PopulatesAllForms_AndTheReadOnlySections()
    {
        SeedConfig(_client);

        await _viewModel.RefreshAsync();

        Assert.True(_viewModel.IsLoaded);
        Assert.False(_viewModel.IsErrorVisible);

        var dc = _viewModel.DynamicContext;
        Assert.False(dc.Enabled);
        Assert.True(dc.DenseClampEnabled);
        Assert.Equal("5", dc.AutoShrinkCooldownTurns);
        Assert.Equal("600", dc.AutoShrinkIdleTimeoutSeconds);

        var resilience = _viewModel.Resilience;
        Assert.Equal("0", resilience.MaxConsecutiveFailures);
        Assert.Equal("2000", resilience.ConsecutiveFailuresTokenTolerance);

        var pricing = _viewModel.Pricing;
        Assert.Equal("3", pricing.PromptPricePerMillion);
        Assert.Equal("15", pricing.CompletionPricePerMillion);
        Assert.Equal(string.Empty, pricing.PricePerMillion); // null → blank
        Assert.Equal("$", pricing.Currency);
        Assert.Equal("0.75", pricing.CachedPromptPricePerMillion);
        Assert.Equal("0.28", pricing.ElectricityPricePerKwh);
        Assert.Equal("200", pricing.DefaultNodePowerWatts);

        var cloud = _viewModel.Cloud;
        Assert.True(cloud.Enabled);
        Assert.Equal("gpt-4o-mini", cloud.DefaultModel);
        Assert.Equal("Local", cloud.PreferredRelayTarget);
        Assert.True(cloud.OverflowConfigured);

        // ApiSecurity is read-only display (NOT part of the PUT body).
        Assert.True(_viewModel.RequireKeyForAllEndpoints);
        Assert.Equal(48, _viewModel.DefaultKeyRotationHours);

        var key = Assert.Single(_viewModel.ApiKeys);
        Assert.Equal("ci-key", key.Name);
    }

    [Fact]
    public async Task Save_IssuesAllFourSections_AndRepopulatesFromTheEcho()
    {
        SeedConfig(_client);
        await _viewModel.RefreshAsync();

        _viewModel.DynamicContext.Enabled = true;
        _viewModel.DynamicContext.AutoShrinkCooldownTurns = "7";
        _viewModel.Resilience.MaxConsecutiveFailures = "5";
        _viewModel.Pricing.PromptPricePerMillion = "4.5";
        _viewModel.Cloud.DefaultModel = "gpt-5";

        await _viewModel.SaveCommand.ExecuteAsync(null);

        var request = Assert.Single(_client.UpdateConfigCalls);
        Assert.NotNull(request.Cloud);
        Assert.NotNull(request.DynamicContext);
        Assert.NotNull(request.Resilience);
        Assert.NotNull(request.Pricing);
        Assert.True(request.DynamicContext!.Enabled);
        Assert.Equal(7, request.DynamicContext.AutoShrinkCooldownTurns);
        Assert.Equal(5, request.Resilience!.MaxConsecutiveFailures);
        Assert.Equal(4.5, request.Pricing!.PromptPricePerMillion);
        Assert.Equal("gpt-5", request.Cloud!.DefaultModel);

        // The echoed config re-populated the forms (the fake echoes the new values).
        Assert.True(_viewModel.DynamicContext.Enabled);
        Assert.Equal("7", _viewModel.DynamicContext.AutoShrinkCooldownTurns);
        Assert.Equal("gpt-5", _viewModel.Cloud.DefaultModel);
        Assert.Equal("4.5", _viewModel.Pricing.PromptPricePerMillion);
        Assert.False(_viewModel.IsErrorVisible);
    }

    [Fact]
    public async Task Save_NegativePrice_IsRejectedClientSide_WithoutCallingTheApi()
    {
        SeedConfig(_client);
        await _viewModel.RefreshAsync();

        _viewModel.Pricing.PromptPricePerMillion = "-1";

        await _viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_client.UpdateConfigCalls);
        Assert.True(_viewModel.IsErrorVisible);
        Assert.Contains("non-negative", _viewModel.LastError);
    }

    [Fact]
    public async Task Save_On400_SurfacesTheServerError()
    {
        SeedConfig(_client);
        await _viewModel.RefreshAsync();
        _client.UpdateConfigError = (System.Net.HttpStatusCode.BadRequest, "no configuration sections provided");

        await _viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(_viewModel.IsErrorVisible);
        Assert.Contains("no configuration sections provided", _viewModel.LastError);
    }

    [Fact]
    public async Task CreateKey_StoresNothingOfTheRawKey_AndShowsItExactlyOnce()
    {
        SeedConfig(_client);
        await _viewModel.RefreshAsync();

        _viewModel.CreateKeyName = "ci-nightly";
        _viewModel.CreateKeyHours = "168";
        await _viewModel.CreateKeyCommand.ExecuteAsync(null);

        var created = Assert.Single(_client.CreatedKeys);
        Assert.Equal("ci-nightly", created.Name);
        Assert.NotNull(created.ExpiresAt);
        Assert.True(created.ExpiresAt > DateTimeOffset.UtcNow.AddHours(167));
        // The raw key is surfaced ONCE in the one-time panel…
        Assert.Same(created.Key, _viewModel.CreatedKey);
        Assert.True(_viewModel.IsCreatedKeyPanelVisible);
        // …and the masked list holds NO key material.
        Assert.Equal(2, _client.ApiKeys.Count);
        Assert.Equal(2, _viewModel.ApiKeys.Count);
        Assert.All(_viewModel.ApiKeys, k => Assert.NotEqual(created.Key, k.Name));
    }

    [Fact]
    public async Task CreateKey_InvalidName_IsRejectedClientSide()
    {
        SeedConfig(_client);
        await _viewModel.RefreshAsync();

        _viewModel.CreateKeyName = "has whitespace";
        await _viewModel.CreateKeyCommand.ExecuteAsync(null);
        Assert.Empty(_client.CreatedKeys);
        Assert.True(_viewModel.IsErrorVisible);

        _viewModel.CreateKeyName = "ok";
        _viewModel.CreateKeyHours = "0";
        await _viewModel.CreateKeyCommand.ExecuteAsync(null);
        Assert.Empty(_client.CreatedKeys);
        Assert.Contains("1 hour", _viewModel.LastError);
    }

    [Fact]
    public async Task DeleteKey_RemovesTheMaskedRow()
    {
        SeedConfig(_client);
        await _viewModel.RefreshAsync();

        await _viewModel.DeleteKeyCommand.ExecuteAsync("ci-key");

        Assert.Empty((await _client.GetApiKeysAsync()).Keys);
        Assert.Empty(_viewModel.ApiKeys);
        Assert.Empty(_client.ApiKeys);
    }

    [Fact]
    public async Task RotateDefaultKey_ShowsTheNewKeyExactlyOnce_WithTheInvalidationNote()
    {
        SeedConfig(_client);
        await _viewModel.RefreshAsync();

        await _viewModel.RotateDefaultKeyCommand.ExecuteAsync(null);

        var rotated = Assert.Single(_client.RotatedKeys);
        Assert.Same(rotated.Key, _viewModel.RotatedKey);
        Assert.True(_viewModel.IsRotatedKeyPanelVisible);
        Assert.False(_viewModel.IsErrorVisible);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() => await _viewModel.DisposeAsync();
}
