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
    public async Task AddTarget_ProbeFailure_SurfacesTheError_AndDoesNotAddTheRow()
    {
        SeedFleet(_client);
        await _viewModel.RefreshAsync();

        // The dead shared-form add mode retired with the shared form (task #171): the
        // command-error-surface coverage stays form-free with a direct request.
        var request = new AddTargetRequest(
            Name: "Flaky",
            Url: "http://10.0.0.9:11434",
            Type: "ollama");

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

        // Task #171: the API key edit moved to the provider form (the old test used the
        // Local ollama row precisely because the shared dialog showed the key box for every
        // type) — OpenRouter is the seeded openai row with ApiKeyConfigured = true.
        var row = _viewModel.Rows.Single(r => r.Name == "OpenRouter");
        var form = _viewModel.CreateExternalProviderEditForm(row);
        Assert.True(form.ExistingApiKeyConfigured);
        form.MaxParallel = "3";

        var (request, error) = form.BuildUpdateRequest();
        Assert.Null(error);
        await _viewModel.EditTargetCommand.ExecuteAsync(("OpenRouter", request));

        var sent = Assert.Single(_client.UpdateTargetCalls);
        Assert.Equal("OpenRouter", sent.Name);
        Assert.Null(sent.Request.ApiKey); // untouched = NO CHANGE (absent on the wire)
        Assert.Equal(3, sent.Request.MaxParallel);
        Assert.Equal(3, _client.Targets["OpenRouter"].MaxParallel); // persisted by the store
    }

    [Fact]
    public async Task EditTarget_ApiKeyExplicitlyEmptied_SendsEmptyString_Clear()
    {
        SeedFleet(_client);
        await _viewModel.RefreshAsync();

        var row = _viewModel.Rows.Single(r => r.Name == "OpenRouter");
        Assert.True(row.ApiKeyConfigured);
        var form = _viewModel.CreateExternalProviderEditForm(row);
        form.ApiKey = "temp"; // touch the box…
        form.ApiKey = "";     // …then explicitly empty it → CLEAR
        var (request, _) = form.BuildUpdateRequest();

        await _viewModel.EditTargetCommand.ExecuteAsync(("OpenRouter", request));

        Assert.Equal(string.Empty, _client.UpdateTargetCalls.Single().Request.ApiKey);
        Assert.False(_client.Targets["OpenRouter"].ApiKeyConfigured); // cleared on the store
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
    public void CreateOllamaNodeEditForm_PrefillsSshUserAndSshPort_FromTheRow()
    {
        var row = new FleetTargetRow { Name = "GpuBox", SshUser = "gpuadmin", SshPort = 2222 };

        var form = _viewModel.CreateOllamaNodeEditForm(row);

        Assert.Equal("gpuadmin", form.SshUser);
        Assert.Equal("2222", form.SshPort);
        Assert.Equal("ssh: gpuadmin@(port 2222)", row.SshLabel);
    }

    [Fact]
    public void CreateOllamaNodeEditForm_BlankSshFields_StayBlank_AndBlankIsValid()
    {
        var row = new FleetTargetRow
        {
            Name = "Plain",
            Url = "http://10.0.0.8:11434/",
            ConfigMaxParallel = 1,
        };

        var form = _viewModel.CreateOllamaNodeEditForm(row);

        Assert.Equal(string.Empty, form.SshUser);
        Assert.Equal(string.Empty, form.SshPort);
        Assert.Null(form.BuildUpdateRequest().Error); // blank ssh fields are valid
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
        // Task #164: unified profile → the duplicated VRAM segment is dropped and the RAM
        // segment is marked " (unified)" (the 512 MB iGPU stays on the GPU line only).
        Assert.Equal("RAM: 91.2 GB (unified) · VRAM Speed: 341.2 GB/s", row.HardwareMemoryLine);
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
    public void Row_HardwareMemoryLine_DiscreteProfile_RendersExactlyAsTask153()
    {
        // Task #164: non-unified profiles keep the byte-identical RAM/VRAM/speed line.
        var row = new FleetTargetRow
        {
            Name = "DiscreteBox",
            HardwareProfile = new NodeHardwareProfile(
                "DiscreteBox", "linux", "x64", false, 65536, SystemRamType.Ddr5,
                [new GpuDeviceProfile("NVIDIA", "RTX 4080 SUPER", 16376, "GDDR6X", 256, 468)],
                16376, 468),
        };
        Assert.Equal("RAM: 64 GB · VRAM: 16 GB · VRAM Speed: 468 GB/s", row.HardwareMemoryLine);
    }

    [Fact]
    public void Row_HardwareMemoryLine_StrixHaloVerbatimProfile_IgpuVramDoesNotResurface()
    {
        // Task #164: the verbatim 2026-11-22 Strix Halo session profile (93379 MB LPDDR5X,
        // 341.2 GB/s, iGPU 512 MB) — the 512 MB iGPU must NOT resurface as a VRAM segment
        // on the memory line (it stays on the GPU line only).
        var row = new FleetTargetRow
        {
            Name = "StrixBox",
            HardwareSource = "persisted",
            HardwareProfile = new NodeHardwareProfile(
                "StrixBox", "linux", "x64", true, 93379, SystemRamType.LpDdr5X,
                [
                    new GpuDeviceProfile(
                        "AMD", "Strix Halo [Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics]",
                        512, "Unknown", 0, 341.2),
                ],
                93379, 341.2),
        };
        Assert.Equal("RAM: 91.2 GB (unified) · VRAM Speed: 341.2 GB/s", row.HardwareMemoryLine);
        // The iGPU VRAM is still visible on the GPU line.
        Assert.Equal(
            "GPU: AMD Strix Halo [Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics] (512 MB, 341.2 GB/s)",
            row.HardwareGpuLine);
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
    public async Task AddTarget_SshFields_CarryTheWireAndRoundTripToTheRow()
    {
        await _viewModel.RefreshAsync(); // empty fleet: no auto-probes

        // Task #171: the live UI add path is the dedicated Ollama-node form (the dead
        // shared-form add mode retired with the shared form).
        var form = _viewModel.CreateOllamaNodeAddForm();
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
        var form = _viewModel.CreateOllamaNodeEditForm(row);
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
        var form = _viewModel.CreateOllamaNodeEditForm(row);
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
        var form = _viewModel.CreateOllamaNodeEditForm(row);
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

    // -- Task #171: the dedicated per-type EDIT forms ----------------------------------

    private static FleetTargetRow FullOllamaRow() => new()
    {
        Name = "Local",
        Url = "http://127.0.0.1:11434/",
        Type = "ollama",
        ConfigMaxParallel = 2,
        HardwareHint = "RTX 4090",
        PowerWatts = 350,
        SshUser = "gpuadmin",
        SshPort = 2222,
        Models = [new TargetModelDto("llama3:8b", 8192, "Q4_K_M")],
        ApiKeyConfigured = true,
    };

    [Fact]
    public void CreateOllamaNodeEditForm_PrefillsAllFields_FromTheRow()
    {
        var form = _viewModel.CreateOllamaNodeEditForm(FullOllamaRow());

        Assert.Equal("Local", form.Name); // get-only identity = the row name
        Assert.Equal("http://127.0.0.1:11434/", form.Url);
        Assert.Equal("2", form.MaxParallel); // the PERSISTED budget, not the effective one
        Assert.Equal("RTX 4090", form.HardwareHint);
        Assert.Equal("350", form.PowerWatts);
        Assert.Equal("gpuadmin", form.SshUser);
        Assert.Equal("2222", form.SshPort);
    }

    [Fact]
    public void CreateOllamaNodeEditForm_NullRow_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _viewModel.CreateOllamaNodeEditForm(null!));
    }

    [Fact]
    public void OllamaEdit_ValidationMatrix_ByteIdenticalMessages()
    {
        var form = _viewModel.CreateOllamaNodeEditForm(FullOllamaRow());

        // The error surfaces on BuildUpdateRequest().Error (the view's single-pass flow
        // then calls ReportError, which lights HasValidationError — same contract as the
        // dedicated add forms).
        form.Url = "not-a-uri";
        var (urlError, urlMessage) = form.BuildUpdateRequest();
        Assert.Equal("The target URL must be an absolute http(s) URI.", urlMessage);
        form.ReportError(urlMessage);
        Assert.True(form.HasValidationError);

        form.Url = "http://127.0.0.1:11434";
        form.MaxParallel = "0";
        Assert.Equal("maxParallel must be an integer of 1 or greater.", form.BuildUpdateRequest().Error);
        form.MaxParallel = "abc";
        Assert.Equal("maxParallel must be an integer of 1 or greater.", form.BuildUpdateRequest().Error);

        form.MaxParallel = "2";
        form.PowerWatts = "-5";
        Assert.Equal("powerWatts must be a non-negative number (or blank).", form.BuildUpdateRequest().Error);
        form.PowerWatts = "watts";
        Assert.Equal("powerWatts must be a non-negative number (or blank).", form.BuildUpdateRequest().Error);

        form.PowerWatts = "";
        form.SshUser = "bad user"; // space
        Assert.Equal(
            "sshUser must be 1–64 characters of letters, digits, '.', '_' or '-' (or blank).",
            form.BuildUpdateRequest().Error);

        form.SshUser = "gpuadmin";
        form.SshPort = "0";
        Assert.Equal("sshPort must be a port between 1 and 65535 (or blank).", form.BuildUpdateRequest().Error);

        form.SshPort = "70000";
        Assert.Equal("sshPort must be a port between 1 and 65535 (or blank).", form.BuildUpdateRequest().Error);

        form.SshPort = "2222";
        var (request, error) = form.BuildUpdateRequest();
        Assert.Null(error);
        Assert.False(form.HasValidationError);
        Assert.NotNull(request);
    }

    [Fact]
    public void OllamaEdit_BuildUpdateRequest_NeverSendsTypeApiKeyProviderModelsEnabled()
    {
        var (request, error) = _viewModel.CreateOllamaNodeEditForm(FullOllamaRow()).BuildUpdateRequest();

        Assert.Null(error);
        Assert.Null(request.Enabled); // enablement is the row toggle
        Assert.Null(request.Type); // D3: the protocol family never changes from edit
        Assert.Null(request.ApiKey); // no key surface (a key on non-openai is a 400 anyway)
        Assert.Null(request.Provider); // no label surface
        Assert.Null(request.Models); // no roster surface — the row's roster is PRESERVED
    }

    [Fact]
    public void OllamaEdit_HardwareHintBlank_Clears_ValueSendsTrimmed()
    {
        var form = _viewModel.CreateOllamaNodeEditForm(FullOllamaRow());
        form.HardwareHint = string.Empty; // D1: blanking a pre-filled value CLEARS it
        var (blanked, blankedError) = form.BuildUpdateRequest();
        Assert.Null(blankedError);
        Assert.Equal(string.Empty, blanked.HardwareHint); // "" = CLEAR on the wire

        form.HardwareHint = "  new hint  ";
        var (value, valueError) = form.BuildUpdateRequest();
        Assert.Null(valueError);
        Assert.Equal("new hint", value.HardwareHint); // trimmed
    }

    [Fact]
    public void OllamaEdit_SshUserBlank_Clears_ValueSends()
    {
        var form = _viewModel.CreateOllamaNodeEditForm(FullOllamaRow());
        form.SshUser = string.Empty; // the task #144 convention: blank = "" (CLEAR)
        var (blanked, blankedError) = form.BuildUpdateRequest();
        Assert.Null(blankedError);
        Assert.Equal(string.Empty, blanked.SshUser);

        form.SshUser = "  root  ";
        var (value, valueError) = form.BuildUpdateRequest();
        Assert.Null(valueError);
        Assert.Equal("root", value.SshUser); // trimmed
    }

    [Fact]
    public void OllamaEdit_SshPortBlank_NoChange_ValueSends()
    {
        var form = _viewModel.CreateOllamaNodeEditForm(FullOllamaRow());
        form.SshPort = string.Empty; // the port wire cannot clear → null (NO CHANGE)
        var (blanked, blankedError) = form.BuildUpdateRequest();
        Assert.Null(blankedError);
        Assert.Null(blanked.SshPort);

        form.SshPort = "22";
        var (value, valueError) = form.BuildUpdateRequest();
        Assert.Null(valueError);
        Assert.Equal(22, value.SshPort);
    }

    [Fact]
    public void OllamaEdit_PowerWattsBlank_NoChange_ValueSends()
    {
        var form = _viewModel.CreateOllamaNodeEditForm(FullOllamaRow());
        form.PowerWatts = string.Empty; // no wire clear path → null (NO CHANGE)
        var (blanked, blankedError) = form.BuildUpdateRequest();
        Assert.Null(blankedError);
        Assert.Null(blanked.PowerWatts);

        form.PowerWatts = "125.5";
        var (value, valueError) = form.BuildUpdateRequest();
        Assert.Null(valueError);
        Assert.Equal(125.5, value.PowerWatts);
    }

    [Fact]
    public async Task OllamaEdit_Valid_SendsNormalizedUrlAndParsedMaxParallel_RoundTrips()
    {
        // Seed the store (the fake 404s an update for an unknown target) — a LOCAL ollama
        // target, so it is never ssh-probeable and no auto-probes fire.
        _client.SeedTarget(new RouterTargetConfigDto(
            "Local", "http://127.0.0.1:11434/", true, "RTX 4090", 2, "ollama", null,
            [new TargetModelDto("llama3:8b", 8192, "Q4_K_M")], true, 350));
        _client.FleetResponse = new RouterFleetResponse(
        [
            new RouterTargetItem("Local", "http://127.0.0.1:11434/", true, "Online", 12, "local",
                new RouterTargetHardware("CPU", [], null, 1, 1.0),
                new RouterTargetExecution(0, null, "-"), null),
        ]);
        await _viewModel.RefreshAsync();

        var row = _viewModel.Rows.Single(r => r.Name == "Local");
        var form = _viewModel.CreateOllamaNodeEditForm(row);
        form.Url = "http://127.0.0.1:11435/";
        form.MaxParallel = "3";
        var (request, error) = form.BuildUpdateRequest();
        Assert.Null(error);
        Assert.Equal("http://127.0.0.1:11435", request.Url); // trailing / stripped
        Assert.Equal(3, request.MaxParallel);

        await _viewModel.EditTargetCommand.ExecuteAsync(("Local", request));

        var sent = Assert.Single(_client.UpdateTargetCalls);
        Assert.Equal("Local", sent.Name);
        Assert.Equal(3, sent.Request.MaxParallel);
        Assert.Equal("http://127.0.0.1:11435", _client.Targets["Local"].Url);
    }

    [Fact]
    public void CreateExternalProviderEditForm_PrefillsAllFields_FromTheRow()
    {
        // The OpenRouter shape (openai + provider label + key + roster) built directly —
        // the same values SeedFleet uses for the fleet rows.
        var row = new FleetTargetRow
        {
            Name = "OpenRouter",
            Url = "https://openrouter.ai/api",
            Type = "openai",
            Provider = "OpenRouter",
            ConfigMaxParallel = 1,
            ApiKeyConfigured = true,
            Models = [new TargetModelDto("openrouter/gpt-4o-mini")],
        };

        var form = _viewModel.CreateExternalProviderEditForm(row);

        Assert.Equal("OpenRouter", form.Name); // get-only identity
        Assert.Equal("https://openrouter.ai/api", form.BaseUrl);
        Assert.Equal("1", form.MaxParallel);
        Assert.Equal("OpenRouter", form.Provider);
        Assert.True(form.ExistingApiKeyConfigured);
        Assert.Equal(
            "A key is stored — leave blank to keep it, or enter a new key to replace it.",
            form.ApiKeyStatusText);
        var entry = Assert.Single(form.Models);
        Assert.Equal("openrouter/gpt-4o-mini", entry.Name);
        Assert.Equal(string.Empty, entry.ContextLength); // null → blank
        Assert.Equal(string.Empty, entry.Quantization);
    }

    [Fact]
    public void CreateExternalProviderEditForm_NullRow_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _viewModel.CreateExternalProviderEditForm(null!));
    }

    private static ExternalProviderEditFormViewModel ProviderForm(FleetTargetsViewModel vm) =>
        vm.CreateExternalProviderEditForm(new FleetTargetRow
        {
            Name = "OpenRouter",
            Url = "https://openrouter.ai/api",
            Type = "openai",
            Provider = "OpenRouter",
            ConfigMaxParallel = 1,
            ApiKeyConfigured = true,
            Models = [new TargetModelDto("openrouter/gpt-4o-mini", 128000, null)],
        });

    [Fact]
    public void ProviderEdit_BadBaseUrl_Rejects()
    {
        var form = ProviderForm(_viewModel);
        form.BaseUrl = string.Empty; // blank → "http://" → not an absolute http(s) URI
        Assert.Equal("The target URL must be an absolute http(s) URI.", form.BuildUpdateRequest().Error);
    }

    [Fact]
    public void ProviderEdit_BareHostPort_NormalizesToHttp()
    {
        var form = ProviderForm(_viewModel);
        form.BaseUrl = "api.groq.com:443";
        var (request, error) = form.BuildUpdateRequest();
        Assert.Null(error);
        Assert.Equal("http://api.groq.com:443", request.Url);
    }

    [Fact]
    public void ProviderEdit_BadMaxParallel_Rejects()
    {
        var form = ProviderForm(_viewModel);
        form.MaxParallel = "0";
        Assert.Equal("maxParallel must be an integer of 1 or greater.", form.BuildUpdateRequest().Error);
    }

    [Fact]
    public void ProviderEdit_RosterBlankName_Rejects()
    {
        var form = ProviderForm(_viewModel);
        form.Models[0].Name = "  ";
        Assert.Equal("Every roster model needs a name.", form.BuildUpdateRequest().Error);
    }

    [Fact]
    public void ProviderEdit_RosterBadContextLength_Rejects()
    {
        var form = ProviderForm(_viewModel);
        form.Models[0].ContextLength = "-1";
        Assert.Equal(
            "Model \"openrouter/gpt-4o-mini\": contextLength must be a positive integer (or blank).",
            form.BuildUpdateRequest().Error);
        form.Models[0].ContextLength = "abc";
        Assert.Equal(
            "Model \"openrouter/gpt-4o-mini\": contextLength must be a positive integer (or blank).",
            form.BuildUpdateRequest().Error);
    }

    [Fact]
    public void ProviderEdit_ApiKeyUntouched_SendsNull()
    {
        var (request, error) = ProviderForm(_viewModel).BuildUpdateRequest();

        Assert.Null(error);
        Assert.Null(request.ApiKey); // untouched = the stored key is KEPT
    }

    [Fact]
    public void ProviderEdit_ApiKeyEmptied_SendsEmptyString()
    {
        var form = ProviderForm(_viewModel);
        form.ApiKey = "temp"; // touch the box…
        form.ApiKey = "";     // …then explicitly empty it → CLEAR
        var (request, error) = form.BuildUpdateRequest();

        Assert.Null(error);
        Assert.Equal(string.Empty, request.ApiKey); // "" = CLEAR
        Assert.True(form.IsApiKeyTouched);
    }

    [Fact]
    public void ProviderEdit_ApiKeyReplaced_SendsTheValue()
    {
        var form = ProviderForm(_viewModel);
        form.ApiKey = "  new-key  ";
        var (request, error) = form.BuildUpdateRequest();

        Assert.Null(error);
        Assert.Equal("new-key", request.ApiKey); // trimmed = REPLACE
    }

    [Fact]
    public void ProviderEdit_ProviderBlank_Clears_ValueSendsTrimmed()
    {
        var form = ProviderForm(_viewModel);
        form.Provider = string.Empty; // D1: blanking the pre-filled label CLEARS it
        var (blanked, blankedError) = form.BuildUpdateRequest();
        Assert.Null(blankedError);
        Assert.Equal(string.Empty, blanked.Provider);

        form.Provider = "  Groq  ";
        var (value, valueError) = form.BuildUpdateRequest();
        Assert.Null(valueError);
        Assert.Equal("Groq", value.Provider);
    }

    [Fact]
    public void ProviderEdit_RosterUnchanged_RoundTripsEntries()
    {
        var (request, error) = ProviderForm(_viewModel).BuildUpdateRequest();

        Assert.Null(error);
        var model = Assert.Single(request.Models!);
        Assert.Equal("openrouter/gpt-4o-mini", model.Name);
        Assert.Equal(128000, model.ContextLength);
        Assert.Null(model.Quantization); // blank → null per entry
    }

    [Fact]
    public void ProviderEdit_AllRowsRemoved_SendsEmptyList_Clears()
    {
        var form = ProviderForm(_viewModel); // the row HAD a roster at open (D2)
        form.RemoveModelRowCommand.Execute(form.Models[0]);
        Assert.Empty(form.Models);

        var (request, error) = form.BuildUpdateRequest();

        Assert.Null(error);
        Assert.NotNull(request.Models); // NOT null — a provided empty list CLEARS the roster
        Assert.Empty(request.Models);
    }

    [Fact]
    public void ProviderEdit_NeverHadRoster_NoRowsAdded_SendsNull()
    {
        var form = _viewModel.CreateExternalProviderEditForm(new FleetTargetRow
        {
            Name = "Plain",
            Url = "https://api.example.com/v1",
            Type = "openai",
            ConfigMaxParallel = 1,
        }); // no roster at open, none added → NO CHANGE (no config.json diff on an untouched save)

        var (request, error) = form.BuildUpdateRequest();

        Assert.Null(error);
        Assert.Null(request.Models);
    }

    [Fact]
    public void ProviderEdit_NeverHadRoster_RowsAdded_SendsTheList()
    {
        var form = _viewModel.CreateExternalProviderEditForm(new FleetTargetRow
        {
            Name = "Plain",
            Url = "https://api.example.com/v1",
            Type = "openai",
            ConfigMaxParallel = 1,
        });
        form.AddModelRowCommand.Execute(null);
        form.Models[0].Name = "some-model";

        var (request, error) = form.BuildUpdateRequest();

        Assert.Null(error);
        var model = Assert.Single(request.Models!);
        Assert.Equal("some-model", model.Name);
    }

    [Fact]
    public void ProviderEdit_NeverSendsTypePowerWattsHardwareHintSsh()
    {
        var (request, error) = ProviderForm(_viewModel).BuildUpdateRequest();

        Assert.Null(error);
        Assert.Null(request.Enabled);
        Assert.Null(request.Type); // D3
        Assert.Null(request.PowerWatts); // fields not offered → NO CHANGE
        Assert.Null(request.HardwareHint);
        Assert.Null(request.SshUser);
        Assert.Null(request.SshPort);
    }

    [Fact]
    public void ProviderEdit_RosterRowCommands_AddAndRemove()
    {
        var form = ProviderForm(_viewModel);

        form.AddModelRowCommand.Execute(null);
        Assert.Equal(2, form.Models.Count);
        Assert.Equal(string.Empty, form.Models[1].Name);
        Assert.Equal(string.Empty, form.Models[1].ContextLength);
        Assert.Equal(string.Empty, form.Models[1].Quantization);

        var target = form.Models[0];
        form.RemoveModelRowCommand.Execute(target);
        Assert.DoesNotContain(target, form.Models);
        form.RemoveModelRowCommand.Execute(null); // null-safe no-op
        Assert.Single(form.Models);
    }

    [Fact]
    public async Task ProviderEdit_RoundTripsThroughTheStore()
    {
        // Seed the store (the fake 404s an update for an unknown target) — no fleet entries,
        // so no auto-probes.
        _client.SeedTarget(new RouterTargetConfigDto(
            "OpenRouter", "https://openrouter.ai/api", true, null, 1, "openai", "OpenRouter",
            [new TargetModelDto("openrouter/gpt-4o-mini", 128000, null)], true, null));
        await _viewModel.RefreshAsync();

        var form = ProviderForm(_viewModel);
        form.BaseUrl = "https://openrouter.ai/api";
        form.ApiKey = "new-key";
        form.Models[0].Name = "openrouter/gpt-5-mini";
        form.Models[0].Quantization = "FP8";
        var (request, error) = form.BuildUpdateRequest();
        Assert.Null(error);

        await _viewModel.EditTargetCommand.ExecuteAsync(("OpenRouter", request));

        var sent = Assert.Single(_client.UpdateTargetCalls);
        Assert.Equal("OpenRouter", sent.Name);
        Assert.Equal("new-key", sent.Request.ApiKey);
        Assert.Null(sent.Request.Type); // the protocol family was never sent
        var model = Assert.Single(sent.Request.Models!);
        Assert.Equal("openrouter/gpt-5-mini", model.Name);
        Assert.Equal("FP8", model.Quantization);
        // The store keeps only the configured flag (key material never persisted in the fake).
        Assert.True(_client.Targets["OpenRouter"].ApiKeyConfigured);
    }

    // -- Task #171: the edit-dispatch routing (FleetTargetsView predicate) -----------

    [Theory]
    [InlineData("openai", true)]
    [InlineData("OpenAI", true)]
    [InlineData("OLLAMA", false)] // case-insensitive match ONLY for openai
    [InlineData("ollama", false)]
    [InlineData("OSNATIVE", false)]
    [InlineData("custom", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public static void UseExternalProviderEditDialog_RoutesOnlyOpenai_CaseInsensitive(
        string? type, bool expected)
    {
        Assert.Equal(expected, OllamaRouter.App.Views.FleetTargetsView.UseExternalProviderEditDialog(type));
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() => await _viewModel.DisposeAsync();
}
