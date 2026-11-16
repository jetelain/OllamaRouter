using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Models;

/// <inheritdoc cref="IModelPullService"/>
/// <para>
/// The pull transport is the dedicated <c>"ModelPull"</c> <see cref="HttpClient"/>
/// (infinite client-level timeout — pulls are legitimately long; every call is bounded by
/// the request <see cref="CancellationToken"/> and the alias step additionally by the
/// 10-minute <see cref="AliasCreateBudget"/>). Every daemon body is serialized through the
/// source-generated <see cref="OllamaRouterJsonSerializerContext"/> (AOT-safe).
/// </para>
/// </summary>
public sealed class ModelPullService : IModelPullService
{
    /// <summary>The daemon-side budget of ONE alias <c>POST /api/create</c> call.</summary>
    internal static readonly TimeSpan AliasCreateBudget = TimeSpan.FromMinutes(10);

    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly IModelCatalogCacheService? _catalogCache;
    private readonly ITargetLivenessService? _liveness;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ModelPullService> _logger;

    public ModelPullService(
        IOptionsMonitor<OllamaRouterOptions> options,
        IModelCatalogCacheService? catalogCache,
        ITargetLivenessService? liveness,
        HttpClient httpClient,
        ILogger<ModelPullService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _catalogCache = catalogCache;
        _liveness = liveness;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<ModelPullTargetResolution> ResolveAsync(string? targetName, CancellationToken cancellationToken = default)
    {
        var targets = _options.CurrentValue.GetEffectiveTargets();

        OllamaTargetOptions? target;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            // Primary-target fallback — the exact pinned-class semantics of the management
            // passthrough ("Local" when present+enabled, else first enabled, else first).
            target = targets.FirstOrDefault(t => t.Enabled &&
                         string.Equals(t.Name, OllamaReverseProxyConfig.LocalTarget, StringComparison.OrdinalIgnoreCase))
                     ?? targets.FirstOrDefault(t => t.Enabled)
                     ?? targets.FirstOrDefault();
            if (target is null)
            {
                return new ModelPullTargetResolution(null, 404, "no fleet targets configured");
            }
        }
        else
        {
            target = targets.FirstOrDefault(t => string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return new ModelPullTargetResolution(null, 404, $"target not found: {targetName}");
            }
        }

        if (target.Type != TargetType.Ollama)
        {
            return new ModelPullTargetResolution(
                null, 400,
                $"target '{target.Name}' is not an Ollama-protocol target (pull is Ollama-protocol only)");
        }

        if (!target.Enabled)
        {
            return new ModelPullTargetResolution(null, 409, $"target '{target.Name}' is disabled");
        }

        // Fresh liveness probe (the liveness service coalesces concurrent probes for the same
        // target; a minimal host without the service skips the gate — the daemon call itself
        // would then fail into a terminal 'error' event).
        if (_liveness is not null)
        {
            var state = await _liveness.EnsureKnownAsync((RoutingTarget)target.Name!, cancellationToken).ConfigureAwait(false);
            if (state == TargetLivenessState.Offline)
            {
                return new ModelPullTargetResolution(null, 409, $"target '{target.Name}' is offline");
            }
        }

        return new ModelPullTargetResolution(target, null, null);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<OllamaRouterModelPullEvent> PullAsync(
        string model,
        OllamaTargetOptions target,
        int? aliasContext,
        string? alias,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var name = target.Name!;
        var url = target.Url!.TrimEnd('/');
        var stopwatch = Stopwatch.StartNew();
        yield return new OllamaRouterModelPullEvent("started", Target: name, Model: model);

        // 1. Presence short-circuit (the 30 s single-flight catalog cache): re-runs skip the
        //    daemon pull entirely; a stale cache just costs one daemon no-op pull.
        if (await IsModelPresentAsync(url, model, cancellationToken).ConfigureAwait(false))
        {
            yield return new OllamaRouterModelPullEvent("present", Target: name, Model: model);
        }
        else
        {
            // Stream the daemon's NDJSON progress lines LIVE (each daemon line reaches the
            // client the moment the daemon emits it — the whole point of the streaming
            // endpoint; a GB-scale pull must show progress WHILE it runs). The manual
            // enumerator keeps every yield OUTSIDE any try-with-catch (a yield in a
            // try/catch is CS1626) while still mapping the daemon/transport failures to a
            // terminal 'error' event with stage "pull".
            var pullError = string.Empty;
            var daemonFailed = false;
            var enumerator = DaemonPullStreamAsync(url, name, model, cancellationToken).GetAsyncEnumerator();
            try
            {
                while (true)
                {
                    bool hasEvent;
                    OllamaRouterModelPullEvent? current = null;
                    try
                    {
                        hasEvent = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // the stream simply ends; the client surfaces the cancellation
                    }
                    catch (PullDaemonFailureException ex)
                    {
                        daemonFailed = true;
                        pullError = ex.Error;
                        hasEvent = false;
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogWarning(ex, "Model pull for {Model} on target {Target} failed (transport).", model, name);
                        daemonFailed = true;
                        pullError = ex.Message;
                        hasEvent = false;
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Model pull for {Model} on target {Target} failed (stream).", model, name);
                        daemonFailed = true;
                        pullError = ex.Message;
                        hasEvent = false;
                    }

                    if (!hasEvent)
                    {
                        break; // clean end of the daemon stream (or the mapped failure below)
                    }

                    current = enumerator.Current;
                    yield return current; // LIVE — outside any try-with-catch
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (daemonFailed)
            {
                yield return new OllamaRouterModelPullEvent(
                    "error", Target: name, Model: model, Stage: "pull", Error: pullError);
                yield break;
            }
        }

        // 2. The context alias (maintainer decision: "the goal is to create an alias with the
        //    context"): a thin Modelfile over the pulled model pinning num_ctx. Idempotent:
        //    an already-present alias (a re-run right after the install) is adopted without a
        //    daemon call.
        if (aliasContext is { } context && alias is not null)
        {
            yield return new OllamaRouterModelPullEvent("alias_started", Target: name, Alias: alias, Context: context);
            if (await IsModelPresentAsync(url, alias, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Alias {Alias} already present on target {Target}; skipping /api/create.", alias, name);
            }
            else
            {
                var aliasError = await CreateAliasAsync(url, name, model, alias, context, cancellationToken).ConfigureAwait(false);
                if (aliasError is not null)
                {
                    yield return new OllamaRouterModelPullEvent(
                        "error", Target: name, Model: model, Stage: "alias", Error: aliasError);
                    yield break;
                }
            }

            yield return new OllamaRouterModelPullEvent("alias_created", Target: name, Alias: alias, Context: context);
        }

        stopwatch.Stop();
        yield return new OllamaRouterModelPullEvent(
            "success", Target: name, Model: model, Alias: alias, DurationMs: stopwatch.ElapsedMilliseconds);

        // 3. Best-effort catalog refresh so the cartography matrix shows the model + alias
        //    immediately (failure logged, NEVER fails the install).
        if (_catalogCache is not null)
        {
            try
            {
                await _catalogCache.RefreshAsync(target.Url, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The install already succeeded; a cancelled refresh is not an error condition.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog refresh after model pull failed for target {Target}.", name);
            }
        }
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// The daemon's <c>POST /api/pull</c> (stream: true) as an async event stream: one
    /// <c>progress</c> event per daemon progress line. A daemon <c>{"error": "..."}</c> line
    /// or a non-2xx response throws <see cref="PullDaemonFailureException"/> (the caller maps
    /// it to the terminal <c>error</c> event with stage "pull"); transport failures
    /// (<c>HttpRequestException</c> / <c>IOException</c>) and cancellation propagate as-is.
    /// The terminal <c>{"status":"success"}</c> line ends the stream silently (success is
    /// reported by the <c>success</c> event in <see cref="PullAsync"/> after the alias step).
    /// </summary>
    private async IAsyncEnumerable<OllamaRouterModelPullEvent> DaemonPullStreamAsync(
        string url, string targetName, string model, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new OllamaDaemonPullRequest(model, true),
            OllamaRouterJsonSerializerContext.Default.OllamaDaemonPullRequest);
        // ResponseHeadersRead: PostAsync (the default ResponseContentRead) would BUFFER THE
        // ENTIRE pull stream before returning — defeating the live progress forwarding.
        // The body then streams under <paramref name="cancellationToken"/> (the client-level
        // timeout is infinite; the request token bounds it).
        using var response = await _httpClient
            .SendAsync(
                new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(url), "api/pull")) { Content = new ByteArrayContent(body) },
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadDaemonErrorAsync(response, cancellationToken).ConfigureAwait(false);
            throw new PullDaemonFailureException(error ?? $"daemon returned {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            OllamaDaemonLineEvent? evt = null;
            try
            {
                evt = JsonSerializer.Deserialize(line, OllamaRouterJsonSerializerContext.Default.OllamaDaemonLineEvent);
            }
            catch (JsonException)
            {
                // Skip a malformed line (a daemon error line is always well-formed JSON).
            }

            if (evt is null)
            {
                continue;
            }

            if (evt.Error is { } error)
            {
                throw new PullDaemonFailureException(error);
            }

            if (evt.Status is { } status and not "success")
            {
                yield return new OllamaRouterModelPullEvent(
                    "progress",
                    Target: targetName,
                    Status: status,
                    Digest: evt.Digest,
                    TotalBytes: evt.Total,
                    CompletedBytes: evt.Completed);
            }
        }
    }

    /// <summary>The terminal pull failure of the daemon pull stream (error text from the daemon's
    /// <c>{"error": "..."}</c> line or the non-2xx response body).</summary>
    private sealed class PullDaemonFailureException : Exception
    {
        public PullDaemonFailureException(string error) : base(error) => Error = error;

        public string Error { get; }
    }

    private async Task<bool> IsModelPresentAsync(string url, string modelName, CancellationToken cancellationToken)
    {
        if (_catalogCache is null)
        {
            return false;
        }

        try
        {
            return await _catalogCache.ModelExistsAsync(url, modelName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail open: an unreachable cache probe must not block a real pull.
            _logger.LogDebug(ex, "Catalog presence probe failed for {Model} on {Url}; proceeding to pull.", modelName, url);
            return false;
        }
    }

    private async Task<string?> CreateAliasAsync(
        string url, string targetName, string model, string alias, int context, CancellationToken cancellationToken)
    {
        using var aliasCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        aliasCts.CancelAfter(AliasCreateBudget);
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(
                new OllamaDaemonCreateRequest(alias, model, new OllamaDaemonCreateParameters(context), false),
                OllamaRouterJsonSerializerContext.Default.OllamaDaemonCreateRequest);
            using var response = await _httpClient
                .PostAsync(new Uri(new Uri(url), "api/create"), new ByteArrayContent(body), aliasCts.Token)
                .ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(aliasCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var error = ParseDaemonErrorPayload(payload) ?? $"daemon returned {(int)response.StatusCode}";
            // Idempotency tolerance: a re-run create on an existing alias is reported as
            // success (the alias exists — which is exactly the desired end state).
            if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return error;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the outer cancellation ends the stream silently
        }
        catch (OperationCanceledException)
        {
            return $"alias creation timed out after {AliasCreateBudget.TotalMinutes:0} min";
        }
        catch (HttpRequestException ex)
        {
            return ex.Message;
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
    }

    private static async Task<string?> ReadDaemonErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseDaemonErrorPayload(payload) ?? Truncate(payload) ?? $"daemon returned {(int)response.StatusCode}";
        }
        catch
        {
            return $"daemon returned {(int)response.StatusCode}";
        }
    }

    private static string? ParseDaemonErrorPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(payload, OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse);
            return string.IsNullOrWhiteSpace(parsed?.Error) ? null : parsed!.Error;
        }
        catch (JsonException)
        {
            return Truncate(payload);
        }
    }

    private static string? Truncate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value!.Length <= 512 ? value : value[..512];
}
