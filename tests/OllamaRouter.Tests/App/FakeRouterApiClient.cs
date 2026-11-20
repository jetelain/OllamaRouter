using System.Net;
using OllamaRouter.Client;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Endpoints.RouterApi.Preload;
using OllamaRouter.Endpoints.RouterApi.Recommendations;
using OllamaRouter.Endpoints.RouterApi.SessionAffinity;
using OllamaRouter.Endpoints.RouterApi.Sessions;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Endpoints.RouterApi.Vram;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Recommendations;

namespace OllamaRouter.Tests.App;

/// <summary>
/// In-memory <see cref="IOllamaRouterApiClient"/> double for the desktop-app view-model
/// tests (task #102): every endpoint the app uses is backed by mutable in-memory state,
/// every call is logged, and the target CRUD behaviors (name/type validation, the
/// <c>apiKey</c> no-change/clear semantics, 400/404/409 failures, the one-time raw
/// API-key capture) mirror the server contract. NO network is touched.
/// </summary>
internal sealed class FakeRouterApiClient : IOllamaRouterApiClient, IDisposable
{
    private bool _disposed;

    /// <summary>Whether <see cref="Dispose"/> was called (the app disposes clients on
    /// switch/dispose; the assertion surface for that behavior).</summary>
    public bool Disposed => _disposed;

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
    private readonly object _gate = new();
    private readonly Dictionary<string, RouterTargetConfigDto> _targets =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ordered log of every invoked method (test assertion surface).</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The ordered log of <c>PullModelAsync</c> calls (model, target, aliasContext).
    /// (task #125 — the install wizard's per-target pull assertions.)</summary>
    public List<(string Model, string? Target, int? AliasContext)> PullCalls { get; } = [];

    /// <summary>The result of <c>PullModelAsync</c> (null = default success: the alias is
    /// computed from the reference + context via <see cref="ModelPullNaming"/>).</summary>
    public OllamaRouterModelPullResult? PullResult { get; set; }

    /// <summary>An exception thrown by <c>PullModelAsync</c> (pre-stream 400/404/409 or
    /// transport failures).</summary>
    public Exception? PullException { get; set; }

    /// <summary>Targets whose pull returns a FAILED result (in-stream error — Succeeded
    /// false + error text, NO exception): the "one failure does not stop the rest" test.</summary>
    public HashSet<string> PullFailTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The error text of the <see cref="PullFailTargets"/> failed result.</summary>
    public string PullFailError { get; set; } = "simulated pull failure";

    /// <summary>The progress events emitted BEFORE the result on every <c>PullModelAsync</c>
    /// call (the wizard's live-row assertions).</summary>
    public List<OllamaRouterModelPullProgress> PullProgressEvents { get; } = [];

    /// <summary>A simulated pull duration (ms); while the fake is "in flight" a cancelled
    /// token makes <c>PullModelAsync</c> throw <c>OperationCanceledException</c> (the
    /// in-flight cancellation test).</summary>
    public int PullDelayMs { get; set; }

    /// <summary>The one-shot exception thrown by the NEXT call of any method (then cleared).</summary>
    public Exception? NextCallException { get; set; }

    // -- Scriptable state -------------------------------------------------------

    /// <summary>The <c>GET /api/router/info</c> response.</summary>
    public RouterInfoResponse? InfoResponse { get; set; }

    /// <summary>A STICKY <c>GET /api/router/info</c> failure (thrown on every info call until
    /// cleared — for deterministic 401 key-locked scripting).</summary>
    public OllamaRouterApiException? InfoException { get; set; }

    /// <summary>The <c>GET /monitor/api</c> state (empty by default).</summary>
    public MonitorStateResponse MonitorState { get; set; } = new(
        false,
        Array.Empty<MonitorFleetTargetItem>(),
        new MonitorStatisticsStatus(
            new Dictionary<string, OllamaRouter.Services.Monitoring.ActivityStatisticsTotals>(),
            new Dictionary<string, OllamaRouter.Services.Monitoring.ActivityStatisticsTotals>()),
        Array.Empty<MonitorInProgressItem>(),
        Array.Empty<MonitorRecentRequestItem>());

    /// <summary>The <c>GET /api/router/fleet</c> rows (empty by default).</summary>
    public RouterFleetResponse FleetResponse { get; set; } = new([]);

    /// <summary>FIFO override of the <c>GET /api/router/fleet</c> payloads (task #130 — the
    /// install wizard's re-entrancy regression tests): while non-empty, each call DEQUEUES
    /// and serves the next item (per-call distinct payloads); otherwise the scripted
    /// <see cref="FleetResponse"/> (the existing behavior).</summary>
    public Queue<RouterFleetResponse>? FleetResponses { get; set; }

    /// <summary>Per-call delay gates for <c>GET /api/router/fleet</c> (task #130 — mirrors the
    /// <see cref="UninstallBlock"/> / <see cref="PullDelayMs"/> precedent): while non-empty, each
    /// call DEQUEUES and awaits the next task BEFORE returning (making the fetch in-flight
    /// so concurrent step entries overlap deterministically); otherwise no delay.</summary>
    public Queue<Task>? FleetFetchBlocks { get; set; }

    // -- Daemon-side hardware probe scripting (task #144) ------------------------

    /// <summary>Per-call delay gates for <c>ProbeTargetHardwareAsync</c> (task #144 — mirrors
    /// <see cref="FleetFetchBlocks"/>): while non-empty, each call DEQUEUES and awaits the next
    /// task BEFORE returning (keeping the probe in flight so the busy-state carry-over can
    /// be observed across row rebuilds); otherwise no delay.</summary>
    public Queue<Task>? ProbeFetchBlocks { get; set; }

    /// <summary>The one-shot exception thrown by the NEXT <c>ProbeTargetHardwareAsync</c> call
    /// (then cleared; mirrors the <see cref="PullException"/> precedent).</summary>
    public Exception? ProbeException { get; set; }

    /// <summary>Targets whose probe FAILS (→ 502 <see cref="OllamaRouterApiException"/> with
    /// <see cref="ProbeFailError"/>; the hardware store is NOT written — mirrors the server,
    /// where the store is written only after a successful probe).</summary>
    public HashSet<string> ProbeFailTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The error text of a <see cref="ProbeFailTargets"/> probe failure.</summary>
    public string ProbeFailError { get; set; } = "simulated probe failure";

    /// <summary>Per-target probe result overrides (default: a synthetic RTX 4090 profile).</summary>
    public Dictionary<string, NodeHardwareProfile> ProbeProfiles { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>GET /api/router/fleet/hardware</c> rows (empty by default).</summary>
    public RouterFleetHardwareResponse HardwareResponse { get; set; } = new([]);

    /// <summary>Sticky <c>GET /api/router/fleet/hardware</c> failure (thrown on every hardware
    /// call until cleared — the wizard treats a failed hardware fetch as "unknown state
    /// unknown" → no auto-probes; mirrors the <see cref="InfoException"/> precedent).</summary>
    public OllamaRouterApiException? HardwareException { get; set; }

    /// <summary>The <c>GET /api/router/benchmark/results</c> response (empty by default —
    /// task #125: the install wizard's assisted mode reads it best-effort).</summary>
    public RouterBenchmarkResultsResponse BenchmarkResults { get; set; } = new([]);

    /// <summary>The <c>GET /api/router/models/{name}</c> response (null by default — the
    /// fake then throws the 404 <c>model not found</c>, the NORMAL first-install case the
    /// install wizard treats as a hint, not an error).</summary>
    public RouterModelDetailResponse? ModelDetailResponse { get; set; }

    /// <summary>Override of the <c>GET /api/router/models/{name}</c> failure (thrown when set).</summary>
    public OllamaRouterApiException? ModelDetailException { get; set; }

    /// <summary>The <c>GET /api/router/config</c> configuration (empty by default).</summary>
    public RouterConfigResponse ConfigResponse { get; set; } = new(
        [],
        new RouterCloudConfigDto(false, null, "Local", false),
        new RouterDynamicContextConfigDto(true, true, 10, 300),
        new RouterRecordingConfigDto(false, null),
        new RouterResilienceConfigDto(3, 4000),
        new RouterPricingConfigDto(null, null, null, "$", null, null, null),
        new ApiSecurityConfigDto(false, 24));

    /// <summary>The <c>GET /api/router/cartography</c> response (empty by default).</summary>
    public RouterCartographyResponse CartographyResponse { get; set; } = new([], [], []);

    /// <summary>The <c>POST /api/router/vram/reclaim</c> response (default: nothing unloaded).</summary>
    public RouterVramReclaimResponse ReclaimResponse { get; set; } = new(true, 0, [], null);

    /// <summary>The <c>GET /api/router/monitor/sessions</c> response (empty store by default).</summary>
    public RouterSessionUsageListResponse SessionsResponse { get; set; } = new(null, 0, []);

    /// <summary>The <c>GET /api/router/monitor/sessions/{sessionId}</c> detail store (an unknown
    /// session id ⇒ 404 <see cref="OllamaRouterApiException"/>, mirroring the wire).</summary>
    public Dictionary<string, RouterSessionUsageDetailResponse> SessionDetails { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>GET /api/router/preload/preview</c> +
    /// <c>POST .../preload</c> response (default: no selection).</summary>
    public RouterModelPreloadResponse PreloadResponse { get; set; } = new("model", null, 900, null, null, null, null, null, [], null);

    /// <summary>Scripted add-target failure (400/409/422 → <see cref="OllamaRouterApiException"/>).</summary>
    public (HttpStatusCode Status, string Error)? UninstallError { get; set; }

    /// <summary>If set, uninstall calls await this before responding (busy-guard scripting).</summary>
    public Task? UninstallBlock { get; set; }

    public RouterModelUninstallResponse UninstallResponse { get; set; } =
        new("model", []);

    public (HttpStatusCode Status, string Error)? AddTargetError { get; set; }

    /// <summary>Scripted <c>PUT /api/router/config</c> failure.</summary>
    public (HttpStatusCode Status, string Error)? UpdateConfigError { get; set; }

    /// <summary>The <c>GET /api/router/recommendations</c> matrix (stable empty shape by default).</summary>
    public FleetRecommendations Recommendations { get; set; } = new(
        null, new Dictionary<string, RoleRecommendation>(), new Dictionary<string, ModelNodeProfile>());

    /// <summary>Scripted <c>POST /api/router/recommendations/generate</c> 200 response (served when
    /// <see cref="GenerateError"/> is null; when null a generated default matrix is returned).</summary>
    public RouterRecommendationsGeneratedResponse? GenerateResponse { get; set; }

    /// <summary>Scripted <c>POST /api/router/recommendations/generate</c> failure (e.g. 409 no benchmark data).</summary>
    public (HttpStatusCode Status, string Error)? GenerateError { get; set; }

    /// <summary>Scripted <c>POST /api/router/recommendations/apply</c> 200 response (served when
    /// <see cref="ApplyError"/> is null; when null an applied default is returned).</summary>
    public RouterRecommendationsAppliedResponse? ApplyResponse { get; set; }

    /// <summary>Scripted <c>POST /api/router/recommendations/apply</c> failure (e.g. 409 no recommendations file).</summary>
    public (HttpStatusCode Status, string Error)? ApplyError { get; set; }

    // -- Captured calls ----------------------------------------------------------

    /// <summary>Every <c>POST /targets</c> flag map, in call order.</summary>
    public List<IReadOnlyDictionary<string, bool>> AvailabilityCalls { get; } = [];

    /// <summary>Every <c>POST /api/router/targets</c> request, in call order.</summary>
    public List<AddTargetRequest> AddTargetCalls { get; } = [];

    /// <summary>Every <c>POST /api/router/fleet/hardware/{target}/probe</c> target name, in
    /// call order (task #144).</summary>
    public List<string> ProbeCalls { get; } = [];

    /// <summary>Every <c>PUT /api/router/targets/{name}</c> (name, request) pair.</summary>
    public List<(string Name, UpdateTargetRequest Request)> UpdateTargetCalls { get; } = [];

    /// <summary>Every <c>DELETE /api/router/targets/{name}</c> name.</summary>
    public List<string> DeleteTargetCalls { get; } = [];

    /// <summary>Every <c>PUT /api/router/config</c> request, in call order.</summary>
    public List<UpdateRouterConfigRequest> UpdateConfigCalls { get; } = [];

    /// <summary>Every preload PREVIEW call (model, keepAlive, intent, target).</summary>
    public List<(string Model, int? KeepAliveSeconds, int? ContextIntentTokens, string? Target)> PreviewCalls { get; } = [];

    /// <summary>Every preload PRELOAD call (model, request).</summary>
    public List<(string Model, RouterModelPreloadRequest? Request)> PreloadCalls { get; } = [];

    /// <summary>Every uninstall call (model, targets in request order; task #126).</summary>
    public List<(string Model, IReadOnlyList<string> Targets)> UninstallCalls { get; } = [];

    /// <summary>Every VRAM-reclaim request, in call order.</summary>
    public List<RouterVramReclaimRequest?> ReclaimCalls { get; } = [];

    /// <summary>Every created API key (the raw key is captured EXACTLY ONCE, like the wire).</summary>
    public List<ApiKeyCreatedResponse> CreatedKeys { get; } = [];

    /// <summary>Every default-key rotation (the new raw key).</summary>
    public List<ApiKeyRotatedResponse> RotatedKeys { get; } = [];

    /// <summary>Captured <c>POST /api/router/recommendations/generate</c> calls (call-order record).</summary>
    public List<RouterRecommendationsGeneratedResponse> GenerateCalls { get; } = [];

    /// <summary>Captured <c>POST /api/router/recommendations/apply</c> calls (call-order record).</summary>
    public List<RouterRecommendationsAppliedResponse> ApplyCalls { get; } = [];

    /// <summary>The masked user-key store (the raw keys are NEVER retained).</summary>
    public List<ApiKeyInfoResponse> ApiKeys { get; } = [];

    // -- Target store helpers ----------------------------------------------------

    /// <summary>Adds a target row to the in-memory store (test setup helper).</summary>
    public void SeedTarget(RouterTargetConfigDto target) =>
        _targets[target.Name] = target;

    /// <summary>The current in-memory target store (config order preserved by
    /// <see cref="ConfigResponse"/>; this exposes the CRUD side).</summary>
    public IReadOnlyDictionary<string, RouterTargetConfigDto> Targets => _targets;

    /// <summary>The probe-derived hardware store (task #144 — mirrors the daemon's
    /// targets-hardware.json): target → (profile, "ssh"); written ONLY after a successful
    /// probe; <see cref="GetFleetHardwareAsync"/> merges it into the scripted
    /// <see cref="HardwareResponse"/> (an empty store ⇒ today's byte-identical behavior, so
    /// existing tests are unaffected).</summary>
    private readonly Dictionary<string, (NodeHardwareProfile Profile, string Source)> _hardwareStore =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The probe-derived hardware store (assertion surface).</summary>
    public IReadOnlyDictionary<string, (NodeHardwareProfile Profile, string Source)> HardwareStore =>
        _hardwareStore;

    // ---------------------------------------------------------------------------

    private void Log(string method)
    {
        lock (_gate)
        {
            Calls.Add(method);
            if (NextCallException is { } exception)
            {
                NextCallException = null;
                throw exception;
            }
        }
    }

    /// <inheritdoc />
    public Task<RouterInfoResponse> GetInfoAsync(CancellationToken ct = default)
    {
        Log(nameof(GetInfoAsync));
        if (InfoException is { } infoException)
        {
            throw infoException;
        }

        return InfoResponse is { } info
            ? Task.FromResult(info)
            : Task.FromException<RouterInfoResponse>(new OllamaRouterApiException(
                HttpStatusCode.NotFound, "no info configured on the fake"));
    }

    /// <inheritdoc />
    public Task<RouterShutdownResponse> ShutdownAsync(CancellationToken ct = default) =>
        NotImplemented<RouterShutdownResponse>(nameof(ShutdownAsync));

    /// <inheritdoc />
    public Task<MonitorStateResponse> GetMonitorStateAsync(CancellationToken ct = default)
    {
        Log(nameof(GetMonitorStateAsync));
        return Task.FromResult(MonitorState);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, bool>> UpdateTargetAvailabilityAsync(
        IReadOnlyDictionary<string, bool> flags, CancellationToken ct = default)
    {
        Log(nameof(UpdateTargetAvailabilityAsync));
        AvailabilityCalls.Add(flags);
        return Task.FromResult<IReadOnlyDictionary<string, bool>>(flags);
    }

    /// <inheritdoc />
    public async Task<RouterFleetResponse> GetFleetAsync(CancellationToken ct = default)
    {
        Log(nameof(GetFleetAsync));
        // Dequeue BOTH the delay gate and the payload at CALL time (FIFO per call — not per
        // completion order): concurrent calls must keep their scripted correspondence even
        // when a slower call finishes LAST.
        Task? block = FleetFetchBlocks is { Count: > 0 } ? FleetFetchBlocks.Dequeue() : null;
        RouterFleetResponse response = FleetResponses is { Count: > 0 }
            ? FleetResponses.Dequeue()
            : FleetResponse;
        if (block is not null)
        {
            await block;
        }

        return response;
    }

    /// <inheritdoc />
    public Task<RouterFleetHardwareResponse> GetFleetHardwareAsync(CancellationToken ct = default)
    {
        Log(nameof(GetFleetHardwareAsync));
        if (HardwareException is { } hardwareException)
        {
            throw hardwareException;
        }

        lock (_gate)
        {
            // An empty store ⇒ byte-identical to the scripted response (existing behavior).
            if (_hardwareStore.Count == 0)
            {
                return Task.FromResult(HardwareResponse);
            }

            // Mirror the server's probe → store → GET flow: nodes already in the scripted
            // response are REPLACED by the stored entry (source "ssh", reachable), store-only
            // targets are appended.
            var nodes = HardwareResponse.Nodes.ToList();
            foreach (var (target, (profile, source)) in _hardwareStore)
            {
                var merged = new FleetHardwareNode(target, source, profile, true);
                var index = nodes.FindIndex(n => string.Equals(n.Target, target, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    nodes[index] = merged;
                }
                else
                {
                    nodes.Add(merged);
                }
            }

            return Task.FromResult(new RouterFleetHardwareResponse(nodes));
        }
    }

    /// <inheritdoc />
    public Task<NodeHardwareProfile> UpdateTargetHardwareAsync(
        string target, NodeHardwareProfile profile, CancellationToken ct = default) =>
        NotImplemented<NodeHardwareProfile>(nameof(UpdateTargetHardwareAsync));

    /// <inheritdoc />
    public async Task<NodeHardwareProfile> ProbeTargetHardwareAsync(string target, CancellationToken ct = default)
    {
        Log(nameof(ProbeTargetHardwareAsync));
        ProbeCalls.Add(target);

        Task? block = ProbeFetchBlocks is { Count: > 0 } ? ProbeFetchBlocks.Dequeue() : null;
        if (block is not null)
        {
            await block;
        }

        if (ProbeException is { } probeException)
        {
            ProbeException = null;
            throw probeException;
        }

        if (ProbeFailTargets.Contains(target))
        {
            throw new OllamaRouterApiException(HttpStatusCode.BadGateway, ProbeFailError);
        }

        // The store is written ONLY after a successful probe (mirrors the server).
        var profile = ProbeProfiles.TryGetValue(target, out var scripted)
            ? scripted
            : new NodeHardwareProfile(
                target, "Linux", "x64", false, 32768, SystemRamType.Ddr5,
                [new GpuDeviceProfile("NVIDIA", "RTX 4090", 24576, "GDDR6X", 384, 1008)],
                24576, 1008);
        lock (_gate)
        {
            _hardwareStore[target] = (profile, "ssh");
        }
        return profile;
    }

    /// <inheritdoc />
    public Task<RouterCartographyResponse> GetCartographyAsync(CancellationToken ct = default)
    {
        Log(nameof(GetCartographyAsync));
        return Task.FromResult(CartographyResponse);
    }

    /// <inheritdoc />
    public Task<RouterModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
        NotImplemented<RouterModelsResponse>(nameof(GetModelsAsync));

    /// <inheritdoc />
    public Task<RouterModelDetailResponse> GetModelDetailAsync(
        string modelName, CancellationToken ct = default)
    {
        Log(nameof(GetModelDetailAsync));
        if (ModelDetailException is { } detailException)
        {
            throw detailException;
        }

        return ModelDetailResponse is { } detail
            ? Task.FromResult(detail)
            : Task.FromException<RouterModelDetailResponse>(new OllamaRouterApiException(
                HttpStatusCode.NotFound, $"model '{modelName}' not found (fake default)"));
    }

    /// <inheritdoc />
    public async Task<OllamaRouterModelPullResult> PullModelAsync(
        string model,
        string? target = null,
        int? aliasContext = null,
        IProgress<OllamaRouterModelPullProgress>? progress = null,
        CancellationToken ct = default)
    {
        Log(nameof(PullModelAsync));
        PullCalls.Add((model, target, aliasContext));

        if (PullDelayMs > 0)
        {
            await Task.Delay(PullDelayMs, ct);
        }

        ct.ThrowIfCancellationRequested();

        if (PullException is { } pullException)
        {
            throw pullException;
        }

        if (PullFailTargets.Contains(target ?? string.Empty))
        {
            foreach (var evt in PullProgressEvents)
            {
                progress?.Report(evt);
            }

            return new OllamaRouterModelPullResult(false, target ?? "Local", model, null, aliasContext, PullFailError, null);
        }

        foreach (var evt in PullProgressEvents)
        {
            progress?.Report(evt);
        }

        if (PullResult is { } scripted)
        {
            return scripted;
        }

        var alias = aliasContext is { } ctx &&
            ModelPullNaming.TryComputeAliasName(model, ctx, out var computed)
            ? computed
            : null;
        return new OllamaRouterModelPullResult(true, target ?? "Local", model, alias, aliasContext, null, 42);
    }

    /// <inheritdoc />
    public Task<RouterModelPreloadResponse> PreloadModelAsync(
        string modelName, RouterModelPreloadRequest? request, CancellationToken ct = default)
    {
        Log(nameof(PreloadModelAsync));
        PreloadCalls.Add((modelName, request));
        return Task.FromResult(PreloadResponse);
    }

    /// <inheritdoc />
    public async Task<RouterModelUninstallResponse> UninstallModelAsync(
        string model, IReadOnlyList<string> targets, CancellationToken ct = default)
    {
        Log(nameof(UninstallModelAsync));
        UninstallCalls.Add((model, targets));

        if (UninstallBlock is { } blockTask)
        {
            await blockTask;
        }

        if (UninstallError is { } scripted)
        {
            UninstallError = null;
            throw new OllamaRouterApiException(scripted.Status, scripted.Error);
        }

        // A scripted response is served as-is; the default (no script) derives one per-target
        // failure row per requested target (fail-soft 200 shape).
        var response = UninstallResponse;
        if (response.Targets.Count == 0)
        {
            response = new RouterModelUninstallResponse(model,
                targets.Select(t => new RouterModelUninstallTargetResult(t, false, "not found")).ToList());
        }

        return response;
    }

    public Task<RouterModelPreloadResponse> PreviewPreloadAsync(
        string model, int? keepAliveSeconds, int? contextIntentTokens,
        string? target, CancellationToken ct = default)
    {
        Log(nameof(PreviewPreloadAsync));
        PreviewCalls.Add((model, keepAliveSeconds, contextIntentTokens, target));
        return Task.FromResult(PreloadResponse);
    }

    /// <inheritdoc />
    public Task<RouterVramReclaimResponse> ReclaimVramAsync(
        RouterVramReclaimRequest? request, CancellationToken ct = default)
    {
        Log(nameof(ReclaimVramAsync));
        ReclaimCalls.Add(request);
        return Task.FromResult(ReclaimResponse);
    }

    /// <inheritdoc />
    public Task<RouterConfigResponse> GetConfigAsync(CancellationToken ct = default)
    {
        Log(nameof(GetConfigAsync));
        // The live target store wins (CRUD flows through it); an unseeded store falls back
        // to the scripted <see cref="ConfigResponse"/> targets.
        var response = _targets.Count > 0
            ? ConfigResponse with { Targets = _targets.Values.ToList() }
            : ConfigResponse;
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public Task<UpdateRouterConfigResponse> UpdateConfigAsync(
        UpdateRouterConfigRequest request, CancellationToken ct = default)
    {
        Log(nameof(UpdateConfigAsync));
        if (UpdateConfigError is { } error)
        {
            throw new OllamaRouterApiException(error.Status, error.Error);
        }

        UpdateConfigCalls.Add(request);

        var current = ConfigResponse;
        var updated = new RouterConfigResponse(
            current.Targets,
            request.Cloud is { } cloud
                ? new RouterCloudConfigDto(
                    cloud.Enabled,
                    cloud.DefaultModel ?? current.Cloud.DefaultModel,
                    string.IsNullOrEmpty(cloud.PreferredRelayTarget)
                        ? "Local"
                        : cloud.PreferredRelayTarget ?? current.Cloud.PreferredRelayTarget,
                    current.Cloud.OverflowConfigured)
                : current.Cloud,
            request.DynamicContext ?? current.DynamicContext,
            current.Recording,
            request.Resilience ?? current.Resilience,
            request.Pricing ?? current.Pricing,
            current.ApiSecurity);
        return Task.FromResult(new UpdateRouterConfigResponse(true, "configuration updated", updated));
    }

    /// <inheritdoc />
    public Task<AddTargetResponse> AddTargetAsync(AddTargetRequest request, CancellationToken ct = default)
    {
        Log(nameof(AddTargetAsync));
        AddTargetCalls.Add(request);

        if (AddTargetError is { } scripted)
        {
            throw new OllamaRouterApiException(scripted.Status, scripted.Error);
        }

        if (string.IsNullOrWhiteSpace(request.Name) ||
            request.Name.Contains('/') || request.Name.Contains('\\') || request.Name.Contains(':') ||
            string.Equals(request.Name.Trim(), "Cloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new OllamaRouterApiException(HttpStatusCode.BadRequest, "invalid target name");
        }

        if (request.Type is not null &&
            !request.Type.Equals("ollama", StringComparison.OrdinalIgnoreCase) &&
            !request.Type.Equals("openai", StringComparison.OrdinalIgnoreCase) &&
            !request.Type.Equals("osnative", StringComparison.OrdinalIgnoreCase))
        {
            throw new OllamaRouterApiException(HttpStatusCode.BadRequest, "invalid target type");
        }

        // Task #143 mirror: a non-blank sshUser must match the POSIX-ish username charset;
        // sshPort must be 1..65535 when present.
        if (request.SshUser is { Length: > 0 } &&
            !System.Text.RegularExpressions.Regex.IsMatch(request.SshUser, "^[A-Za-z0-9._-]{1,64}$"))
        {
            throw new OllamaRouterApiException(HttpStatusCode.BadRequest, "invalid sshUser");
        }

        if (request.SshPort is { } addSshPort && (addSshPort < 1 || addSshPort > 65535))
        {
            throw new OllamaRouterApiException(HttpStatusCode.BadRequest, "invalid sshPort");
        }

        if (_targets.ContainsKey(request.Name))
        {
            throw new OllamaRouterApiException(
                HttpStatusCode.Conflict, $"target already exists: {request.Name}");
        }

        var target = new RouterTargetConfigDto(
            request.Name,
            request.Url,
            true,
            request.HardwareHint,
            request.MaxParallel,
            request.Type ?? "ollama",
            request.Provider,
            request.Models,
            request.ApiKey is not null,
            request.PowerWatts,
            SshUser: string.IsNullOrWhiteSpace(request.SshUser) ? null : request.SshUser,
            SshPort: request.SshPort);
        _targets[request.Name] = target;
        return Task.FromResult(new AddTargetResponse(
            true, request.TestConnectionFirst, "Online", request.Models?.Count ?? 0, target, 12, null));
    }

    /// <inheritdoc />
    public Task<UpdateTargetResponse> UpdateTargetAsync(
        string name, UpdateTargetRequest request, CancellationToken ct = default)
    {
        Log(nameof(UpdateTargetAsync));
        UpdateTargetCalls.Add((name, request));

        if (!_targets.TryGetValue(name, out var current))
        {
            throw new OllamaRouterApiException(
                HttpStatusCode.NotFound, $"target not found: {name}");
        }

        // Wire semantics: null/absent = no change, "" = clear.
        var updated = current with
        {
            Url = request.Url ?? current.Url,
            Enabled = request.Enabled ?? current.Enabled,
            HardwareHint = request.HardwareHint is { } hint ? hint : current.HardwareHint,
            MaxParallel = request.MaxParallel ?? current.MaxParallel,
            Type = request.Type ?? current.Type,
            // ApiKey: null = no change, "" = clear, value = set (only the flag is stored).
            ApiKeyConfigured = request.ApiKey is null
                ? current.ApiKeyConfigured
                : request.ApiKey.Length > 0,
            Provider = request.Provider is { } provider ? provider : current.Provider,
            Models = request.Models ?? current.Models,
            PowerWatts = request.PowerWatts ?? current.PowerWatts,
            // Task #143 wire semantics: null/absent = no change, "" = clear, value = set.
            SshUser = request.SshUser is null
                ? current.SshUser
                : request.SshUser.Length == 0 ? null : request.SshUser,
            // Task #143 wire semantics: null/absent = no change, value = set (no clear
            // semantics on the port — the documented powerWatts precedent).
            SshPort = request.SshPort ?? current.SshPort,
        };
        _targets[name] = updated;
        return Task.FromResult(new UpdateTargetResponse(true, "target updated", updated));
    }

    /// <inheritdoc />
    public Task<DeleteTargetResponse> DeleteTargetAsync(string name, CancellationToken ct = default)
    {
        Log(nameof(DeleteTargetAsync));
        DeleteTargetCalls.Add(name);
        if (!_targets.Remove(name))
        {
            throw new OllamaRouterApiException(
                HttpStatusCode.NotFound, $"target not found: {name}");
        }

        return Task.FromResult(new DeleteTargetResponse(true, $"target deleted: {name}"));
    }

    /// <inheritdoc />
    public Task<ResetTargetStreakResponse> ResetTargetStreakAsync(string name, CancellationToken ct = default)
    {
        Log(nameof(ResetTargetStreakAsync));
        if (!_targets.ContainsKey(name))
        {
            throw new OllamaRouterApiException(
                HttpStatusCode.NotFound, $"target not found: {name}");
        }

        return Task.FromResult(new ResetTargetStreakResponse(true, name, 0, "Closed"));
    }

    /// <inheritdoc />
    public Task<ResetFleetStreaksResponse> ResetFleetStreaksAsync(CancellationToken ct = default)
    {
        Log(nameof(ResetFleetStreaksAsync));
        return Task.FromResult(new ResetFleetStreaksResponse(true, 0, []));
    }

    /// <inheritdoc />
    public Task<RouterBenchmarkRunResponse> StartBenchmarkAsync(
        RouterBenchmarkRequest? request, CancellationToken ct = default) =>
        NotImplemented<RouterBenchmarkRunResponse>(nameof(StartBenchmarkAsync));

    /// <inheritdoc />
    public Task<RouterBenchmarkResultsResponse> GetBenchmarkResultsAsync(CancellationToken ct = default)
    {
        Log(nameof(GetBenchmarkResultsAsync));
        return Task.FromResult(BenchmarkResults);
    }

    /// <inheritdoc />
    public Task<RouterBenchmarkClearedResponse> ClearBenchmarkResultsAsync(CancellationToken ct = default) =>
        NotImplemented<RouterBenchmarkClearedResponse>(nameof(ClearBenchmarkResultsAsync));

    /// <inheritdoc />
    public Task<RouterSessionUsageListResponse> GetSessionsAsync(CancellationToken ct = default)
    {
        Log(nameof(GetSessionsAsync));
        return Task.FromResult(SessionsResponse);
    }

    /// <inheritdoc />
    public Task<RouterSessionUsageDetailResponse> GetSessionDetailAsync(
        string sessionId, CancellationToken ct = default)
    {
        Log(nameof(GetSessionDetailAsync));
        if (!SessionDetails.TryGetValue(sessionId, out var detail))
        {
            throw new OllamaRouterApiException(
                HttpStatusCode.NotFound, $"session not found: {sessionId}");
        }

        return Task.FromResult(detail);
    }

    /// <inheritdoc />
    public Task<RouterSessionAffinityReleaseResponse> ReleaseSessionAffinityAsync(
        string sessionId, CancellationToken ct = default) =>
        NotImplemented<RouterSessionAffinityReleaseResponse>(nameof(ReleaseSessionAffinityAsync));

    /// <inheritdoc />
    public Task<ApiKeysListResponse> GetApiKeysAsync(CancellationToken ct = default)
    {
        Log(nameof(GetApiKeysAsync));
        return Task.FromResult(new ApiKeysListResponse(ApiKeys));
    }

    /// <inheritdoc />
    public Task<ApiKeyCreatedResponse> CreateApiKeyAsync(
        ApiKeyCreateRequest request, CancellationToken ct = default)
    {
        Log(nameof(CreateApiKeyAsync));
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64 ||
            request.Name.Any(c => char.IsWhiteSpace(c) || c == '/' || c == ':') ||
            ApiKeys.Any(k => string.Equals(k.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new OllamaRouterApiException(HttpStatusCode.BadRequest, "invalid key name");
        }

        if (request.ValidFor is < 1)
        {
            throw new OllamaRouterApiException(
                HttpStatusCode.BadRequest, "validFor must be at least 1 hour");
        }

        // The raw key is returned EXACTLY ONCE; only the masked row is retained.
        var rawKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) +
                     Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var created = new ApiKeyCreatedResponse(
            true,
            request.Name,
            rawKey,
            DateTimeOffset.UtcNow,
            request.ValidFor is { } hours ? DateTimeOffset.UtcNow.AddHours(hours) : null);
        CreatedKeys.Add(created);
        ApiKeys.Add(new ApiKeyInfoResponse(request.Name, created.CreatedAt, created.ExpiresAt));
        return Task.FromResult(created);
    }

    /// <inheritdoc />
    public Task<ApiKeyDeletedResponse> DeleteApiKeyAsync(string name, CancellationToken ct = default)
    {
        Log(nameof(DeleteApiKeyAsync));
        var index = ApiKeys.FindIndex(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new OllamaRouterApiException(
                HttpStatusCode.NotFound, $"API key not found: {name}");
        }

        ApiKeys.RemoveAt(index);
        return Task.FromResult(new ApiKeyDeletedResponse(true, null));
    }

    /// <inheritdoc />
    public Task<ApiKeyRotatedResponse> RotateDefaultKeyAsync(CancellationToken ct = default)
    {
        Log(nameof(RotateDefaultKeyAsync));
        var rawKey = "rotated-" + Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var rotated = new ApiKeyRotatedResponse(true, rawKey, DateTimeOffset.UtcNow);
        RotatedKeys.Add(rotated);
        return Task.FromResult(rotated);
    }

    /// <inheritdoc />
    public Task<FleetRecommendations> GetRecommendationsAsync(CancellationToken ct = default)
    {
        Log(nameof(GetRecommendationsAsync));
        return Task.FromResult(Recommendations);
    }

    /// <inheritdoc />
    public Task<RouterRecommendationsGeneratedResponse> GenerateRecommendationsAsync(CancellationToken ct = default)
    {
        Log(nameof(GenerateRecommendationsAsync));
        if (GenerateError is { } error)
        {
            throw new OllamaRouterApiException(error.Status, error.Error);
        }

        if (GenerateResponse is { } response)
        {
            GenerateCalls.Add(response);
            return Task.FromResult(response);
        }

        // Default: a freshly generated single-role matrix over a "Local" node.
        var generatedAt = DateTimeOffset.UtcNow;
        var matrix = new FleetRecommendations(
            generatedAt,
            new Dictionary<string, RoleRecommendation>
            {
                ["basic"] = new RoleRecommendation(
                    "basic", "llama3:8b", 4096, "Generated from benchmark evidence.",
                    [new NodeRoleProfile("Local", 4096, 2048, 24.5, 8192)]),
            },
            new Dictionary<string, ModelNodeProfile>
            {
                ["llama3:8b"] = new ModelNodeProfile(
                    "llama3:8b", "llama", 8192, "q8_0",
                    [new NodeRoleProfile("Local", 4096, 2048, 24.5, 8192)]),
            });
        var generated = new RouterRecommendationsGeneratedResponse(
            true, generatedAt, 1, 1, ["Local"], matrix);
        GenerateCalls.Add(generated);
        return Task.FromResult(generated);
    }

    /// <inheritdoc />
    public Task<RouterRecommendationsAppliedResponse> ApplyRecommendationsAsync(CancellationToken ct = default)
    {
        Log(nameof(ApplyRecommendationsAsync));
        if (ApplyError is { } error)
        {
            throw new OllamaRouterApiException(error.Status, error.Error);
        }

        if (ApplyResponse is { } response)
        {
            ApplyCalls.Add(response);
            return Task.FromResult(response);
        }

        var applied = new RouterRecommendationsAppliedResponse(
            true, DateTimeOffset.UtcNow,
            [new RouterRecommendationsAppliedTarget("Local", 1, 4096)]);
        ApplyCalls.Add(applied);
        return Task.FromResult(applied);
    }

    private static Task<T> NotImplemented<T>(string method) =>
        Task.FromException<T>(new NotSupportedException(
            $"FakeRouterApiClient does not implement {method} (the app does not call it)."));
}
