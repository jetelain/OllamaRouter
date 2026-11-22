using OllamaRouter.App.ViewModels;
using OllamaRouter.Client;
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

    // -- Task #144: ssh credential form fields + the daemon-side hardware probe ---

    private static async Task WaitUntilAsync(Func<bool> condition, int maxBeats = 20000)
    {
        var satisfied = false;
        for (var i = 0; i < maxBeats && !satisfied; i++)
        {
            try
            {
                satisfied = condition();
            }
            catch (InvalidOperationException)
            {
                // The VM's async continuations run on thread-pool threads in the test host
                // (no Avalonia dispatcher) and may rebuild Rows (an ObservableCollection, i.e.
                // a List) WHILE the poll enumerates it — a transient "collection was modified"
                // is not the condition being awaited: treat it as "not yet" and poll again.
                satisfied = false;
            }

            await Task.Yield();
        }

        Assert.True(condition());
    }

    private static TaskCompletionSource Gate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Builds a FIFO <see cref="Queue{T}"/> (the <c>Queue{T}(IEnumerable{T})</c>
    /// constructor does not make <c>Queue{T}</c> a valid collection-expression target).</summary>
    private static Queue<T> AsQueue<T>(params T[] items) => new(items);

    /// <summary>
    /// The task #144 seed: five targets exercising the ssh-probeable matrix — GpuBox
    /// (remote ollama + sshUser + no profile → the ONLY auto-probe eligible row), Local
    /// (local — the daemon probes itself, never ssh-probed), Plain (remote ollama, no
    /// sshUser), Groq (remote openai), Profiled (remote ollama + sshUser but already
    /// profiled, source "peer").
    /// </summary>
    private static void SeedSshFleet(FakeRouterApiClient client)
    {
        client.SeedTarget(new RouterTargetConfigDto(
            "GpuBox", "http://10.0.0.7:11434/", true, null, 1, "ollama", null,
            null, false, null, "gpuadmin", 2222));
        client.SeedTarget(new RouterTargetConfigDto(
            "Local", "http://127.0.0.1:11434/", true, null, 1, "ollama", null,
            null, false, null, "gpuadmin", null));
        client.SeedTarget(new RouterTargetConfigDto(
            "Plain", "http://10.0.0.8:11434/", true, null, 1, "ollama", null,
            null, false, null, null, null));
        client.SeedTarget(new RouterTargetConfigDto(
            "Groq", "https://api.groq.com/openai/v1/", true, null, 1, "openai", "Groq",
            null, false, null, "gpuadmin", null));
        client.SeedTarget(new RouterTargetConfigDto(
            "Profiled", "http://10.0.0.9:11434/", true, null, 1, "ollama", null,
            null, false, null, "gpuadmin", null));
        client.FleetResponse = new RouterFleetResponse(
        [
            new RouterTargetItem("GpuBox", "http://10.0.0.7:11434/", true, "Online", 10, "remote",
                new RouterTargetHardware("CPU", [], null, 1, 0.2),
                new RouterTargetExecution(0, null, "-"), null),
            new RouterTargetItem("Local", "http://127.0.0.1:11434/", true, "Online", 5, "local",
                new RouterTargetHardware("CPU", [], null, 1, 0.2),
                new RouterTargetExecution(0, null, "-"), null),
            new RouterTargetItem("Plain", "http://10.0.0.8:11434/", true, "Online", 10, "remote",
                new RouterTargetHardware("CPU", [], null, 1, 0.2),
                new RouterTargetExecution(0, null, "-"), null),
            new RouterTargetItem("Groq", "https://api.groq.com/openai/v1/", true, "Online", 40, "remote",
                new RouterTargetHardware("CPU", [], null, 1, 0.2),
                new RouterTargetExecution(0, null, "-"), null),
            new RouterTargetItem("Profiled", "http://10.0.0.9:11434/", true, "Online", 10, "remote",
                new RouterTargetHardware("CPU", [], null, 1, 0.2),
                new RouterTargetExecution(0, null, "-"), null),
        ]);
        client.HardwareResponse = new RouterFleetHardwareResponse(
        [
            new FleetHardwareNode("Profiled", "peer",
                new NodeHardwareProfile(
                    "Profiled", "Linux", "x64", false, 16384, SystemRamType.Ddr5,
                    [new GpuDeviceProfile("NVIDIA", "RTX 3090", 24576, "GDDR6X", 384, 936)],
                    24576, 936),
                true),
        ]);
    }

    [Fact]
    public void CreateEditForm_PrefillsSshUserAndSshPort_FromTheRow()
    {
        var row = new FleetTargetRow { Name = "GpuBox", SshUser = "gpuadmin", SshPort = 2222 };

        var form = _viewModel.CreateEditForm(row);

        Assert.Equal("gpuadmin", form.SshUser);
        Assert.Equal("2222", form.SshPort);
        Assert.Equal("ssh: gpuadmin@(port 2222)", row.SshLabel);
    }

    [Fact]
    public void CreateEditForm_BlankSshFields_StayBlank_AndBlankIsValid()
    {
        var row = new FleetTargetRow
        {
            Name = "Plain",
            Url = "http://10.0.0.8:11434/",
            ConfigMaxParallel = 1,
        };

        var form = _viewModel.CreateEditForm(row);

        Assert.Equal(string.Empty, form.SshUser);
        Assert.Equal(string.Empty, form.SshPort);
        Assert.Null(form.BuildUpdateRequest().Error); // blank ssh fields are valid (both modes)
        Assert.Null(row.SshLabel); // no label when unset
    }

    [Fact]
    public void Row_HasHardwareProfile_MirrorsProfilePresence()
    {
        var without = new FleetTargetRow { Name = "Plain" };
        Assert.False(without.HasHardwareProfile);

        var with = new FleetTargetRow
        {
            Name = "GpuBox",
            HardwareProfile = new NodeHardwareProfile(
                "Profiled", "Linux", "x64", false, 16384, SystemRamType.Ddr5,
                [new GpuDeviceProfile("NVIDIA", "RTX 3090", 24576, "GDDR6X", 384, 936)],
                24576, 936),
        };
        Assert.True(with.HasHardwareProfile);
    }

    // -- Task #153: the card display projections (one line per topic) --------------------

    /// <summary>The 2026-11-21 test-session profile: linux x64, 93379 MB RAM, 93379 MB fast
    /// memory, 341.2 GB/s, one lspci AMD GPU with 0 MB / Unknown / 0 GB/s.</summary>
    private static FleetTargetRow TestSessionRow(int? configMaxVramMB = null) => new()
    {
        Name = "StrixBox",
        Location = "remote",
        PowerWatts = 125,
        EffectiveKeepAliveSeconds = -1,
        KeepAliveSource = "default",
        ConfigMaxVramMB = configMaxVramMB,
        HardwareSource = "persisted",
        HardwareProfile = new NodeHardwareProfile(
            "StrixBox", "linux", "x64", true, 93379, SystemRamType.LpDdr5X,
            [
                new GpuDeviceProfile(
                    "AMD", "Strix Halo [Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics]",
                    0, "Unknown", 0, 0),
            ],
            93379, 341.2),
    };

    [Fact]
    public void Row_HardwareSummaryProjections_TestSessionProfile_OneLinePerTopic()
    {
        var row = TestSessionRow();

        // The canonical card lines (CONFIRMED Q1 A: ÷1024, 1 decimal max / 0.## bandwidth).
        Assert.Equal("Hardware profile (persisted)", row.HardwareProfileHeader);
        Assert.Equal("OS: linux x64", row.HardwareOsLine);
        Assert.Equal("RAM: 91.2 GB · VRAM: 91.2 GB · VRAM Speed: 341.2 GB/s", row.HardwareMemoryLine);
        Assert.Equal(
            "GPU: AMD Strix Halo [Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics]",
            row.HardwareGpuLine); // NO 0 MB / Unknown / 0 GB/s sub-values
        Assert.Equal("Keep alive: indefinite (default)", row.KeepAliveLabel);
        // Config override unset → NO annotation (the effective value falls back to the profile).
        Assert.Null(row.VramOverrideLabel);
    }

    [Fact]
    public void Row_HardwareSummaryProjections_NoProfile_HidesTheLines_AndHeaderCoalescesNone()
    {
        var row = new FleetTargetRow
        {
            Name = "Plain",
            HardwareSource = null,
            EffectiveKeepAliveSeconds = null,
            KeepAliveSource = null,
        };

        Assert.Equal("Hardware profile (none)", row.HardwareProfileHeader);
        Assert.False(row.HasHardwareProfile);
        Assert.Null(row.HardwareOsLine);
        Assert.Null(row.HardwareMemoryLine);
        Assert.Null(row.HardwareGpuLine);
        Assert.Null(row.KeepAliveLabel); // null seconds → hidden
        Assert.Null(row.VramOverrideLabel); // profile-anchored → hidden without a profile
    }

    [Fact]
    public void Row_VramOverrideLabel_PredicateCombinations()
    {
        // Differs → the annotation with the human-readable value.
        var differs = TestSessionRow(configMaxVramMB: 24576);
        Assert.Equal("VRAM override: 24 GB", differs.VramOverrideLabel);

        // Equal to the profile's fast memory → the fleet effective value IS the override →
        // invisible → no annotation.
        var equal = TestSessionRow(configMaxVramMB: 93379);
        Assert.Null(equal.VramOverrideLabel);

        // Unset → no annotation.
        Assert.Null(TestSessionRow().VramOverrideLabel);

        // Set but no profile → the annotation is profile-anchored → hidden.
        var noProfile = new FleetTargetRow { Name = "Plain", ConfigMaxVramMB = 24576 };
        Assert.Null(noProfile.VramOverrideLabel);
    }

    [Fact]
    public async Task RefreshAsync_ConfigMaxVramMB_RoundTripsFromTheSeededConfig_AndDrivesTheAnnotation()
    {
        // A LOCAL target (never ssh-probeable → no auto-probes in this test).
        _client.SeedTarget(new RouterTargetConfigDto(
            "StrixBox", "http://127.0.0.1:11435/", true, null, 1, "ollama", null,
            null, false, null, null, null, 24576)); // persisted config override (13th field)
        _client.FleetResponse = new RouterFleetResponse(
        [
            new RouterTargetItem("StrixBox", "http://127.0.0.1:11435/", true, "Online", 5, "local",
                new RouterTargetHardware("CPU", [], null, 1, 0.2),
                new RouterTargetExecution(0, null, "-"), null),
        ]);
        _client.HardwareResponse = new RouterFleetHardwareResponse(
        [
            new FleetHardwareNode("StrixBox", "persisted",
                new NodeHardwareProfile(
                    "StrixBox", "linux", "x64", true, 93379, SystemRamType.LpDdr5X,
                    [new GpuDeviceProfile("AMD", "Strix Halo", 0, "Unknown", 0, 0)],
                    93379, 341.2),
                true),
        ]);

        await _viewModel.RefreshAsync();

        var row = _viewModel.Rows.Single(r => r.Name == "StrixBox");
        Assert.Equal(24576, row.ConfigMaxVramMB); // round-tripped from the seeded config DTO
        Assert.Equal("VRAM override: 24 GB", row.VramOverrideLabel); // ≠ profile 93379 → visible

        // Override == profile → the annotation disappears on the next tick.
        _client.SeedTarget(new RouterTargetConfigDto(
            "StrixBox", "http://127.0.0.1:11435/", true, null, 1, "ollama", null,
            null, false, null, null, null, 93379));
        await _viewModel.RefreshAsync();
        Assert.Null(_viewModel.Rows.Single(r => r.Name == "StrixBox").VramOverrideLabel);
    }

    [Fact]
    public void BuildAddRequest_SshFields_ValidateUsernameCharsetAndPortRange()
    {
        var form = new TargetEditFormViewModel(isEdit: false);
        form.Name = "ok";
        form.Url = "http://10.0.0.7:11434";
        Assert.Null(form.BuildAddRequest().Error); // blank = valid

        const string userError =
            "sshUser must be 1–64 characters of letters, digits, '.', '_' or '-' (or blank).";
        const string portError = "sshPort must be a port between 1 and 65535 (or blank).";

        form.SshUser = "bad user"; // space
        Assert.Equal(userError, form.BuildAddRequest().Error);

        form.SshUser = "üser"; // non-ASCII
        Assert.Equal(userError, form.BuildAddRequest().Error);

        form.SshUser = new string('a', 65); // > 64 chars
        Assert.Equal(userError, form.BuildAddRequest().Error);

        form.SshUser = "gpu.admin-1"; // valid charset
        form.SshPort = "0";
        Assert.Equal(portError, form.BuildAddRequest().Error);

        form.SshPort = "70000";
        Assert.Equal(portError, form.BuildAddRequest().Error);

        form.SshPort = "abc"; // non-numeric
        Assert.Equal(portError, form.BuildAddRequest().Error);

        form.SshPort = "2222";
        Assert.Null(form.BuildAddRequest().Error);
    }

    [Fact]
    public void BuildAddRequest_SshFields_BlankSendsNull_ValueSendsTheValue()
    {
        var form = new TargetEditFormViewModel(isEdit: false);
        form.Name = "ok";
        form.Url = "http://10.0.0.7:11434";
        var (blank, blankError) = form.BuildAddRequest();
        Assert.Null(blankError);
        Assert.Null(blank.SshUser); // ADD: blank = unset (null)
        Assert.Null(blank.SshPort);

        form.SshUser = "gpuadmin";
        form.SshPort = "2222";
        var (filled, filledError) = form.BuildAddRequest();
        Assert.Null(filledError);
        Assert.Equal("gpuadmin", filled.SshUser);
        Assert.Equal(2222, filled.SshPort);
    }

    [Fact]
    public async Task AddTarget_SshFields_CarryTheWireAndRoundTripToTheRow()
    {
        await _viewModel.RefreshAsync(); // empty fleet: no auto-probes

        var form = _viewModel.CreateAddForm();
        form.Name = "GpuBox";
        form.Url = "http://10.0.0.7:11434";
        form.SshUser = "gpuadmin";
        form.SshPort = "2222";
        var (request, error) = form.BuildAddRequest();
        Assert.Null(error);

        await _viewModel.AddTargetCommand.ExecuteAsync(request);

        var sent = Assert.Single(_client.AddTargetCalls);
        Assert.Equal("gpuadmin", sent.SshUser);
        Assert.Equal(2222, sent.SshPort);
        var row = _viewModel.Rows.Single(r => r.Name == "GpuBox");
        Assert.Equal("gpuadmin", row.SshUser); // round-tripped via the fake store
        Assert.Equal(2222, row.SshPort);
        Assert.Equal("ssh: gpuadmin@(port 2222)", row.SshLabel);
        Assert.True(row.CanUpdateHardware); // ssh-probeable row → the button is enabled
    }

    [Fact]
    public async Task EditTarget_SshUserBlank_Clears_AndSshPortBlank_IsNoChange()
    {
        SeedSshFleet(_client);
        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1); // GpuBox auto-probe settled

        var row = _viewModel.Rows.Single(r => r.Name == "GpuBox");
        var form = _viewModel.CreateEditForm(row);
        Assert.Equal("gpuadmin", form.SshUser);
        Assert.Equal("2222", form.SshPort);
        form.SshUser = string.Empty; // explicitly emptied → CLEAR (task #143 wire)
        form.SshPort = string.Empty; // blank → NO CHANGE (no clear semantics on the port wire)

        var (request, error) = form.BuildUpdateRequest();
        Assert.Null(error);
        Assert.Equal(string.Empty, request.SshUser);
        Assert.Null(request.SshPort);

        await _viewModel.EditTargetCommand.ExecuteAsync(("GpuBox", request));

        var stored = _client.Targets["GpuBox"];
        Assert.Null(stored.SshUser); // cleared on the store
        Assert.Equal(2222, stored.SshPort); // untouched
    }

    [Fact]
    public async Task EditTarget_SshFieldsWithValue_SetOnTheStore()
    {
        SeedSshFleet(_client);
        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1); // GpuBox auto-probe settled

        var row = _viewModel.Rows.Single(r => r.Name == "Plain");
        var form = _viewModel.CreateEditForm(row);
        form.SshUser = "root";
        form.SshPort = "22";
        var (request, error) = form.BuildUpdateRequest();
        Assert.Null(error);
        Assert.Equal("root", request.SshUser);
        Assert.Equal(22, request.SshPort);

        await _viewModel.EditTargetCommand.ExecuteAsync(("Plain", request));

        var stored = _client.Targets["Plain"];
        Assert.Equal("root", stored.SshUser);
        Assert.Equal(22, stored.SshPort);
    }

    [Fact]
    public async Task UpdateHardware_NonProbeableRows_IssueNoProbeCall_AndLeaveStateUntouched()
    {
        SeedSshFleet(_client);
        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1); // GpuBox auto-probe only
        var baseline = _client.ProbeCalls.ToList();

        // local / no-sshUser / non-ollama rows are NEVER ssh-probeable → the button is
        // disabled and the command is a no-op.
        var local = _viewModel.Rows.Single(r => r.Name == "Local");
        var plain = _viewModel.Rows.Single(r => r.Name == "Plain");
        var groq = _viewModel.Rows.Single(r => r.Name == "Groq");
        Assert.False(local.CanUpdateHardware);
        Assert.False(plain.CanUpdateHardware);
        Assert.False(groq.CanUpdateHardware);
        Assert.Equal("Update hardware", local.UpdateHardwareButtonContent);

        await _viewModel.UpdateHardwareCommand.ExecuteAsync(local);
        await _viewModel.UpdateHardwareCommand.ExecuteAsync(plain);
        await _viewModel.UpdateHardwareCommand.ExecuteAsync(groq);
        await _viewModel.UpdateHardwareCommand.ExecuteAsync((FleetTargetRow?)null);

        Assert.Equal(baseline, _client.ProbeCalls); // no new probe calls
        Assert.False(local.IsProbing);
        Assert.Null(local.ProbeError);
        Assert.Null(_viewModel.LastError);
    }

    [Fact]
    public async Task UpdateHardware_EligibleRow_ProbesExactlyOnce_AndRefreshShowsSshSource()
    {
        SeedSshFleet(_client);
        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1); // GpuBox auto-probe only
        var row = _viewModel.Rows.Single(r => r.Name == "Profiled");
        Assert.Equal("peer", row.HardwareSource);
        Assert.True(row.CanUpdateHardware);

        await _viewModel.UpdateHardwareCommand.ExecuteAsync(row);
        await WaitUntilAsync(() => _viewModel.Rows.Single(r => r.Name == "Profiled").HardwareSource == "ssh");

        // The explicit probe ran EXACTLY once (plus the GpuBox auto-probe from the refresh).
        Assert.Equal(["GpuBox", "Profiled"], [.. _client.ProbeCalls]);
        var refreshed = _viewModel.Rows.Single(r => r.Name == "Profiled");
        Assert.Equal("ssh", refreshed.HardwareSource); // the inspector shows the new source
        Assert.NotNull(refreshed.HardwareProfile);
        Assert.Equal("RTX 4090", refreshed.HardwareProfile!.Gpus[0].ModelName); // fake default
        Assert.True(refreshed.HardwareReachable);
        Assert.False(refreshed.IsProbing);
        Assert.Null(refreshed.ProbeError);
        Assert.Null(_viewModel.LastError);
    }

    [Fact]
    public async Task UpdateHardware_InFlight_AConcurrentRefreshRebuildsTheRowStillProbing()
    {
        SeedSshFleet(_client);
        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1);

        var gate = Gate();
        _client.ProbeFetchBlocks = AsQueue(gate.Task);
        var row = _viewModel.Rows.Single(r => r.Name == "Profiled");
        var rowReference = row;

        await _viewModel.UpdateHardwareCommand.ExecuteAsync(row);
        Assert.True(row.IsProbing);
        Assert.False(row.CanUpdateHardware);
        Assert.Equal("Probing…", row.UpdateHardwareButtonContent);

        // The 5 s poll REBUILDS the rows: the busy state must survive the rebuild (carried
        // by name, not on the row object), and NO second probe call may fire while in flight.
        await _viewModel.RefreshAsync();
        var rebuilt = _viewModel.Rows.Single(r => r.Name == "Profiled");
        Assert.NotSame(rowReference, rebuilt);
        Assert.True(rebuilt.IsProbing);
        Assert.False(rebuilt.CanUpdateHardware);
        Assert.Equal("Probing…", rebuilt.UpdateHardwareButtonContent);
        Assert.Equal(2, _client.ProbeCalls.Count); // dedupe: the in-flight probe is not re-fired

        gate.SetResult();
        await WaitUntilAsync(() => _viewModel.Rows.Single(r => r.Name == "Profiled").HardwareSource == "ssh");
        var settled = _viewModel.Rows.Single(r => r.Name == "Profiled");
        Assert.False(settled.IsProbing);
        Assert.True(settled.CanUpdateHardware);
        Assert.Equal("Update hardware", settled.UpdateHardwareButtonContent);
    }

    [Fact]
    public async Task UpdateHardware_Failure_SetsTheRowError_KeepsTheBannerQuiet_ClearsOnTheNextRefreshes()
    {
        SeedSshFleet(_client);
        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1);

        _client.ProbeFailTargets.Add("Profiled");
        var row = _viewModel.Rows.Single(r => r.Name == "Profiled");
        await _viewModel.UpdateHardwareCommand.ExecuteAsync(row);

        // The row shows the server's readable message; the tab-level banner stays quiet.
        await WaitUntilAsync(() => _viewModel.Rows.Single(r => r.Name == "Profiled").ProbeError is not null);
        Assert.Equal("simulated probe failure", _viewModel.Rows.Single(r => r.Name == "Profiled").ProbeError);
        Assert.Null(_viewModel.LastError);

        // The error survives exactly ONE more rebuild (carried over + consumed there)…
        await _viewModel.RefreshAsync();
        Assert.Equal(
            "simulated probe failure", _viewModel.Rows.Single(r => r.Name == "Profiled").ProbeError);
        // …and clears on the refresh after that (transient, no timer).
        await _viewModel.RefreshAsync();
        Assert.Null(_viewModel.Rows.Single(r => r.Name == "Profiled").ProbeError);
    }

    [Fact]
    public async Task UpdateHardware_Success_DropsThePendingErrorOfAPreviouslyFailedProbe()
    {
        SeedSshFleet(_client);
        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1); // GpuBox auto-probe settled

        // Explicit probe #1 fails → a transient error is pending (no refresh has consumed
        // it yet — the tests have no background poller)…
        _client.ProbeFailTargets.Add("Profiled");
        var row = _viewModel.Rows.Single(r => r.Name == "Profiled");
        await _viewModel.UpdateHardwareCommand.ExecuteAsync(row);
        await WaitUntilAsync(() => _viewModel.Rows.Single(r => r.Name == "Profiled").ProbeError is not null);

        // …and explicit probe #2 is fired while that stale error is still pending (gated in
        // flight). The success must DROP the pending error — otherwise the success refresh
        // would resurface a stale failure on a row that just probed fine.
        _client.ProbeFailTargets.Clear();
        var gate = Gate();
        _client.ProbeFetchBlocks = AsQueue(gate.Task);
        await _viewModel.UpdateHardwareCommand.ExecuteAsync(row);
        gate.SetResult();

        await WaitUntilAsync(() =>
            _client.ProbeCalls.Count == 3 &&
            _viewModel.Rows.Single(r => r.Name == "Profiled").HardwareSource == "ssh");

        var settled = _viewModel.Rows.Single(r => r.Name == "Profiled");
        Assert.Equal("ssh", settled.HardwareSource);
        Assert.Null(settled.ProbeError); // the stale failure did NOT resurface on the successful row
        Assert.False(settled.IsProbing);
        Assert.Null(_viewModel.LastError);
    }

    [Fact]
    public async Task AutoProbe_OnRefresh_FiresExactlyOnceForTheEligibleRow()
    {
        SeedSshFleet(_client);

        await _viewModel.RefreshAsync();

        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1);
        // Fires EXACTLY for ssh-probeable + no-profile rows: local / no-sshUser / openai /
        // already-profiled rows are never auto-probed.
        Assert.Equal(["GpuBox"], [.. _client.ProbeCalls]);

        // The probe's own success refresh is skipped by the single-writer gate while the
        // firing refresh holds the lock — the NEXT tick (as in production) picks up the
        // profile (source "ssh").
        await _viewModel.RefreshAsync();
        var gpuBox = _viewModel.Rows.Single(r => r.Name == "GpuBox");
        Assert.Equal("ssh", gpuBox.HardwareSource);
        Assert.NotNull(gpuBox.HardwareProfile);
        Assert.True(gpuBox.HardwareReachable);

        // A further refresh fires NO auto-probe (GpuBox is profiled now; the others were
        // never eligible).
        await _viewModel.RefreshAsync();
        Assert.Equal(1, _client.ProbeCalls.Count);
    }

    [Fact]
    public async Task AutoProbe_InFlight_ASecondRefreshIssuesNoSecondCall()
    {
        SeedSshFleet(_client);
        var gate = Gate();
        _client.ProbeFetchBlocks = AsQueue(gate.Task);

        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1);
        Assert.True(_viewModel.Rows.Single(r => r.Name == "GpuBox").IsProbing);

        // Tick 2 while the first probe is gated: deduped by the in-flight set — no second call.
        await _viewModel.RefreshAsync();
        Assert.Equal(1, _client.ProbeCalls.Count);
        Assert.True(_viewModel.Rows.Single(r => r.Name == "GpuBox").IsProbing); // carried by name

        gate.SetResult();
        await WaitUntilAsync(() => _viewModel.Rows.Single(r => r.Name == "GpuBox").HardwareSource == "ssh");
        Assert.False(_viewModel.Rows.Single(r => r.Name == "GpuBox").IsProbing);
    }

    [Fact]
    public async Task AutoProbe_Failure_IsSilentAndSuppressed_UntilAConfigEditLiftsIt()
    {
        SeedSshFleet(_client);
        _client.ProbeFailTargets.Add("GpuBox");

        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1);
        await WaitUntilAsync(() => !_viewModel.Rows.Any(r => r.IsProbing));

        // Silent: no row error, no tab banner…
        Assert.All(_viewModel.Rows, r => Assert.Null(r.ProbeError));
        Assert.Null(_viewModel.LastError);
        // …and suppressed (no retry storm against a dead host): further refreshes issue no
        // auto calls.
        await _viewModel.RefreshAsync();
        await _viewModel.RefreshAsync();
        Assert.Equal(1, _client.ProbeCalls.Count);

        // A successful config save of that target LIFTS the suppression → the next refresh
        // auto-probes again (the ssh credential may have been fixed).
        var row = _viewModel.Rows.Single(r => r.Name == "GpuBox");
        var form = _viewModel.CreateEditForm(row);
        var (request, _) = form.BuildUpdateRequest();
        _client.ProbeFailTargets.Clear();
        await _viewModel.EditTargetCommand.ExecuteAsync(("GpuBox", request));

        await WaitUntilAsync(() => _client.ProbeCalls.Count == 2);
        // The lifted auto-probe's success refresh is skipped while the edit's refresh holds
        // the gate — the next tick picks up the profile.
        await _viewModel.RefreshAsync();
        Assert.Equal("ssh", _viewModel.Rows.Single(r => r.Name == "GpuBox").HardwareSource);
    }

    [Fact]
    public async Task AutoProbe_ConfigCondition400Failure_StaysRetryable()
    {
        SeedSshFleet(_client);
        // A 400 config-condition failure (e.g. "no sshUser") is NOT suppressed — only
        // host-level failures (502/timeout) are.
        _client.ProbeException = new OllamaRouterApiException(
            System.Net.HttpStatusCode.BadRequest, "no sshUser configured for target GpuBox");

        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 1);
        await WaitUntilAsync(() => !_viewModel.Rows.Any(r => r.IsProbing));
        Assert.All(_viewModel.Rows, r => Assert.Null(r.ProbeError));

        // The next refresh fires AGAIN (suppression was not set) — and succeeds this time.
        await _viewModel.RefreshAsync();
        await WaitUntilAsync(() => _client.ProbeCalls.Count == 2);
        // The success refresh is skipped while this refresh holds the gate — the next tick
        // picks up the profile.
        await _viewModel.RefreshAsync();
        Assert.Equal("ssh", _viewModel.Rows.Single(r => r.Name == "GpuBox").HardwareSource);
        Assert.Equal(2, _client.ProbeCalls.Count);
    }

    [Fact]
    public async Task DeleteTargetApiFailure_BannerShowsHumanReadableMessage_NotRawJson()
    {
        // Task #146: the red banner must show the daemon's human-readable sentence carried
        // in the exception's parsed `Error` — never the raw `{"success": false, ...}` body.
        SeedFleet(_client);
        var humanMessage = "failed to persist target: the configuration file /tmp/cfg/config.json is not valid JSON even after ignoring // and /* */ comments and trailing commas — fix or rename the file and retry; no changes were saved.";
        _client.NextCallException = new OllamaRouterApiException(
            System.Net.HttpStatusCode.InternalServerError, humanMessage);

        await _viewModel.DeleteTargetAsync("Local");

        Assert.Equal(humanMessage, _viewModel.LastError);
        Assert.Contains("config.json", _viewModel.LastError);
        Assert.DoesNotContain("{\"", _viewModel.LastError);
        Assert.DoesNotContain("success\"", _viewModel.LastError);
    }

    [Fact]
    public async Task EditTargetApiFailure_BannerShowsHumanReadableMessage_NotRawJson()
    {
        SeedFleet(_client);
        var humanMessage = "failed to persist target: the configuration file /tmp/cfg/config.json is not valid JSON even after ignoring // and /* */ comments and trailing commas — fix or rename the file and retry; no changes were saved.";
        _client.NextCallException = new OllamaRouterApiException(
            System.Net.HttpStatusCode.InternalServerError, humanMessage);

        await _viewModel.EditTargetCommand.ExecuteAsync(("Local", new UpdateTargetRequest(Enabled: false)));

        Assert.Equal(humanMessage, _viewModel.LastError);
        Assert.DoesNotContain("{\"", _viewModel.LastError);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() => await _viewModel.DisposeAsync();
}
