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
using OllamaRouter.Endpoints.RouterApi.SessionAffinity;
using OllamaRouter.Endpoints.RouterApi.Sessions;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Endpoints.RouterApi.Vram;
using OllamaRouter.Middleware;
using OllamaRouter.Security;
using OllamaRouter.Services.Hardware;

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
    /// <c>POST /api/router/models/{*modelName}/preload</c>: loads the model on the selected fleet
    /// target. <paramref name="request"/> may be null (an absent body is valid; the server then
    /// uses the query-string / class defaults).
    /// </summary>
    Task<RouterModelPreloadResponse> PreloadModelAsync(
        string modelName, RouterModelPreloadRequest? request = null, CancellationToken ct = default);

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

    // ------------------------------------------------------------------
    //  Benchmarks
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>POST /api/router/benchmark</c> (202): queues a benchmark run.
    /// <paramref name="request"/> may be null (an absent body is valid → all effective targets).
    /// </summary>
    Task<RouterBenchmarkRunResponse> StartBenchmarkAsync(
        RouterBenchmarkRequest? request = null, CancellationToken ct = default);

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
            return string.IsNullOrWhiteSpace(parsed?.Error) ? Truncate(raw) : parsed!.Error;
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
