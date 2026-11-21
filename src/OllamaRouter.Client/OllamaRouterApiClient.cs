using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
using OllamaRouter.Middleware;
using Pmad.LocalApi.Security;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Recommendations;

namespace OllamaRouter.Client;

/// <summary>
/// Strongly-typed client for the OllamaRouter management/monitoring API (task #98): the
/// full <c>/api/router/*</c> + <c>/monitor/api</c> surface shared by the CLI, the desktop
/// app and external tooling (e.g. NightCodeForge). All serialization flows through the
/// source-generated <see cref="OllamaRouterClientJsonContext"/> (no reflection, AOT-safe).
/// </summary>
public interface IOllamaRouterApiClient
{
    // ------------------------------------------------------------------
    //  Info + monitor
    // ------------------------------------------------------------------

    /// <summary><c>GET /api/router/info</c>: router version, fleet summary and feature flags.</summary>
    Task<RouterInfoResponse> GetInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/admin/shutdown</c> (202): triggers the router's graceful host shutdown
    /// (task #99). Key-protected: unauthenticated requests get a 401 (surfaced as
    /// <see cref="OllamaRouterApiException"/> after the single automatic 401 re-read retry).
    /// </summary>
    Task<RouterShutdownResponse> ShutdownAsync(CancellationToken ct = default);

    /// <summary><c>GET /monitor/api</c>: the full monitor state (fleet, statistics, requests, cost, overhead).</summary>
    Task<MonitorStateResponse> GetMonitorStateAsync(CancellationToken ct = default);

    /// <summary><c>POST /targets</c>: applies the flat target-name → enabled-flag map; returns the applied flags.</summary>
    Task<IReadOnlyDictionary<string, bool>> UpdateTargetAvailabilityAsync(
        IReadOnlyDictionary<string, bool> flags, CancellationToken ct = default);

    // ------------------------------------------------------------------
    //  Fleet + hardware
    // ------------------------------------------------------------------

    /// <summary><c>GET /api/router/fleet</c>: the fleet target rows (liveness, hardware, execution, preload policy).</summary>
    Task<RouterFleetResponse> GetFleetAsync(CancellationToken ct = default);

    /// <summary><c>GET /api/router/fleet/hardware</c>: per-node hardware profiles + reachability.</summary>
    Task<RouterFleetHardwareResponse> GetFleetHardwareAsync(CancellationToken ct = default);

    /// <summary><c>PUT /api/router/fleet/hardware/{target}</c>: stores/overwrites the target's hardware profile; echoes it back.</summary>
    Task<NodeHardwareProfile> UpdateTargetHardwareAsync(
        string target, NodeHardwareProfile profile, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/fleet/hardware/{target}/probe</c> (task #143): runs the daemon-side
    /// explicit hardware probe (local bash for the local workstation target, ssh to
    /// <c>{sshUser}@{host}</c> for remote targets — key-based auth, requires a configured
    /// <c>sshUser</c>) and stores the digest result with the provenance label "local"/"ssh"
    /// (an explicit probe is authoritative — it overwrites even a manual profile); echoes
    /// the stored profile. Non-2xx (400 no sshUser / 404 unknown target / 502 probe failure,
    /// 401 without a key) surface as <see cref="OllamaRouterApiException"/>.
    /// </summary>
    Task<NodeHardwareProfile> ProbeTargetHardwareAsync(string target, CancellationToken ct = default);

    // ------------------------------------------------------------------
    //  Cartography + models + preload + VRAM
    // ------------------------------------------------------------------

    /// <summary><c>GET /api/router/cartography</c>: fleet node rows, model deployment rows and flat loaded-process list.</summary>
    Task<RouterCartographyResponse> GetCartographyAsync(CancellationToken ct = default);

    /// <summary><c>GET /api/router/models</c>: the fleet-wide model capability summaries.</summary>
    Task<RouterModelsResponse> GetModelsAsync(CancellationToken ct = default);

    /// <summary><c>GET /api/router/models/{*modelName}</c>: the model detail + per-target qualification matrix.</summary>
    Task<RouterModelDetailResponse> GetModelDetailAsync(string modelName, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/models/pull</c> (task #125): pulls <paramref name="model"/>
    /// (a <c>name[:tag]</c> reference) onto <paramref name="target"/> (null = the primary
    /// target fallback, resolved LIVE server-side) and streams the progress back; when
    /// <paramref name="aliasContext"/> (>= 1) is set the target additionally gets the context
    /// alias <c>{name}:{tag}-{ctx}</c>. <paramref name="progress"/> receives the per-line pull
    /// progress + alias lifecycle (reported on the caller's synchronization context). Pre-stream
    /// failures (400/404/409) surface as <see cref="OllamaRouterApiException"/>; in-stream
    /// failures surface as a <see cref="OllamaRouterModelPullResult"/> with
    /// <c>Succeeded: false</c> + <c>Error</c> (no exception); cancellation surfaces as
    /// <c>OperationCanceledException</c>.
    /// </summary>
    Task<OllamaRouterModelPullResult> PullModelAsync(
        string model,
        string? target = null,
        int? aliasContext = null,
        IProgress<OllamaRouterModelPullProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/models/{*modelName}/preload</c>: loads the model on the selected fleet
    /// target. <paramref name="request"/> may be null (an absent body is valid; the server then
    /// uses the query-string / class defaults).
    /// </summary>
    Task<RouterModelPreloadResponse> PreloadModelAsync(
        string modelName, RouterModelPreloadRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/models/uninstall</c> (task #126): permanently deletes the model from
    /// the selected fleet targets (body-only contract: <c>{ model, targets[] }</c>).
    /// Key-protected in default mode (the <see cref="IDefaultKeyReader"/> / explicit key handles
    /// the 401). Returns the per-target execution report IN REQUEST ORDER (fail-soft: a failing
    /// target never aborts the others — its row carries <c>success:false</c> + the daemon's
    /// error text). 400 (invalid body) / 404 (unknown model or target — checked before ANY
    /// daemon call) surface as <see cref="OllamaRouterApiException"/>.
    /// </summary>
    Task<RouterModelUninstallResponse> UninstallModelAsync(
        string model, IReadOnlyList<string> targets, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /api/router/preload/preview</c>: the side-effect-free preload selection (query
    /// parameters <c>model</c> / <c>keepAliveSeconds</c> / <c>contextIntentTokens</c> / <c>target</c>).
    /// </summary>
    Task<RouterModelPreloadResponse> PreviewPreloadAsync(
        string model, int? keepAliveSeconds = null, int? contextIntentTokens = null,
        string? target = null, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/vram/reclaim</c>: unloads models on the target set.
    /// <paramref name="request"/> may be null (an absent body is valid → all effective targets).
    /// </summary>
    Task<RouterVramReclaimResponse> ReclaimVramAsync(
        RouterVramReclaimRequest? request = null, CancellationToken ct = default);

    // ------------------------------------------------------------------
    //  Configuration + targets
    // ------------------------------------------------------------------

    /// <summary><c>GET /api/router/config</c>: the full live router configuration.</summary>
    Task<RouterConfigResponse> GetConfigAsync(CancellationToken ct = default);

    /// <summary><c>PUT /api/router/config</c>: partial per-section configuration update.</summary>
    Task<UpdateRouterConfigResponse> UpdateConfigAsync(UpdateRouterConfigRequest request, CancellationToken ct = default);

    /// <summary><c>POST /api/router/targets</c> (201): adds a fleet target (connection probe optional).</summary>
    Task<AddTargetResponse> AddTargetAsync(AddTargetRequest request, CancellationToken ct = default);

    /// <summary><c>PUT /api/router/targets/{name}</c>: partial target update.</summary>
    Task<UpdateTargetResponse> UpdateTargetAsync(string name, UpdateTargetRequest request, CancellationToken ct = default);

    /// <summary><c>DELETE /api/router/targets/{name}</c>: removes a fleet target.</summary>
    Task<DeleteTargetResponse> DeleteTargetAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/targets/{name}/reset-streak</c> (task #95): resets the target's
    /// failure streak + circuit-breaker cooldown (200 with streak 0 / "Closed"; 404 unknown
    /// target — surfaced as <see cref="OllamaRouterApiException"/>).
    /// </summary>
    Task<ResetTargetStreakResponse> ResetTargetStreakAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/fleet/reset-streaks</c> (task #95): resets the failure streak of
    /// every fleet target (the Cloud pseudo-target when cloud overflow is enabled); returns
    /// the per-target previousStreak/reset summary.
    /// </summary>
    Task<ResetFleetStreaksResponse> ResetFleetStreaksAsync(CancellationToken ct = default);

    // ------------------------------------------------------------------
    //  Benchmarks
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>POST /api/router/benchmark</c> (202): queues a benchmark run.
    /// <paramref name="request"/> may be null (an absent body is valid → all effective targets).
    /// <see cref="RouterBenchmarkRequest.ExcludedModels"/> (task #152) is the per-target
    /// run-scoped model exclusion map: excluded models are skipped for THAT RUN ONLY (they
    /// produce no rows) and are never persisted; a null request — sent when nothing is
    /// unchecked — is a byte-identical absent-body POST (the pre-#152 wire contract).
    /// </summary>
    Task<RouterBenchmarkRunResponse> StartBenchmarkAsync(
        RouterBenchmarkRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /api/router/benchmark/candidates</c> (task #152): the per-target benchmark
    /// candidate plan — one row per effective non-Cloud target (configuration order) with the
    /// stored fleet-hardware profile (null when unknown — no probe is triggered) and the EXACT
    /// catalog candidate set the run loop would benchmark, each with its pre-flight fit
    /// verdict (<c>Fits</c> / <c>Pruned</c> / <c>Assumed</c> + the largest surviving ladder
    /// context, capped by the model's nativeContext). Read-only, keyless in default mode.
    /// </summary>
    Task<RouterBenchmarkCandidatesResponse> GetBenchmarkCandidatesAsync(CancellationToken ct = default);

    /// <summary><c>GET /api/router/benchmark/results</c>: every known target result (persisted or in-flight).</summary>
    Task<RouterBenchmarkResultsResponse> GetBenchmarkResultsAsync(CancellationToken ct = default);

    /// <summary><c>DELETE /api/router/benchmark/results</c>: clears the in-memory + persisted benchmark results.</summary>
    Task<RouterBenchmarkClearedResponse> ClearBenchmarkResultsAsync(CancellationToken ct = default);

    // ------------------------------------------------------------------
    //  Sessions
    // ------------------------------------------------------------------

    /// <summary><c>GET /api/router/monitor/sessions</c>: per-session usage aggregates (most-recently-active first).</summary>
    Task<RouterSessionUsageListResponse> GetSessionsAsync(CancellationToken ct = default);

    /// <summary><c>GET /api/router/monitor/sessions/{*sessionId}</c>: the session detail + per-target breakdown.</summary>
    Task<RouterSessionUsageDetailResponse> GetSessionDetailAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/sessions/affinity/release</c>: releases the session's sticky-routing
    /// affinity lease (identified by the <c>X-OllamaRouter-Session-Id</c> request header).
    /// </summary>
    Task<RouterSessionAffinityReleaseResponse> ReleaseSessionAffinityAsync(string sessionId, CancellationToken ct = default);

    // ------------------------------------------------------------------
    //  API keys
    // ------------------------------------------------------------------

    /// <summary><c>GET /api/router/api-keys</c>: the masked user key list (no key material).</summary>
    Task<ApiKeysListResponse> GetApiKeysAsync(CancellationToken ct = default);

    /// <summary><c>POST /api/router/api-keys</c> (201): creates a user key (the raw key is returned exactly once).</summary>
    Task<ApiKeyCreatedResponse> CreateApiKeyAsync(ApiKeyCreateRequest request, CancellationToken ct = default);

    /// <summary><c>DELETE /api/router/api-keys/{name}</c>: deletes a user key.</summary>
    Task<ApiKeyDeletedResponse> DeleteApiKeyAsync(string name, CancellationToken ct = default);

    /// <summary><c>POST /api/router/api-keys/default/rotate</c>: rotates the default key (previous key invalid immediately).</summary>
    Task<ApiKeyRotatedResponse> RotateDefaultKeyAsync(CancellationToken ct = default);

    // ------------------------------------------------------------------
    //  Role recommendations (task #66 wire surface; the Setup Wizard's Step 4)
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>GET /api/router/recommendations</c>: the live role recommendation matrix (read-only;
    /// the stable empty shape when nothing is active). Keyless in default mode.
    /// </summary>
    Task<FleetRecommendations> GetRecommendationsAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/recommendations/generate</c> (no body): recompute + persist +
    /// activate the matrix from the persisted benchmark results. 409 (no terminal benchmark
    /// data) surfaces as <see cref="OllamaRouterApiException"/> with the parsed <c>Error</c>.
    /// </summary>
    Task<RouterRecommendationsGeneratedResponse> GenerateRecommendationsAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>POST /api/router/recommendations/apply</c> (no body): idempotent re-activation of the
    /// current <c>recommended.json</c>, reporting the per-target ceilings now enforced. 409 (no
    /// file) surfaces as <see cref="OllamaRouterApiException"/> with the parsed <c>Error</c>.
    /// </summary>
    Task<RouterRecommendationsAppliedResponse> ApplyRecommendationsAsync(CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IOllamaRouterApiClient"/> implementation (task #98).
/// <para>
/// Authentication: an explicitly configured <c>apiKey</c> wins; otherwise the
/// <see cref="IDefaultKeyReader"/> re-reads <c>default-key.dat</c> on every request (no cache).
/// When a key is available the request carries <c>Authorization: Bearer &lt;key&gt;</c>; when
/// none is available the request is sent keyless (the router's default mode keeps read routes
/// public). On an HTTP 401 with a key reader configured the SAME request is retried EXACTLY
/// ONCE with the freshly re-read key — this transparently handles the router's default-key
/// auto-rotation. Without a key reader (explicit key only) a 401 is surfaced immediately.
/// Every other non-2xx status surfaces as <see cref="OllamaRouterApiException"/> (Status +
/// parsed error, raw-body fallback).
/// </para>
/// </summary>
public sealed class OllamaRouterApiClient : IOllamaRouterApiClient, IDisposable
{
    /// <summary>Default request timeout: model preload loads can exceed the 20 s server preload bound.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The public session-id request header of the router's session-affinity API.</summary>
    internal const string SessionIdHeader = "X-OllamaRouter-Session-Id";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string? _apiKey;
    private readonly IDefaultKeyReader? _keyReader;

    /// <summary>
    /// Creates the client. <paramref name="httpClient"/> is injectable for tests (a fake
    /// <see cref="HttpMessageHandler"/>); when omitted an internal client is created with
    /// <c>BaseAddress = baseUrl</c> and <c>Timeout = timeout ?? 120 s</c> (an owned client is
    /// the only one <see cref="Dispose"/> frees). A provided client with a null
    /// <see cref="HttpClient.BaseAddress"/> gets <paramref name="baseUrl"/> applied.
    /// </summary>
    public OllamaRouterApiClient(
        Uri baseUrl,
        string? apiKey = null,
        IDefaultKeyReader? keyReader = null,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _keyReader = keyReader;

        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                BaseAddress = baseUrl,
                Timeout = timeout ?? DefaultTimeout,
            };
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsClient = false;
            if (_httpClient.BaseAddress is null)
            {
                _httpClient.BaseAddress = baseUrl;
            }
        }
    }

    /// <summary>
    /// Frees the internal <see cref="HttpClient"/> when this instance created it (an
    /// externally provided client is left alone).
    /// </summary>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    // ------------------------------------------------------------------
    //  IOllamaRouterApiClient — info + monitor
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<RouterInfoResponse> GetInfoAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/info",
            OllamaRouterClientJsonContext.Default.RouterInfoResponse, ct);

    /// <inheritdoc />
    public Task<RouterShutdownResponse> ShutdownAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/router/admin/shutdown",
            OllamaRouterClientJsonContext.Default.RouterShutdownResponse, ct);

    /// <inheritdoc />
    public async Task<MonitorStateResponse> GetMonitorStateAsync(CancellationToken ct = default)
    {
        var state = await SendAsync(HttpMethod.Get, "monitor/api",
            OllamaRouterClientJsonContext.Default.MonitorStateResponse, ct).ConfigureAwait(false);

        // Always-array wire contract (task #45, enforced on the CLIENT read path by task #98):
        // the server ALWAYS serializes <c>residentModels</c> as an array (empty array when idle,
        // never null). A legacy/malformed JSON null is normalized to an empty array here so
        // callers can rely on <c>Fleet[i].ResidentModels</c> never being null (the converter
        // itself keeps reading a raw JSON null as null — pinned by
        // ResidentModelsJsonConverterTests — so the read-path invariant lives here).
        if (state.Fleet.Any(item => item.ResidentModels is null))
        {
            var fleet = state.Fleet
                .Select(item => item.ResidentModels is null
                    ? item with { ResidentModels = Array.Empty<string>() }
                    : item)
                .ToArray();
            state = state with { Fleet = fleet };
        }

        return state;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, bool>> UpdateTargetAvailabilityAsync(
        IReadOnlyDictionary<string, bool> flags, CancellationToken ct = default)
    {
        var copy = new Dictionary<string, bool>(flags, StringComparer.OrdinalIgnoreCase);
        var applied = await SendWithBodyAsync(
            HttpMethod.Post,
            "targets",
            copy,
            OllamaRouterClientJsonContext.Default.DictionaryStringBoolean,
            OllamaRouterClientJsonContext.Default.DictionaryStringBoolean,
            ct).ConfigureAwait(false);
        return applied;
    }

    // ------------------------------------------------------------------
    //  IOllamaRouterApiClient — fleet + hardware
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<RouterFleetResponse> GetFleetAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/fleet",
            OllamaRouterClientJsonContext.Default.RouterFleetResponse, ct);

    /// <inheritdoc />
    public Task<RouterFleetHardwareResponse> GetFleetHardwareAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/fleet/hardware",
            OllamaRouterClientJsonContext.Default.RouterFleetHardwareResponse, ct);

    /// <inheritdoc />
    public Task<NodeHardwareProfile> UpdateTargetHardwareAsync(
        string target, NodeHardwareProfile profile, CancellationToken ct = default)
    {
        var typeInfo = OllamaRouterClientJsonContext.Default.NodeHardwareProfile;
        return SendWithBodyAsync(
            HttpMethod.Put,
            BuildPath("api/router/fleet/hardware", target),
            profile,
            typeInfo,
            typeInfo,
            ct);
    }

    /// <inheritdoc />
    public Task<NodeHardwareProfile> ProbeTargetHardwareAsync(string target, CancellationToken ct = default) =>
        SendAsync(
            HttpMethod.Post,
            BuildPath("api/router/fleet/hardware", target, "probe"),
            OllamaRouterClientJsonContext.Default.NodeHardwareProfile,
            ct);

    // ------------------------------------------------------------------
    //  IOllamaRouterApiClient — cartography + models + preload + VRAM
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<RouterCartographyResponse> GetCartographyAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/cartography",
            OllamaRouterClientJsonContext.Default.RouterCartographyResponse, ct);

    /// <inheritdoc />
    public Task<RouterModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/models",
            OllamaRouterClientJsonContext.Default.RouterModelsResponse, ct);

    /// <inheritdoc />
    public Task<RouterModelDetailResponse> GetModelDetailAsync(string modelName, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, BuildPath("api/router/models", modelName),
            OllamaRouterClientJsonContext.Default.RouterModelDetailResponse, ct);

    /// <inheritdoc />
    public async Task<OllamaRouterModelPullResult> PullModelAsync(
        string model,
        string? target = null,
        int? aliasContext = null,
        IProgress<OllamaRouterModelPullProgress>? progress = null,
        CancellationToken ct = default)
    {
        var request = new OllamaRouterModelPullRequest(model, target, aliasContext);

        // The request is re-created on the 401 retry (a content stream can only be consumed
        // once), exactly like CoreAsync. HttpCompletionOption.ResponseHeadersRead: the
        // HttpClient.Timeout bounds only send + headers; the NDJSON body stream is bound by
        // <paramref name="ct"/> only (pulls are legitimately long).
        HttpRequestMessage CreateMessage()
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "api/router/models/pull");
            var key = ResolveKey();
            if (!string.IsNullOrEmpty(key))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            message.Content = CreateContent(request, OllamaRouterClientJsonContext.Default.OllamaRouterModelPullRequest);
            return message;
        }

        var response = await _httpClient
            .SendAsync(CreateMessage(), HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // 401 auto-retry (default-key auto-rotation): the SAME request is retried EXACTLY ONCE
        // with the freshly re-read key (same semantics as CoreAsync, before the body streams).
        if (response.StatusCode == HttpStatusCode.Unauthorized && _keyReader is not null)
        {
            response.Dispose();
            ct.ThrowIfCancellationRequested();
            response = await _httpClient
                .SendAsync(CreateMessage(), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            var error = await ReadErrorAsync(response).ConfigureAwait(false);
            response.Dispose();
            throw new OllamaRouterApiException(status, error);
        }

        var servedTarget = target ?? string.Empty;
        string? seenAlias = null;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                OllamaRouterModelPullEvent? evt = null;
                try
                {
                    evt = JsonSerializer.Deserialize(line, OllamaRouterClientJsonContext.Default.OllamaRouterModelPullEvent);
                }
                catch (JsonException)
                {
                    // Skip a malformed line (a terminal event line is always well-formed JSON).
                    continue;
                }

                if (evt is null)
                {
                    continue;
                }

                switch (evt.Event)
                {
                    case "started":
                        servedTarget = evt.Target ?? servedTarget;
                        break;
                    case "present":
                        progress?.Report(new OllamaRouterModelPullProgress("pull", "present", null, null, null, null));
                        break;
                    case "progress":
                        progress?.Report(new OllamaRouterModelPullProgress(
                            "pull", evt.Status ?? string.Empty, evt.TotalBytes, evt.CompletedBytes, null, null));
                        break;
                    case "alias_started":
                        progress?.Report(new OllamaRouterModelPullProgress("alias", "started", null, null, evt.Alias, evt.Context));
                        break;
                    case "alias_created":
                        seenAlias = evt.Alias ?? seenAlias;
                        progress?.Report(new OllamaRouterModelPullProgress("alias", "created", null, null, evt.Alias, evt.Context));
                        break;
                    case "success":
                        return new OllamaRouterModelPullResult(
                            true, evt.Target ?? servedTarget, model, evt.Alias ?? seenAlias, aliasContext, null, evt.DurationMs);
                    case "error":
                        return new OllamaRouterModelPullResult(
                            false, evt.Target ?? servedTarget, model, seenAlias, aliasContext, evt.Error ?? "pull failed", null);
                }
            }
        }
        finally
        {
            response.Dispose();
        }

        // Stream end without a terminal event (a clean server disconnect) — NOT a success.
        return new OllamaRouterModelPullResult(
            false, servedTarget, model, seenAlias, aliasContext, "unexpected end of pull stream", null);
    }

    /// <inheritdoc />
    public Task<RouterModelPreloadResponse> PreloadModelAsync(
        string modelName, RouterModelPreloadRequest? request = null, CancellationToken ct = default) =>
        SendWithBodyAsync(
            HttpMethod.Post,
            BuildPath("api/router/models", modelName, "preload"),
            request,
            OllamaRouterClientJsonContext.Default.RouterModelPreloadRequest,
            OllamaRouterClientJsonContext.Default.RouterModelPreloadResponse,
            ct);

    /// <inheritdoc />
    public Task<RouterModelUninstallResponse> UninstallModelAsync(
        string model, IReadOnlyList<string> targets, CancellationToken ct = default) =>
        SendWithBodyAsync(
            HttpMethod.Post,
            "api/router/models/uninstall",
            new RouterModelUninstallRequest(model, targets),
            OllamaRouterClientJsonContext.Default.RouterModelUninstallRequest,
            OllamaRouterClientJsonContext.Default.RouterModelUninstallResponse,
            ct);

    /// <inheritdoc />
    public Task<RouterModelPreloadResponse> PreviewPreloadAsync(
        string model, int? keepAliveSeconds = null, int? contextIntentTokens = null,
        string? target = null, CancellationToken ct = default)
    {
        var query = new List<string> { $"model={EscapeDataSegment(model)}" };
        if (keepAliveSeconds.HasValue)
        {
            query.Add($"keepAliveSeconds={keepAliveSeconds.Value}");
        }

        if (contextIntentTokens.HasValue)
        {
            query.Add($"contextIntentTokens={contextIntentTokens.Value}");
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            query.Add($"target={EscapeDataSegment(target)}");
        }

        return SendAsync(HttpMethod.Get,
            $"api/router/preload/preview?{string.Join("&", query)}",
            OllamaRouterClientJsonContext.Default.RouterModelPreloadResponse, ct);
    }

    /// <inheritdoc />
    public Task<RouterVramReclaimResponse> ReclaimVramAsync(
        RouterVramReclaimRequest? request = null, CancellationToken ct = default) =>
        SendWithBodyAsync(
            HttpMethod.Post,
            "api/router/vram/reclaim",
            request,
            OllamaRouterClientJsonContext.Default.RouterVramReclaimRequest,
            OllamaRouterClientJsonContext.Default.RouterVramReclaimResponse,
            ct);

    // ------------------------------------------------------------------
    //  IOllamaRouterApiClient — configuration + targets
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<RouterConfigResponse> GetConfigAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/config",
            OllamaRouterClientJsonContext.Default.RouterConfigResponse, ct);

    /// <inheritdoc />
    public Task<UpdateRouterConfigResponse> UpdateConfigAsync(UpdateRouterConfigRequest request, CancellationToken ct = default) =>
        SendWithBodyAsync(
            HttpMethod.Put,
            "api/router/config",
            request,
            OllamaRouterClientJsonContext.Default.UpdateRouterConfigRequest,
            OllamaRouterClientJsonContext.Default.UpdateRouterConfigResponse,
            ct);

    /// <inheritdoc />
    public Task<AddTargetResponse> AddTargetAsync(AddTargetRequest request, CancellationToken ct = default) =>
        SendWithBodyAsync(
            HttpMethod.Post,
            "api/router/targets",
            request,
            OllamaRouterClientJsonContext.Default.AddTargetRequest,
            OllamaRouterClientJsonContext.Default.AddTargetResponse,
            ct);

    /// <inheritdoc />
    public Task<UpdateTargetResponse> UpdateTargetAsync(string name, UpdateTargetRequest request, CancellationToken ct = default) =>
        SendWithBodyAsync(
            HttpMethod.Put,
            BuildPath("api/router/targets", name),
            request,
            OllamaRouterClientJsonContext.Default.UpdateTargetRequest,
            OllamaRouterClientJsonContext.Default.UpdateTargetResponse,
            ct);

    /// <inheritdoc />
    public Task<DeleteTargetResponse> DeleteTargetAsync(string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, BuildPath("api/router/targets", name),
            OllamaRouterClientJsonContext.Default.DeleteTargetResponse, ct);

    /// <inheritdoc />
    public Task<ResetTargetStreakResponse> ResetTargetStreakAsync(string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, BuildPath("api/router/targets", name, "reset-streak"),
            OllamaRouterClientJsonContext.Default.ResetTargetStreakResponse, ct);

    /// <inheritdoc />
    public Task<ResetFleetStreaksResponse> ResetFleetStreaksAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/router/fleet/reset-streaks",
            OllamaRouterClientJsonContext.Default.ResetFleetStreaksResponse, ct);

    // ------------------------------------------------------------------
    //  IOllamaRouterApiClient — benchmarks
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<RouterBenchmarkRunResponse> StartBenchmarkAsync(
        RouterBenchmarkRequest? request = null, CancellationToken ct = default) =>
        SendWithBodyAsync(
            HttpMethod.Post,
            "api/router/benchmark",
            request,
            OllamaRouterClientJsonContext.Default.RouterBenchmarkRequest,
            OllamaRouterClientJsonContext.Default.RouterBenchmarkRunResponse,
            ct);

    /// <inheritdoc />
    public Task<RouterBenchmarkCandidatesResponse> GetBenchmarkCandidatesAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/benchmark/candidates",
            OllamaRouterClientJsonContext.Default.RouterBenchmarkCandidatesResponse, ct);

    /// <inheritdoc />
    public Task<RouterBenchmarkResultsResponse> GetBenchmarkResultsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/benchmark/results",
            OllamaRouterClientJsonContext.Default.RouterBenchmarkResultsResponse, ct);

    /// <inheritdoc />
    public Task<RouterBenchmarkClearedResponse> ClearBenchmarkResultsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "api/router/benchmark/results",
            OllamaRouterClientJsonContext.Default.RouterBenchmarkClearedResponse, ct);

    // ------------------------------------------------------------------
    //  IOllamaRouterApiClient — sessions
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<RouterSessionUsageListResponse> GetSessionsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/monitor/sessions",
            OllamaRouterClientJsonContext.Default.RouterSessionUsageListResponse, ct);

    /// <inheritdoc />
    public Task<RouterSessionUsageDetailResponse> GetSessionDetailAsync(string sessionId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, BuildPath("api/router/monitor/sessions", sessionId),
            OllamaRouterClientJsonContext.Default.RouterSessionUsageDetailResponse, ct);

    /// <inheritdoc />
    public Task<RouterSessionAffinityReleaseResponse> ReleaseSessionAffinityAsync(string sessionId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/router/sessions/affinity/release",
            OllamaRouterClientJsonContext.Default.RouterSessionAffinityReleaseResponse,
            ct,
            configure: message => message.Headers.Add(SessionIdHeader, sessionId));

    // ------------------------------------------------------------------
    //  IOllamaRouterApiClient — API keys
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<ApiKeysListResponse> GetApiKeysAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/api-keys",
            OllamaRouterClientJsonContext.Default.ApiKeysListResponse, ct);

    /// <inheritdoc />
    public Task<ApiKeyCreatedResponse> CreateApiKeyAsync(ApiKeyCreateRequest request, CancellationToken ct = default) =>
        SendWithBodyAsync(
            HttpMethod.Post,
            "api/router/api-keys",
            request,
            OllamaRouterClientJsonContext.Default.ApiKeyCreateRequest,
            OllamaRouterClientJsonContext.Default.ApiKeyCreatedResponse,
            ct);

    /// <inheritdoc />
    public Task<ApiKeyDeletedResponse> DeleteApiKeyAsync(string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, BuildPath("api/router/api-keys", name),
            OllamaRouterClientJsonContext.Default.ApiKeyDeletedResponse, ct);

    /// <inheritdoc />
    public Task<ApiKeyRotatedResponse> RotateDefaultKeyAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/router/api-keys/default/rotate",
            OllamaRouterClientJsonContext.Default.ApiKeyRotatedResponse, ct);

    // ------------------------------------------------------------------
    //  IOllamaRouterApiClient — role recommendations (task #66 wire surface)
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<FleetRecommendations> GetRecommendationsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/router/recommendations",
            OllamaRouterClientJsonContext.Default.FleetRecommendations, ct);

    /// <inheritdoc />
    public Task<RouterRecommendationsGeneratedResponse> GenerateRecommendationsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/router/recommendations/generate",
            OllamaRouterClientJsonContext.Default.RouterRecommendationsGeneratedResponse, ct);

    /// <inheritdoc />
    public Task<RouterRecommendationsAppliedResponse> ApplyRecommendationsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/router/recommendations/apply",
            OllamaRouterClientJsonContext.Default.RouterRecommendationsAppliedResponse, ct);

    // ------------------------------------------------------------------
    //  Plumbing
    // ------------------------------------------------------------------

    private Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string path,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct = default,
        Action<HttpRequestMessage>? configure = null) =>
        CoreAsync(method, path, static () => (HttpContent?)null, configure, responseTypeInfo, ct);

    private Task<TResponse> SendWithBodyAsync<TBody, TResponse>(
        HttpMethod method,
        string path,
        TBody? body,
        JsonTypeInfo<TBody> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct = default)
        where TBody : notnull
    {
        // A null body means "no request body at all" (the server treats an absent/empty body
        // as valid on the optional-body endpoints). The content is re-created on the 401
        // retry (a content stream can only be consumed once).
        Func<HttpContent?> createContent = body is null
            ? static () => (HttpContent?)null
            : () => CreateContent(body, requestTypeInfo);
        return CoreAsync(method, path, createContent, null, responseTypeInfo, ct);
    }

    private static ByteArrayContent CreateContent<TBody>(TBody body, JsonTypeInfo<TBody> typeInfo)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, typeInfo));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private async Task<TResponse> CoreAsync<TResponse>(
        HttpMethod method,
        string path,
        Func<HttpContent?> createContent,
        Action<HttpRequestMessage>? configure,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct)
    {
        var response = await SendOnceAsync(method, path, createContent, configure, ResolveKey(), ct)
            .ConfigureAwait(false);

        // 401 auto-retry (default-key auto-rotation): with a key reader the SAME request is
        // retried EXACTLY ONCE with the freshly re-read key. Without a reader (explicit key
        // only) the 401 is surfaced immediately. A cancelled token never retries.
        if (response.StatusCode == HttpStatusCode.Unauthorized && _keyReader is not null)
        {
            response.Dispose();
            ct.ThrowIfCancellationRequested();
            response = await SendOnceAsync(method, path, createContent, configure,
                _keyReader.ReadDefaultKey(), ct).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            var error = await ReadErrorAsync(response).ConfigureAwait(false);
            response.Dispose();
            throw new OllamaRouterApiException(status, error);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync(responseTypeInfo, ct).ConfigureAwait(false)
                ?? throw new OllamaRouterApiException(response.StatusCode, "empty response body");
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string path,
        Func<HttpContent?> createContent,
        Action<HttpRequestMessage>? configure,
        string? key,
        CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (!string.IsNullOrEmpty(key))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        configure?.Invoke(message);
        var content = createContent();
        if (content is not null)
        {
            message.Content = content;
        }

        return await _httpClient.SendAsync(message, ct).ConfigureAwait(false);
    }

    private async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        var raw = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(raw, OllamaRouterClientJsonContext.Default.OllamaErrorResponse);
            if (parsed is null)
            {
                return Truncate(raw);
            }

            // error → message → raw (truncated to 512 chars): the plain `error` shape wins;
            // the Setup API's `{"success": false, "message": "..."}` bodies surface their
            // human-readable message (task #146) instead of the raw JSON; a JSON body
            // carrying neither field falls back to the raw text exactly as before.
            if (!string.IsNullOrWhiteSpace(parsed.Error))
            {
                return parsed.Error;
            }

            if (!string.IsNullOrWhiteSpace(parsed.Message))
            {
                return parsed.Message;
            }

            return Truncate(raw);
        }
        catch (JsonException)
        {
            // Not a JSON {"error": "..."} body — surface the raw text (truncated to 512 chars).
            return Truncate(raw);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 512 ? value : value[..512];

    /// <summary>
    /// The key for this request: the explicit key wins, else the default key freshly re-read
    /// from disk (null when no reader is configured or the file is absent/unreadable — the
    /// request is then sent keyless, the router's default mode).
    /// </summary>
    private string? ResolveKey() => _apiKey ?? _keyReader?.ReadDefaultKey();

    /// <summary>
    /// Builds a relative route path, escaping each segment individually so namespaced
    /// catch-all values (<c>org/model:tag</c> → <c>org/model%3Atag</c>) survive; the server
    /// unescapes the tail. Fixed route segments are identity-escaped.
    /// </summary>
    internal static string BuildPath(params string[] segments) =>
        string.Join("/", segments.Select(EscapeDataSegment));

    internal static string EscapeDataSegment(string value) =>
        Uri.EscapeDataString(value).Replace("%2F", "/");
}
