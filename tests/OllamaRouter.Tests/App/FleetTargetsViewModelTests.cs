using OllamaRouter.App.ViewModels;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="FleetTargetsViewModel"/> tests (task #102, spec §7): the merged
/// fleet + config + hardware rows, the effective read-only values (no write call is ever
/// issued for them), the add flow (full request capture + refresh on success + 422 surface),
/// the edit API-key no-change/clear wire semantics, delete, and the persisted enablement
/// toggle.
/// </summary>
public sealed class FleetTargetsViewModelTests : IAsyncLifetime
{
    private readonly FakeRouterApiClient _client = new();
    private readonly FleetTargetsViewModel _viewModel;

    public FleetTargetsViewModelTests()
    {
        _viewModel = new FleetTargetsViewModel(() => _client);
    }

    internal static void SeedFleet(FakeRouterApiClient client)
    {
        client.SeedTarget(new RouterTargetConfigDto(
            "Local", "http://127.0.0.1:11434/", true, "RTX 4090", 2, "ollama", null,
            [new TargetModelDto("llama3:8b", 8192, "Q4_K_M")], true, 350));
        client.SeedTarget(new RouterTargetConfigDto(
            "OpenRouter", "https://openrouter.ai/api", true, null, 1, "openai", "OpenRouter",
            [new TargetModelDto("openrouter/gpt-4o-mini")], true, null));
        client.ConfigResponse = client.ConfigResponse with
        {
            Targets =
            [
                new RouterTargetConfigDto(
                    "Local", "http://127.0.0.1:11434/", true, "RTX 4090", 2, "ollama", null,
                    [new TargetModelDto("llama3:8b", 8192, "Q4_K_M")], true, 350),
                new RouterTargetConfigDto(
                    "OpenRouter", "https://openrouter.ai/api", true, null, 1, "openai", "OpenRouter",
                    [new TargetModelDto("openrouter/gpt-4o-mini")], true, null),
            ],
        };
        client.FleetResponse = new RouterFleetResponse(
        [
            new RouterTargetItem(
                "Local", "http://127.0.0.1:11434/", true, "Online", 12, "local",
                new RouterTargetHardware(
                    "NVIDIA", ["GPU0"], 24576, 2, 1.0, false, 32768, "GDDR6X", 1008.0,
                    "NVIDIA GeForce RTX 4090"),
                new RouterTargetExecution(1, "llama3:8b", "4k", 2, ["llama3:8b"], 600),
                new RouterTargetPreload(900, "class")),
            new RouterTargetItem(
                "OpenRouter", "https://openrouter.ai/api", true, "Online", 340, "remote",
                new RouterTargetHardware("CPU", [], null, 1, 0.2),
                new RouterTargetExecution(0, null, "-"),
                null),
        ]);
        client.HardwareResponse = new RouterFleetHardwareResponse(
        [
            new FleetHardwareNode(
                "Local", "peer",
                new NodeHardwareProfile(
                    "Local", "Linux", "x64", false, 32768, SystemRamType.Ddr5,
                    [new GpuDeviceProfile("NVIDIA", "RTX 4090", 24576, "GDDR6X", 384, 1008)],
                    24576, 1008),
                true),
        ]);
    }

    [Fact]
    public async Task RefreshAsync_MergesFleetConfigAndHardware_Rows()
    {
        SeedFleet(_client);

        await _viewModel.RefreshAsync();

        Assert.Equal(2, _viewModel.Rows.Count);
        var local = _viewModel.Rows[0];
        Assert.Equal("Local", local.Name);
        Assert.True(local.Enabled);
        Assert.Equal("Online", local.Liveness);
        Assert.Equal(12, local.LatencyMs);
        Assert.Equal("local", local.Location);
        Assert.Equal("ollama", local.Type);
        Assert.Equal(2, local.ConfigMaxParallel);
        Assert.Equal(350, local.PowerWatts);
        Assert.True(local.ApiKeyConfigured);
        Assert.Equal(1, local.InFlightRequests);
        Assert.Equal("llama3:8b", local.ActiveModel);

        var openrouter = _viewModel.Rows[1];
        Assert.Equal("openai", openrouter.Type);
        Assert.Equal("OpenRouter", openrouter.Provider);
        Assert.Equal("remote", openrouter.Location);
    }

    [Fact]
    public async Task RefreshAsync_SurfacesReadOnlyEffectiveValues_AndHardwareProfile()
    {
        SeedFleet(_client);

        await _viewModel.RefreshAsync();

        var local = _viewModel.Rows[0];
        // EFFECTIVE values rendered read-only from GET /api/router/fleet (+ the effective
        // keep-alive policy). No write call may be issued for them.
        Assert.Equal(24576, local.MaxVramMB);
        Assert.Equal(1.0, local.SpeedFactor);
        Assert.Equal(900, local.EffectiveKeepAliveSeconds);
        Assert.Equal("class", local.KeepAliveSource);
        Assert.False(local.IsUnifiedMemory);
        Assert.Equal(32768, local.SystemRamMB);
        Assert.Equal(1008.0, local.BandwidthGBs);
        Assert.Equal("NVIDIA GeForce RTX 4090", local.GpuName);

        // Hardware inspector: the per-node profile + provenance + reachability.
        Assert.Equal("peer", local.HardwareSource);
        Assert.True(local.HardwareReachable);
        var profile = Assert.IsType<NodeHardwareProfile>(local.HardwareProfile);
        Assert.Equal("Linux", profile.Os);
        Assert.Single(profile.Gpus);
        Assert.Equal(24576, profile.Gpus[0].VramMB);

        Assert.Empty(_client.UpdateTargetCalls);
        Assert.Empty(_client.UpdateConfigCalls);
    }

    [Fact]
    public async Task AddTarget_BuildsTheFullRequest_AndRefreshesOnSuccess()
    {
        SeedFleet(_client);
        await _viewModel.RefreshAsync();

        var form = _viewModel.CreateAddForm();
        form.Name = "GroqBox";
        form.Url = "https://api.groq.com/openai/v1/";
        form.Type = "openai";
        form.Provider = "Groq";
        form.MaxParallel = "4";
        form.HardwareHint = "cloud-groq";
        form.ApiKey = "gsk_key";
        form.PowerWatts = "0";
        form.Models.Add(new ModelRosterEntry
        {
            Name = "llama-3.3-70b",
            ContextLength = "128000",
            Quantization = "Q8_0",
        });

        var (request, error) = form.BuildAddRequest();
        Assert.Null(error);
        await _viewModel.AddTargetCommand.ExecuteAsync(request);

        var sent = Assert.Single(_client.AddTargetCalls);
        Assert.Equal("GroqBox", sent.Name);
        Assert.Equal("https://api.groq.com/openai/v1", sent.Url); // trailing / stripped
        Assert.Equal("openai", sent.Type);
        Assert.Equal("Groq", sent.Provider);
        Assert.Equal(4, sent.MaxParallel);
        Assert.True(sent.TestConnectionFirst);
        Assert.Equal("cloud-groq", sent.HardwareHint);
        Assert.Equal("gsk_key", sent.ApiKey);
        Assert.Equal(0.0, sent.PowerWatts);
        var model = Assert.Single(sent.Models!);
        Assert.Equal("llama-3.3-70b", model.Name);
        Assert.Equal(128000, model.ContextLength);
        Assert.Equal("Q8_0", model.Quantization);
        Assert.Contains(_viewModel.Rows, r => r.Name == "GroqBox");
    }

    [Fact]
    public async Task AddTarget_ProbeFailure_SurfacesTheError_AndDoesNotAddTheRow()
    {
        SeedFleet(_client);
        await _viewModel.RefreshAsync();

        var form = _viewModel.CreateAddForm();
        form.Name = "Flaky";
        form.Url = "http://10.0.0.9:11434";
        var (request, _) = form.BuildAddRequest();

        _client.AddTargetError = (System.Net.HttpStatusCode.UnprocessableEntity,
            "connection probe failed: connection refused (retry when the target is up)");
        await _viewModel.AddTargetCommand.ExecuteAsync(request);

        Assert.True(_viewModel.IsErrorVisible);
        Assert.Contains("connection probe failed", _viewModel.LastError);
        Assert.DoesNotContain(_viewModel.Rows, r => r.Name == "Flaky");
    }

    [Fact]
    public async Task EditTarget_ApiKeyUntouched_SendsNull_NoChange()
    {
        SeedFleet(_client);
        await _viewModel.RefreshAsync();

        var row = _viewModel.Rows[0];
        var form = _viewModel.CreateEditForm(row);
        Assert.True(form.IsEdit);
        Assert.True(form.ExistingApiKeyConfigured);
        form.MaxParallel = "3";

        var (request, error) = form.BuildUpdateRequest();
        Assert.Null(error);
        await _viewModel.EditTargetCommand.ExecuteAsync(("Local", request));

        var sent = Assert.Single(_client.UpdateTargetCalls);
        Assert.Equal("Local", sent.Name);
        Assert.Null(sent.Request.ApiKey); // untouched = NO CHANGE (absent on the wire)
        Assert.Equal(3, sent.Request.MaxParallel);
        Assert.Equal(3, _client.Targets["Local"].MaxParallel); // persisted by the store
    }

    [Fact]
    public async Task EditTarget_ApiKeyExplicitlyEmptied_SendsEmptyString_Clear()
    {
        SeedFleet(_client);
        await _viewModel.RefreshAsync();

        var row = _viewModel.Rows[0];
        Assert.True(row.ApiKeyConfigured);
        var form = _viewModel.CreateEditForm(row);
        form.ApiKey = "temp"; // touch the box…
        form.ApiKey = "";     // …then explicitly empty it → CLEAR
        var (request, _) = form.BuildUpdateRequest();

        await _viewModel.EditTargetCommand.ExecuteAsync(("Local", request));

        Assert.Equal(string.Empty, _client.UpdateTargetCalls.Single().Request.ApiKey);
        Assert.False(_client.Targets["Local"].ApiKeyConfigured); // cleared on the store
    }

    [Fact]
    public async Task DeleteTarget_RemovesTheRow_AfterTheDeleteCall()
    {
        SeedFleet(_client);
        await _viewModel.RefreshAsync();
        Assert.Equal(2, _viewModel.Rows.Count);

        await _viewModel.DeleteTargetCommand.ExecuteAsync("OpenRouter");

        Assert.Equal("OpenRouter", Assert.Single(_client.DeleteTargetCalls));
        Assert.Single(_viewModel.Rows);
        Assert.Equal("Local", _viewModel.Rows[0].Name);
    }

    [Fact]
    public async Task TogglePersistedEnabled_SendsOnlyTheEnabledFlag()
    {
        SeedFleet(_client);
        await _viewModel.RefreshAsync();
        var row = _viewModel.Rows[0];
        Assert.True(row.Enabled);

        await _viewModel.TogglePersistedEnabledCommand.ExecuteAsync(row);

        var sent = Assert.Single(_client.UpdateTargetCalls);
        Assert.Equal("Local", sent.Name);
        Assert.False(sent.Request.Enabled);
        Assert.Null(sent.Request.Url);
        Assert.Null(sent.Request.ApiKey);
        Assert.False(_client.Targets["Local"].Enabled);
    }

    [Fact]
    public void BuildAddRequest_ValidatesNameUrlParallel_AndRoster()
    {
        var form = new TargetEditFormViewModel(isEdit: false);
        form.Url = "http://127.0.0.1:11434";

        form.Name = "Bad/Name";
        Assert.NotNull(form.BuildAddRequest().Error);

        form.Name = "Cloud";
        Assert.NotNull(form.BuildAddRequest().Error);

        form.Name = "ok";
        form.MaxParallel = "0";
        Assert.NotNull(form.BuildAddRequest().Error);

        form.MaxParallel = "1";
        form.PowerWatts = "-5";
        Assert.NotNull(form.BuildAddRequest().Error);

        form.PowerWatts = "";
        form.Models.Add(new ModelRosterEntry { Name = "  " });
        Assert.NotNull(form.BuildAddRequest().Error);

        form.Models.Clear();
        Assert.Null(form.BuildAddRequest().Error);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() => await _viewModel.DisposeAsync();
}
