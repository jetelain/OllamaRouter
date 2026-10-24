using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Services;

public class TargetExecutionTrackerTests : IDisposable
{
    private static readonly RoutingTarget Ta = new("Alpha");
    private static readonly RoutingTarget Tb = new("Beta");
    private static readonly RoutingTarget Tc = new("Gamma");

    private const string AlphaUrl = "http://127.0.0.1:12431";
    private const string BetaUrl = "http://127.0.0.1:12432";
    private const string GammaUrl = "http://127.0.0.1:12433";

    private readonly List<HttpClient> _clients = [];

    /// <summary>
    /// Fake IOllamaModelCatalogClient: only <see cref="FetchArrayBoundedAsync"/> participates;
    /// every other member is a fail-soft stub. Records every call so tests can assert which
    /// targets were (or were not) queried.
    /// </summary>
    private sealed class CountingCatalogClient(Func<string?, (JsonArray? Array, bool Succeeded)> respond) : IOllamaModelCatalogClient
    {
        public int CallCount { get; private set; }
        public List<string> RequestedUrls { get; } = [];

        public Task<(JsonArray? Array, bool Succeeded)> FetchArrayBoundedAsync(string? baseUrl, string path, string arrayPropertyName, TimeSpan budget, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (baseUrl is not null)
            {
                RequestedUrls.Add(baseUrl.TrimEnd('/'));
            }

            return Task.FromResult(respond(baseUrl));
        }

        public Task<JsonArray> GetMergedTagsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default) => Task.FromResult(new JsonArray());
        public Task<JsonArray> GetMergedOpenAIModelsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default) => Task.FromResult(new JsonArray());
        public Task<JsonArray> GetMergedRunningModelsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default) => Task.FromResult(new JsonArray());
        public Task<bool> IsModelLoadedLocallyAsync(string localUrl, string modelName, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<JsonArray> GetTagsAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult(new JsonArray());
        public Task<(JsonArray Tags, string Digest)> GetTagsWithDigestAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult((new JsonArray(), string.Empty));
        public Task<JsonObject?> GetShowAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default) => Task.FromResult<JsonObject?>(null);
        public Task<IReadOnlyList<string>> GetRunningModelNamesAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> UnloadModelAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> StopRunningModelsAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    // No-op catalog cache for the real TargetLivenessService (probe rejoin refreshes must
    // not escape the test).
    private sealed class CountingCatalogCache : IModelCatalogCacheService
    {
        public Task<bool> ModelExistsAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasAnyModelsAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> RefreshAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<(IReadOnlyList<string> Names, string Digest)> RefreshWithDigestAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult((new List<string>() as IReadOnlyList<string>, string.Empty));
        public Task<string> GetCurrentDigestAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult("");
        public string CurrentDigest(string? baseUrl) => "";
        public void RegisterCapabilityCacheKeys(string baseUrl, IEnumerable<string> keys, string digest) { }
        public Task<JsonArray?> GetStaleTagsAsync(string url) => Task.FromResult<JsonArray?>(null);
        public void SetStaleTags(string url, JsonArray tags) { }
        public Task<JsonArray?> GetStaleV1ModelsAsync(string url) => Task.FromResult<JsonArray?>(null);
        public void SetStaleV1Models(string url, JsonArray data) { }
        public Task<JsonArray?> GetStalePsAsync(string url) => Task.FromResult<JsonArray?>(null);
        public void SetStalePs(string url, JsonArray models) { }
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }

    // Builds a real TargetLivenessService whose probe handler succeeds for Alpha/Beta and —
    // when gammaOffline — throws a refused SocketException for Gamma, so the REAL liveness
    // state machine (EnsureKnownAsync probe path) trips Gamma to Offline.
    private async Task<TargetLivenessService> CreateRealLivenessAsync(bool gammaOffline)
    {
        var handler = new FakeProbeHandler(request =>
        {
            if (gammaOffline && request.RequestUri is not null && request.RequestUri.Host == "127.0.0.1" &&
                request.RequestUri.Port == 12433)
            {
                throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok", Encoding.UTF8, "text/plain") };
        });
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        _clients.Add(client);

        var liveness = new TargetLivenessService(
            MsOptions.Create(ThreeTargetOptions()),
            new CountingCatalogCache(),
            client,
            NullLogger<TargetLivenessService>.Instance);

        await liveness.EnsureKnownAsync(Ta);
        await liveness.EnsureKnownAsync(Tb);
        await liveness.EnsureKnownAsync(Tc);
        return liveness;
    }

    private sealed class FakeProbeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.Run(() => respond(request));
        }
    }

    // Records the reset calls made by the tracker so tests can assert which (target, model)
    // leases were invalidated by an eviction between /api/ps refreshes.
    private sealed class RecordingShrinkManager : IContextAutoShrinkManager
    {
        public readonly List<(RoutingTarget Target, string ModelName)> ResetModelCalls = [];
        public readonly List<RoutingTarget> ResetTargetCalls = [];

        public AutoShrinkLeaseDecision Decide(RoutingTarget target, string modelName, ContextSizingDecision engineDecision, DynamicContextOptions dynamic, long estimatedTokens, DateTimeOffset nowUtc)
            => new(engineDecision.InjectedNumCtx, LeaseBucketState.Baseline, false, null);

        public void ResetTargetLeases(RoutingTarget target)
        {
            lock (ResetTargetCalls)
            {
                ResetTargetCalls.Add(target);
            }
        }

        public void ResetModelLease(RoutingTarget target, string modelName)
        {
            lock (ResetModelCalls)
            {
                ResetModelCalls.Add((target, modelName));
            }
        }
    }

    private static OllamaRouterOptions ThreeTargetOptions() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "Alpha", Url = AlphaUrl },
            new OllamaTargetOptions { Name = "Beta", Url = BetaUrl },
            new OllamaTargetOptions { Name = "Gamma", Url = GammaUrl }
        ]
    };

    private static JsonArray Ps(params (string Name, long SizeVram, int? Ctx)[] models)
    {
        var array = new JsonArray();
        foreach (var (name, sizeVram, ctx) in models)
        {
            var obj = new JsonObject { ["name"] = name, ["size_vram"] = sizeVram };
            if (ctx.HasValue)
            {
                obj["num_ctx"] = ctx.Value;
            }

            array.Add(obj);
        }

        return array;
    }

    // ------------------------------------------------------------------
    //  AC1 — fan-out merge over online targets only
    // ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_FansOutOnlyToOnlineTargets_MergesModelsAndSkipsOffline()
    {
        var liveness = await CreateRealLivenessAsync(gammaOffline: true);
        Assert.False(liveness.IsOnline(Tc)); // Sanity: the real prober tripped Gamma.

        var responder = new CountingCatalogClient(baseUrl =>
        {
            if (string.Equals(baseUrl, AlphaUrl, StringComparison.OrdinalIgnoreCase))
            {
                return (Ps(("llama3:latest", 4000, null)), true);
            }

            if (string.Equals(baseUrl, BetaUrl, StringComparison.OrdinalIgnoreCase))
            {
                return (Ps(("qwen3:latest", 2000, null), ("llama3:latest", 1000, null)), true);
            }

            return (null, false); // Should never be asked for Gamma.
        });

        var sut = new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness,
            new Mock<ITargetAvailabilityService>().Object,
            responder,
            NullLogger<TargetExecutionTracker>.Instance);

        await sut.RefreshAsync();

        // The offline target must receive ZERO /api/ps requests; only Alpha and Beta are
        // queried (asserted via the handler's recorded request URLs).
        Assert.DoesNotContain(responder.RequestedUrls, url => string.Equals(url, GammaUrl, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, responder.CallCount);

        var alpha = sut.GetState(Ta)!;
        Assert.Single(alpha.LoadedModels);
        Assert.Equal("llama3:latest", alpha.LoadedModels[0].Name);
        Assert.Equal(4000L, alpha.LoadedModels[0].SizeVramBytes);
        Assert.Equal(4000L, alpha.TotalVramBytes);
        Assert.False(alpha.Stale);
        Assert.NotEqual(DateTimeOffset.MinValue, alpha.LastRefreshedAtUtc);

        var beta = sut.GetState(Tb)!;
        var betaModels = beta.LoadedModels;
        Assert.Equal(2, betaModels.Count);
        // No cross-target dedupe: Beta keeps BOTH of its own entries (llama3 loads on both).
        Assert.Equal("qwen3:latest", beta.LoadedModels[0].Name);
        Assert.Equal(2000L, beta.LoadedModels[0].SizeVramBytes);
        Assert.Equal("llama3:latest", beta.LoadedModels[1].Name);
        Assert.Equal(1000L, beta.LoadedModels[1].SizeVramBytes);
        Assert.Equal(3000L, beta.TotalVramBytes);
        Assert.False(beta.Stale);

        // Every fleet target appears in GetAllStates (Gamma filled in as never-seen/stale).
        var all = sut.GetAllStates();
        Assert.Equal(3, all.Count); // Three fleet targets (Alpha, Beta, Gamma).
        var gamma = all.Single(s => s.Target == Tc);
        Assert.Empty(gamma.LoadedModels);
        Assert.Equal(0L, gamma.TotalVramBytes);
        Assert.True(gamma.Stale);
        Assert.Equal(DateTimeOffset.MinValue, gamma.LastRefreshedAtUtc);
    }

    // ------------------------------------------------------------------
    //  AC2 — TTL / coalescing behavior and stale-on-failure
    // ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_SecondRefreshWithinTtl_IssuesNoNewRequests()
    {
        var catalog = new CountingCatalogClient(_ => (new JsonArray(), true));
        var liveness = new Mock<ITargetLivenessService>();
        SetupLiveness(liveness, includeCloud: false);
        var sut = new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            catalog,
            NullLogger<TargetExecutionTracker>.Instance,
            refreshTtl: TimeSpan.FromSeconds(2));

        await sut.RefreshAsync();
        Assert.Equal(3, catalog.CallCount);

        // Within the 2 s TTL: no new /api/ps requests (stale-while-fresh guard).
        await sut.RefreshAsync();
        await sut.RefreshAsync();
        Assert.Equal(3, catalog.CallCount);
    }

    [Fact]
    public async Task Refresh_AfterTtlElapses_IssuesFreshRequests()
    {
        var catalog = new CountingCatalogClient(_ => (new JsonArray(), true));
        var liveness = new Mock<ITargetLivenessService>();
        SetupLiveness(liveness, includeCloud: false);
        var sut = new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            catalog,
            NullLogger<TargetExecutionTracker>.Instance,
            refreshTtl: TimeSpan.FromMilliseconds(100));

        await sut.RefreshAsync();
        Assert.Equal(3, catalog.CallCount);

        // Advance well past the (shortened) TTL with a generous margin.
        await Task.Delay(600);
        await sut.RefreshAsync();
        Assert.Equal(6, catalog.CallCount);
    }

    [Fact]
    public async Task Refresh_TargetFailure_KeepsPreviousDataAndMarksStale()
    {
        var failing = false;
        var catalog = new CountingCatalogClient(_ =>
            failing
                ? (null, false)
                : (Ps(("llama3:latest", 4000, null)), true));
        var liveness = new Mock<ITargetLivenessService>();
        SetupLiveness(liveness, includeCloud: false);
        var sut = new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            catalog,
            NullLogger<TargetExecutionTracker>.Instance,
            refreshTtl: TimeSpan.FromMilliseconds(100));

        // First refresh succeeds with a populated payload.
        await sut.RefreshAsync();
        var before = sut.GetState(Ta)!;
        Assert.Single(before.LoadedModels);
        Assert.Equal(4000L, before.LoadedModels[0].SizeVramBytes);
        Assert.False(before.Stale);

        // A failed refresh keeps the previous model data and flips Stale to true.
        failing = true;
        // Advance well past the TTL (with a generous margin) so the failure is a fresh fan-out.
        await Task.Delay(600);
        await sut.RefreshAsync();

        var after = sut.GetState(Ta)!;
        Assert.True(after.Stale);
        Assert.Single(after.LoadedModels);
        Assert.Equal("llama3:latest", after.LoadedModels[0].Name);
        Assert.Equal(4000L, after.LoadedModels[0].SizeVramBytes);
        Assert.Equal(4000L, after.TotalVramBytes);
    }

    [Fact]
    public async Task Refresh_ConcurrentCallers_CoalesceIntoSingleFanOut()
    {
        var catalog = new CountingCatalogClient(_ =>
        {
            // Linger so all 8 callers coalesce onto the one in-flight refresh.
            Task.Delay(150).Wait();
            return (new JsonArray(), true);
        });
        var liveness = new Mock<ITargetLivenessService>();
        SetupLiveness(liveness, includeCloud: false);
        var sut = new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            catalog,
            NullLogger<TargetExecutionTracker>.Instance);

        var tasks = Enumerable.Range(0, 8).Select(_ => sut.RefreshAsync()).ToList();
        await Task.WhenAll(tasks);

        Assert.Equal(3, catalog.CallCount); // One fan-out, not eight.
    }

    // ------------------------------------------------------------------
    //  AC3 — in-flight counter balance and floor guard
    // ------------------------------------------------------------------

    [Fact]
    public void InFlight_SuccessFailureAbortDecrements_AllBalanceToZero()
    {
        var sut = CreateSimpleSut();

        // N successful + M failed + K aborted decrements — in the tracker every outcome is
        // the same DecrementInFlight (the middleware's single finally covers all three).
        const int success = 3, failed = 2, aborted = 4;
        var total = success + failed + aborted;
        for (var i = 0; i < total; i++)
        {
            sut.IncrementInFlight(Ta);
        }

        Assert.Equal(total, sut.GetState(Ta)!.InFlightRequests);

        for (var i = 0; i < success; i++)
        {
            sut.DecrementInFlight(Ta); // normal completion
        }

        for (var i = 0; i < failed; i++)
        {
            sut.DecrementInFlight(Ta); // forwarder error
        }

        for (var i = 0; i < aborted; i++)
        {
            sut.DecrementInFlight(Ta); // client aborted stream
        }

        Assert.Equal(0, sut.GetState(Ta)!.InFlightRequests);
        // Other targets are untouched.
        Assert.Equal(0, sut.GetState(Tb)!.InFlightRequests);
    }

    [Fact]
    public void InFlight_UnbalancedDecrements_FloorAtZero()
    {
        var sut = CreateSimpleSut();

        sut.IncrementInFlight(Ta);
        sut.IncrementInFlight(Ta);
        sut.IncrementInFlight(Ta); // 3 increments
        for (var i = 0; i < 5; i++)
        {
            sut.DecrementInFlight(Ta); // 5 decrements (2 unbalanced)
        }

        Assert.Equal(0, sut.GetState(Ta)!.InFlightRequests);
    }

    [Fact]
    public void InFlight_MixedTargets_TrackedIndependently()
    {
        var sut = CreateSimpleSut();

        sut.IncrementInFlight(Ta);
        sut.IncrementInFlight(Ta);
        sut.IncrementInFlight(Tb);
        sut.DecrementInFlight(Ta);

        Assert.Equal(1, sut.GetState(Ta)!.InFlightRequests);
        Assert.Equal(1, sut.GetState(Tb)!.InFlightRequests);
        Assert.Equal(0, sut.GetState(Tc)!.InFlightRequests);

        sut.DecrementInFlight(Ta);
        sut.DecrementInFlight(Tb);

        Assert.Equal(0, sut.GetState(Ta)!.InFlightRequests);
        Assert.Equal(0, sut.GetState(Tb)!.InFlightRequests);
    }

    // ------------------------------------------------------------------
    //  AC4 — high-concurrency snapshot/counter safety
    // ------------------------------------------------------------------

    [Fact]
    public async Task InFlight_HighConcurrencyCountersAndSnapshots_FinalStateIsZero_NoExceptions()
    {
        var sut = CreateSimpleSut();
        var targets = new[] { Ta, Tb, Tc };
        var exceptions = new ConcurrentBag<Exception>();

        // 10 tasks × 50 inc/dec pairs (≥500 pairs total), mixed targets.
        var counterTasks = Enumerable.Range(0, 10).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                var target = targets[(t + i) % targets.Length];
                sut.IncrementInFlight(target);
                sut.DecrementInFlight(target);
            }
        })).ToList();

        // 20 tasks × 50 snapshot calls (1000 concurrent snapshot operations) interleaved
        // with the counters.
        var snapshotTasks = Enumerable.Range(0, 20).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    _ = sut.GetState(targets[(t + i) % targets.Length]);
                    _ = sut.GetAllStates();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToList();

        await Task.WhenAll(counterTasks.Concat(snapshotTasks));

        Assert.Empty(exceptions);
        Assert.Equal(0, sut.GetState(Ta)!.InFlightRequests);
        Assert.Equal(0, sut.GetState(Tb)!.InFlightRequests);
        Assert.Equal(0, sut.GetState(Tc)!.InFlightRequests);
    }

    [Fact]
    public async Task InFlight_ConcurrentCountersPlusRefresh_NoExceptionsAndBalanced()
    {
        var catalog = new CountingCatalogClient(_ => (new JsonArray(), true));
        var liveness = new Mock<ITargetLivenessService>();
        SetupLiveness(liveness, includeCloud: false);
        var sut = new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            catalog,
            NullLogger<TargetExecutionTracker>.Instance,
            refreshTtl: TimeSpan.FromMilliseconds(1));

        var targets = new[] { Ta, Tb, Tc };
        var exceptions = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 10).Select(t => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var target = targets[(t + i) % targets.Length];
                sut.IncrementInFlight(target);
                try
                {
                    await sut.RefreshAsync();
                    _ = sut.GetState(target);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }

                sut.DecrementInFlight(target);
            }
        })).ToList();

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
        Assert.Equal(0, sut.GetState(Ta)!.InFlightRequests);
        Assert.Equal(0, sut.GetState(Tb)!.InFlightRequests);
        Assert.Equal(0, sut.GetState(Tc)!.InFlightRequests);
    }

    // ------------------------------------------------------------------
    //  AC5 — Cloud exclusion
    // ------------------------------------------------------------------

    [Fact]
    public void Cloud_NeverTracked_GetStateReturnsNull_AndCountersAreTolerated()
    {
        var sut = CreateSimpleSut();

        Assert.Null(sut.GetState(RoutingTarget.Cloud));
        Assert.DoesNotContain(sut.GetAllStates(), s => s.Target == RoutingTarget.Cloud);

        // Direct counter calls on Cloud are tolerated harmlessly (floor at 0, never throws).
        sut.IncrementInFlight(RoutingTarget.Cloud);
        sut.IncrementInFlight(RoutingTarget.Cloud);
        sut.DecrementInFlight(RoutingTarget.Cloud);
        sut.DecrementInFlight(RoutingTarget.Cloud);
        sut.DecrementInFlight(RoutingTarget.Cloud); // Unbalanced → floors at 0.
        Assert.Null(sut.GetState(RoutingTarget.Cloud));
    }

    [Fact]
    public void GetState_UnknownNonFleetTarget_ReturnsNull_AndCreatesNoSlot()
    {
        var sut = CreateSimpleSut();
        var unknown = new RoutingTarget("NotInFleet");

        // Unknown (non-fleet) targets are never tracked: no phantom slot is created, and the
        // counter calls on them are inert (floored at 0), so GetAllStates stays in sync.
        Assert.Null(sut.GetState(unknown));
        sut.IncrementInFlight(unknown);
        sut.DecrementInFlight(unknown);
        Assert.Null(sut.GetState(unknown));
        Assert.Equal(3, sut.GetAllStates().Count);
    }

    [Fact]
    public async Task Refresh_NeverQueriesCloud()
    {
        var responder = new CountingCatalogClient(_ => (new JsonArray(), true));

        var liveness = new Mock<ITargetLivenessService>();
        // Even when liveness reports the Cloud pseudo-target as a (routable) fleet member…
        SetupLiveness(liveness, includeCloud: true);
        // …and even when a Cloud target with a URL is configured…
        var options = ThreeTargetOptions();
        options.Targets.Add(new OllamaTargetOptions { Name = "Cloud", Url = "http://cloud.invalid:11434" });
        var sut = new TargetExecutionTracker(
            MsOptions.Create(options),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            responder,
            NullLogger<TargetExecutionTracker>.Instance);

        await sut.RefreshAsync();

        // …the fan-out NEVER queries it.
        Assert.DoesNotContain(responder.RequestedUrls, url => string.Equals(url, "http://cloud.invalid:11434", StringComparison.OrdinalIgnoreCase));
        Assert.Null(sut.GetState(RoutingTarget.Cloud));
    }

    // ------------------------------------------------------------------
    //  Fail-soft parsing (AC1 support)
    // ------------------------------------------------------------------

    [Fact]
    public void ParseModels_MissingAndInvalidFields_FailSoft()
    {
        var models = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "full:entry",
                ["size_vram"] = 123L,
                ["num_ctx"] = 4096
            },
            new JsonObject
            {
                ["model"] = "fallback:entry"
            },
            new JsonObject
            {
                ["name"] = "bad-vram:entry",
                ["size_vram"] = "not-a-number"
            },
            JsonValue.Create(42) // Non-object entries are skipped.
        };

        var parsed = TargetExecutionTracker.ParseModels(models);

        Assert.Equal(3, parsed.Count);
        Assert.Equal("full:entry", parsed[0].Name);
        Assert.Equal(123L, parsed[0].SizeVramBytes);
        Assert.Equal(4096, parsed[0].ContextLength);
        Assert.Equal("fallback:entry", parsed[1].Name);
        Assert.Equal(0L, parsed[1].SizeVramBytes);
        Assert.Null(parsed[1].ContextLength);
        Assert.Equal("bad-vram:entry", parsed[2].Name);
        Assert.Equal(0L, parsed[2].SizeVramBytes);
        Assert.Null(parsed[2].ContextLength);
    }

    [Fact]
    public void GetState_UnseenTarget_ReturnsFillInStaleState()
    {
        var sut = CreateSimpleSut();

        var state = sut.GetState(Tc)!;
        Assert.Empty(state.LoadedModels);
        Assert.Equal(0L, state.TotalVramBytes);
        Assert.True(state.Stale);
        Assert.Equal(DateTimeOffset.MinValue, state.LastRefreshedAtUtc);
        Assert.Equal(0, state.InFlightRequests);
    }

    // ------------------------------------------------------------------
    //  Phase 3 (item 2) — model-eviction lease invalidation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_ModelEvictionBetweenRefreshes_ResetsItsLeaseOnly()
    {
        var firstRefresh = true;
        var catalog = new CountingCatalogClient(baseUrl =>
        {
            if (!string.Equals(baseUrl, AlphaUrl, StringComparison.OrdinalIgnoreCase))
            {
                return (new JsonArray(), true);
            }

            return firstRefresh
                ? (Ps(("a:latest", 1000, null), ("b:latest", 2000, null)), true)
                : (Ps(("a:latest", 1000, null)), true); // b:latest evicted.
        });

        var liveness = new Mock<ITargetLivenessService>();
        SetupLiveness(liveness, includeCloud: false);

        var shrinkManager = new RecordingShrinkManager();
        var sut = new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            catalog,
            NullLogger<TargetExecutionTracker>.Instance,
            refreshTtl: TimeSpan.FromMilliseconds(1),
            contextShrinkManager: shrinkManager);

        await sut.RefreshAsync();
        Assert.Empty(shrinkManager.ResetModelCalls); // First successful refresh: nothing evicted.

        firstRefresh = false;
        await Task.Delay(100); // Well past the (shortened) TTL.
        await sut.RefreshAsync();

        // The evicted model (normalized: tag stripped) had its lease reset; the retained model did not.
        var calls = shrinkManager.ResetModelCalls.ToList();
        Assert.Single(calls);
        Assert.Equal(Ta, calls[0].Target);
        Assert.Equal("b", calls[0].ModelName); // Normalized "b:latest" -> "b".
        Assert.Empty(shrinkManager.ResetTargetCalls);

        // GetEvictedModelNames exposes the same diff for observability.
        var evicted = sut.GetEvictedModelNames(Ta);
        Assert.Equal(["b"], evicted);
    }

    [Fact]
    public async Task Refresh_NoEviction_ReportsNoEvictedModels()
    {
        var catalog = new CountingCatalogClient(_ =>
            (Ps(("a:latest", 1000, null), ("b:latest", 2000, null)), true));
        var liveness = new Mock<ITargetLivenessService>();
        SetupLiveness(liveness, includeCloud: false);

        var shrinkManager = new RecordingShrinkManager();
        var sut = new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            catalog,
            NullLogger<TargetExecutionTracker>.Instance,
            refreshTtl: TimeSpan.FromMilliseconds(1),
            contextShrinkManager: shrinkManager);

        await sut.RefreshAsync();
        Assert.Empty(sut.GetEvictedModelNames(Ta));

        await Task.Delay(100);
        await sut.RefreshAsync();

        // Same model set on both refreshes: no eviction, no lease reset.
        Assert.Empty(sut.GetEvictedModelNames(Ta));
        Assert.Empty(shrinkManager.ResetModelCalls);
    }

    [Fact]
    public async Task Refresh_FailedRefresh_DoesNotResetLeases()
    {
        var failing = false;
        var catalog = new CountingCatalogClient(_ =>
            failing
                ? (null, false)
                : (Ps(("a:latest", 1000, null)), true));
        var liveness = new Mock<ITargetLivenessService>();
        SetupLiveness(liveness, includeCloud: false);

        var shrinkManager = new RecordingShrinkManager();
        var sut = new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            catalog,
            NullLogger<TargetExecutionTracker>.Instance,
            refreshTtl: TimeSpan.FromMilliseconds(1),
            contextShrinkManager: shrinkManager);

        await sut.RefreshAsync();
        // A failed refresh is stale-while-revalidate: the model set is unchanged and no leases
        // are reset (eviction is only concluded from a SUCCESSFUL /api/ps response).
        failing = true;
        await Task.Delay(100);
        await sut.RefreshAsync();

        Assert.Empty(sut.GetEvictedModelNames(Ta));
        Assert.Empty(shrinkManager.ResetModelCalls);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private TargetExecutionTracker CreateSimpleSut()
    {
        var liveness = new Mock<ITargetLivenessService>();
        SetupLiveness(liveness, includeCloud: false);
        return new TargetExecutionTracker(
            MsOptions.Create(ThreeTargetOptions()),
            liveness.Object,
            new Mock<ITargetAvailabilityService>().Object,
            new CountingCatalogClient(_ => (new JsonArray(), true)),
            NullLogger<TargetExecutionTracker>.Instance);
    }

    private static void SetupLiveness(Mock<ITargetLivenessService> liveness, bool includeCloud)
    {
        var fleet = new List<RoutingTarget> { Ta, Tb, Tc };
        if (includeCloud)
        {
            fleet.Add(RoutingTarget.Cloud);
        }

        var entries = fleet
            .ToDictionary(t => t, t => new TargetLivenessEntry(t, TargetLivenessState.Online, null, DateTimeOffset.UtcNow));

        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddMinutes(1)); // Grace window already elapsed.
        liveness.Setup(t => t.GetFleetTargets()).Returns(fleet);
        liveness.Setup(t => t.GetEntries()).Returns(entries);
    }
}
