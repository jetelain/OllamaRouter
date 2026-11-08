using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;

namespace OllamaRouter.Tests.Services.Catalog;

public class OllamaModelCatalogClientTests
{
    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return respond(request);
        }
    }

    private static (OllamaModelCatalogClient Sut, FakeHttpMessageHandler Handler) CreateSut(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHttpMessageHandler(respond);
        return (new OllamaModelCatalogClient(new HttpClient(handler)), handler);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task GetTagsAsync_ValidResponse_ReturnsModelsArray()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("""{ "models": [ { "name": "llama3" }, { "name": "qwen3:8b" } ] }"""));

        var tags = await sut.GetTagsAsync("http://127.0.0.1:11435");

        Assert.Equal(2, tags.Count);
        Assert.Equal("llama3", tags[0]!["name"]!.ToString());
    }

    [Fact]
    public async Task GetTagsAsync_RequestsCorrectUrl()
    {
        var (sut, handler) = CreateSut(_ => JsonResponse("""{ "models": [] }"""));

        await sut.GetTagsAsync("http://127.0.0.1:11435");

        Assert.Equal("http://127.0.0.1:11435/api/tags", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetTagsAsync_HttpError_ReturnsEmptyArray()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var tags = await sut.GetTagsAsync("http://127.0.0.1:11435");

        Assert.Empty(tags);
    }

    [Fact]
    public async Task GetTagsAsync_InvalidJson_ReturnsEmptyArray()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("not json"));

        var tags = await sut.GetTagsAsync("http://127.0.0.1:11435");

        Assert.Empty(tags);
    }

    [Fact]
    public async Task GetTagsAsync_ThrowingHandler_ReturnsEmptyArray()
    {
        var (sut, _) = CreateSut(_ => throw new HttpRequestException("connection refused"));

        var tags = await sut.GetTagsAsync("http://127.0.0.1:11435");

        Assert.Empty(tags);
    }

    [Fact]
    public async Task GetMergedTagsAsync_UsesGetTagsAsync_ForBothInstances_RemoteTakesPriority()
    {
        var handler = new FakeHttpMessageHandler(req => req.RequestUri!.ToString().Contains("local")
            ? JsonResponse("""{ "models": [ { "name": "shared", "source": "local" } ] }""")
            : JsonResponse("""{ "models": [ { "name": "shared", "source": "remote" } ] }"""));
        var sut = new OllamaModelCatalogClient(new HttpClient(handler));

        var merged = await sut.GetMergedTagsAsync("http://local", "http://remote");

        Assert.Single(merged);
        Assert.Equal("remote", merged[0]!["source"]!.ToString());
    }

    [Fact]
    public async Task GetRunningModelNamesAsync_ValidResponse_ReturnsModelNames()
    {
        var (sut, handler) = CreateSut(_ => JsonResponse("""{ "models": [ { "name": "llama3.2:latest" }, { "name": "qwen3:8b" } ] }"""));

        var models = await sut.GetRunningModelNamesAsync("http://127.0.0.1:11435");

        Assert.Equal(2, models.Count);
        Assert.Contains("llama3.2:latest", models);
        Assert.Contains("qwen3:8b", models);
        Assert.Equal("http://127.0.0.1:11435/api/ps", handler.LastRequest?.RequestUri?.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetRunningModelNamesAsync_NullOrWhitespaceUrl_ReturnsEmpty(string? url)
    {
        var (sut, _) = CreateSut(_ => JsonResponse("""{ "models": [ { "name": "llama3" } ] }"""));

        var models = await sut.GetRunningModelNamesAsync(url);

        Assert.Empty(models);
    }

    [Fact]
    public async Task GetRunningModelNamesAsync_HttpError_ReturnsEmpty()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var models = await sut.GetRunningModelNamesAsync("http://127.0.0.1:11435");

        Assert.Empty(models);
    }

    [Fact]
    public async Task UnloadModelAsync_ValidRequest_SendsCorrectPayloadAndReturnsTrue()
    {
        var (sut, handler) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await sut.UnloadModelAsync("http://127.0.0.1:11435", "llama3.2:latest");

        Assert.True(result);
        Assert.Equal("http://127.0.0.1:11435/api/generate", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);

        var body = handler.LastRequestBody;
        Assert.NotNull(body);
        Assert.Contains("\"model\":\"llama3.2:latest\"", body);
        Assert.Contains("\"keep_alive\":0", body);
        Assert.Contains("\"stream\":false", body);
    }

    [Theory]
    [InlineData(null, "llama3")]
    [InlineData("", "llama3")]
    [InlineData("http://127.0.0.1:11435", null)]
    [InlineData("http://127.0.0.1:11435", "")]
    public async Task UnloadModelAsync_MissingUrlOrModel_ReturnsFalse(string? url, string? model)
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await sut.UnloadModelAsync(url, model!);

        Assert.False(result);
    }

    [Fact]
    public async Task UnloadModelAsync_HttpError_ReturnsFalse()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.UnloadModelAsync("http://127.0.0.1:11435", "llama3.2");

        Assert.False(result);
    }

    [Fact]
    public async Task StopRunningModelsAsync_MultipleModelsLoaded_UnloadsAllAndReturnsNames()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requests.Add(req);
            if (req.RequestUri!.AbsolutePath == "/api/ps")
            {
                return JsonResponse("""{ "models": [ { "name": "llama3.2:latest" }, { "name": "mistral:7b" } ] }""");
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var sut = new OllamaModelCatalogClient(new HttpClient(handler));

        var unloaded = await sut.StopRunningModelsAsync("http://127.0.0.1:11435");

        Assert.Equal(2, unloaded.Count);
        Assert.Contains("llama3.2:latest", unloaded);
        Assert.Contains("mistral:7b", unloaded);
        // 1 GET /api/ps + 2 POST /api/generate
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task StopRunningModelsAsync_NoModelsLoaded_ReturnsEmpty()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("""{ "models": [] }"""));

        var unloaded = await sut.StopRunningModelsAsync("http://127.0.0.1:11435");

        Assert.Empty(unloaded);
    }

    [Fact]
    public async Task GetTagsAsync_TimeoutOrCancellation_ReturnsEmptyArray()
    {
        var (sut, _) = CreateSut(_ => throw new TaskCanceledException("HttpClient timeout"));

        var tags = await sut.GetTagsAsync("http://127.0.0.1:11435");

        Assert.Empty(tags);
    }

    [Fact]
    public async Task GetTagsWithDigestAsync_ValidResponse_ReturnsModelsAndSha256Digest()
    {
        const string raw = """{ "models": [ { "name": "llama3", "size": 1234 } ] }""";
        var (sut, handler) = CreateSut(_ => JsonResponse(raw));

        var (tags, digest) = await sut.GetTagsWithDigestAsync("http://127.0.0.1:11435");

        Assert.Single(tags);
        Assert.Equal("llama3", tags[0]!["name"]!.ToString());
        Assert.Equal("http://127.0.0.1:11435/api/tags", handler.LastRequest?.RequestUri?.ToString());

        // Digest is the 64-char lowercase hex SHA-256 of the raw response body.
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        Assert.Matches("^[0-9a-f]{64}$", digest);
        Assert.Equal(expected, digest);
    }

    [Fact]
    public async Task GetTagsWithDigestAsync_IdenticalBodies_SameDigest_DifferentBodies_DifferentDigest()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse("""{ "models": [ { "name": "llama3" } ] }"""));
        var sut = new OllamaModelCatalogClient(new HttpClient(handler));

        var (_, digest1) = await sut.GetTagsWithDigestAsync("http://127.0.0.1:11435");
        var (_, digest2) = await sut.GetTagsWithDigestAsync("http://127.0.0.1:11435");
        Assert.Equal(digest1, digest2);

        // A different body yields a different digest.
        var otherHandler = new FakeHttpMessageHandler(_ => JsonResponse("""{ "models": [ { "name": "qwen3:8b" } ] }"""));
        var otherSut = new OllamaModelCatalogClient(new HttpClient(otherHandler));
        var (_, digestOther) = await otherSut.GetTagsWithDigestAsync("http://127.0.0.1:11435");
        Assert.NotEqual(digest1, digestOther);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetTagsWithDigestAsync_MissingBaseUrl_ReturnsEmptyArrayAndEmptyDigest(string? url)
    {
        var (sut, _) = CreateSut(_ => throw new HttpRequestException("should not be called"));

        var (tags, digest) = await sut.GetTagsWithDigestAsync(url);

        Assert.Empty(tags);
        Assert.Equal("", digest);
    }

    [Fact]
    public async Task GetTagsWithDigestAsync_HttpError_ReturnsEmptyArrayAndEmptyDigest()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var (tags, digest) = await sut.GetTagsWithDigestAsync("http://127.0.0.1:11435");

        Assert.Empty(tags);
        Assert.Equal("", digest);
    }

    [Fact]
    public async Task GetTagsWithDigestAsync_ThrowingHandler_ReturnsEmptyArrayAndEmptyDigest()
    {
        var (sut, _) = CreateSut(_ => throw new HttpRequestException("connection refused"));

        var (tags, digest) = await sut.GetTagsWithDigestAsync("http://127.0.0.1:11435");

        Assert.Empty(tags);
        Assert.Equal("", digest);
    }

    [Fact]
    public async Task GetShowAsync_ValidRequest_PostsToApiShowWithModelPayload()
    {
        var showJson = """
            {
              "model": { "name": "qwen3:latest", "size": 17305205760 },
              "parameters": "num_ctx 49152\nnum_predict 4096\n",
              "details": { "family": "qwen3", "parameter_size": "27.8B" },
              "model_info": { "general.parameter_count": 27800000000 }
            }
            """;
        var (sut, handler) = CreateSut(_ => JsonResponse(showJson));

        var show = await sut.GetShowAsync("http://127.0.0.1:11435", "qwen3:latest");

        Assert.NotNull(show);
        Assert.Equal("http://127.0.0.1:11435/api/show", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal("qwen3", show?["details"]?["family"]?.ToString());
        Assert.Contains("\"model\":\"qwen3:latest\"", handler.LastRequestBody);
    }

    [Theory]
    [InlineData(null, "llama3")]
    [InlineData("", "llama3")]
    [InlineData("http://127.0.0.1:11435", null)]
    [InlineData("http://127.0.0.1:11435", "")]
    [InlineData("http://127.0.0.1:11435", "   ")]
    public async Task GetShowAsync_MissingUrlOrModel_ReturnsNull(string? url, string? model)
    {
        var (sut, _) = CreateSut(_ => throw new HttpRequestException("should not be called"));

        var show = await sut.GetShowAsync(url, model!);

        Assert.Null(show);
    }

    [Fact]
    public async Task GetShowAsync_HttpError_ReturnsNull()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var show = await sut.GetShowAsync("http://127.0.0.1:11435", "llama3");

        Assert.Null(show);
    }

    [Fact]
    public async Task GetShowAsync_InvalidJson_ReturnsNull()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("not json"));

        var show = await sut.GetShowAsync("http://127.0.0.1:11435", "llama3");

        Assert.Null(show);
    }

    [Fact]
    public async Task GetShowAsync_ThrowingHandler_ReturnsNull()
    {
        var (sut, _) = CreateSut(_ => throw new HttpRequestException("connection refused"));

        var show = await sut.GetShowAsync("http://127.0.0.1:11435", "llama3");

        Assert.Null(show);
    }

    [Fact]
    public async Task GetShowAsync_NonObjectRoot_ReturnsNull()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("[1, 2, 3]"));

        var show = await sut.GetShowAsync("http://127.0.0.1:11435", "llama3");

        Assert.Null(show);
    }

    [Fact]
    public async Task FetchArrayBoundedAsync_ValidArray_ReturnsArrayAndSucceeded()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("""{ "models": [ { "name": "llama3" } ] }"""));

        var (array, succeeded) = await sut.FetchArrayBoundedAsync("http://127.0.0.1:11435", "/api/tags", "models", TimeSpan.FromSeconds(1));

        Assert.True(succeeded);
        Assert.NotNull(array);
        Assert.Equal("llama3", array![0]!["name"]!.ToString());
    }

    [Fact]
    public async Task FetchArrayBoundedAsync_EmptyArray_IsASuccessfulEmptyCatalog_NotAFailure()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("""{ "models": [] }"""));

        var (array, succeeded) = await sut.FetchArrayBoundedAsync("http://127.0.0.1:11435", "/api/tags", "models", TimeSpan.FromSeconds(1));

        Assert.True(succeeded);
        Assert.NotNull(array);
        Assert.Empty(array!);
    }

    [Fact]
    public async Task FetchArrayBoundedAsync_MissingArrayProperty_SurfacesFailure_NotASuccessfulNull()
    {
        // An error body ({"error": ...}) or any shape without the expected root property must be
        // surfaced as a failed fetch so the aggregation falls back to the cached catalog.
        var (sut, _) = CreateSut(_ => JsonResponse("""{ "error": "model not found" }"""));

        var (array, succeeded) = await sut.FetchArrayBoundedAsync("http://127.0.0.1:11435", "/api/tags", "models", TimeSpan.FromSeconds(1));

        Assert.False(succeeded);
        Assert.Null(array);
    }

    [Fact]
    public async Task FetchArrayBoundedAsync_BudgetExceeded_ReturnsFailure()
    {
        // The handler never responds; the 50 ms budget must elapse and surface a failure
        // (never hang the caller) without cancelling the underlying request.
        var client = new OllamaModelCatalogClient(new HttpClient(new NoRespondHandler()));

        var started = Environment.TickCount64;
        var (array, succeeded) = await client.FetchArrayBoundedAsync("http://127.0.0.1:11435", "/api/tags", "models", TimeSpan.FromMilliseconds(50));

        Assert.False(succeeded);
        Assert.Null(array);
        Assert.InRange(Environment.TickCount64 - started, 0, 5000);
    }

    // ------------------------------------------------------------------
    //  PreloadModelAsync (task #44)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PreloadModelAsync_PositiveKeepAliveAndNumCtx_PostsExactGeneratePayload()
    {
        var (sut, handler) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });

        var outcome = await sut.PreloadModelAsync("http://127.0.0.1:11435", "llama3:8b", 900, 8192);

        Assert.True(outcome.Accepted);
        Assert.Null(outcome.Error);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal("http://127.0.0.1:11435/api/generate", handler.LastRequest?.RequestUri?.ToString());

        var body = System.Text.Json.Nodes.JsonNode.Parse(handler.LastRequestBody!)!.AsObject()!;
        Assert.Equal("llama3:8b", body["model"]!.GetValue<string>());
        Assert.Equal(string.Empty, body["prompt"]!.GetValue<string>()); // empty prompt = load-only
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.Equal(900, body["keep_alive"]!.GetValue<int>());
        Assert.Equal(8192, body["options"]!["num_ctx"]!.GetValue<int>());
    }

    [Fact]
    public async Task PreloadModelAsync_IndefiniteKeepAlive_PostsMinusOne()
    {
        var (sut, handler) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });

        var outcome = await sut.PreloadModelAsync("http://127.0.0.1:11435", "qwen3:30b", -1, null);

        Assert.True(outcome.Accepted);
        var body = System.Text.Json.Nodes.JsonNode.Parse(handler.LastRequestBody!)!.AsObject()!;
        Assert.Equal(-1, body["keep_alive"]!.GetValue<int>());

        // No positive num_ctx: the options block is ABSENT entirely (the daemon keeps its
        // Modelfile default — no needless KV reallocation).
        Assert.Null(body["options"]);
    }

    [Fact]
    public async Task PreloadModelAsync_ZeroNumCtx_DoesNotPinNumCtx()
    {
        var (sut, handler) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });

        await sut.PreloadModelAsync("http://127.0.0.1:11435", "llama3:8b", 900, 0);

        var body = System.Text.Json.Nodes.JsonNode.Parse(handler.LastRequestBody!)!.AsObject()!;
        Assert.Null(body["options"]); // 0 is not a positive num_ctx
    }

    [Fact]
    public async Task PreloadModelAsync_MissingBaseUrl_ReturnsFailureWithoutHttp()
    {
        var (sut, handler) = CreateSut(_ => throw new InvalidOperationException("must not be called"));

        var outcome = await sut.PreloadModelAsync(null, "llama3:8b", 900, 8192);

        Assert.False(outcome.Accepted);
        Assert.Equal("no target url or model name", outcome.Error);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task PreloadModelAsync_EmptyModelName_ReturnsFailureWithoutHttp()
    {
        var (sut, handler) = CreateSut(_ => throw new InvalidOperationException("must not be called"));

        var outcome = await sut.PreloadModelAsync("http://127.0.0.1:11435", "  ", 900, 8192);

        Assert.False(outcome.Accepted);
        Assert.Equal("no target url or model name", outcome.Error);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task PreloadModelAsync_HttpError_FailSoftNotAccepted()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var outcome = await sut.PreloadModelAsync("http://127.0.0.1:11435", "llama3:8b", 900, 8192);

        Assert.False(outcome.Accepted);
        Assert.Equal("http 500", outcome.Error);
    }

    [Fact]
    public async Task PreloadModelAsync_TransportException_FailSoftNotAccepted()
    {
        var (sut, _) = CreateSut(_ => throw new HttpRequestException("connection refused"));

        var outcome = await sut.PreloadModelAsync("http://127.0.0.1:11435", "llama3:8b", 900, 8192);

        Assert.False(outcome.Accepted);
        Assert.Equal("connection refused", outcome.Error);
    }

    private sealed class NoRespondHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }

    // ------------------------------------------------------------------
    //  Preload load client wiring (task #75): ONLY PreloadModelAsync rides the dedicated
    //  "PreloadLoad" client; every other catalog call keeps the injected 4 s catalog client.
    // ------------------------------------------------------------------

    /// <summary>
    /// A handler that delays every response by <paramref name="delay"/> (a simulated slow
    /// daemon, e.g. a large-model cold load) before handing to <paramref name="respond"/>; the
    /// delay is cancellation-aware so a client timeout surfaces as a cancellation exactly like
    /// a real stalled peer.
    /// </summary>
    private sealed class DelayingHttpMessageHandler(TimeSpan delay, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            await Task.Delay(delay, cancellationToken);
            return respond(request);
        }
    }

    private static IHttpClientFactory BuildPreloadLoadFactory(TimeSpan timeout, HttpMessageHandler preloadHandler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(OllamaModelCatalogClient.PreloadLoadClientName, client =>
        {
            client.Timeout = timeout;
        })
        .ConfigurePrimaryHttpMessageHandler(() => preloadHandler);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    [Fact]
    public async Task PreloadModelAsync_WithoutPreloadClientFactory_FallsBackToInjectedCatalogClient()
    {
        var (sut, handler) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });

        var outcome = await sut.PreloadModelAsync("http://127.0.0.1:11435", "llama3:8b", 900, 8192);

        Assert.True(outcome.Accepted);
        Assert.Equal("http://127.0.0.1:11435/api/generate", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task PreloadModelAsync_SlowLoadPastOldFourSecondBound_IsAcceptedByTwentySecondPreloadClient()
    {
        // AC2: the simulated daemon answers after ~4.5 s — comfortably past the OLD 4 s catalog
        // client bound. The DI-built "PreloadLoad" client carries the configured 20 s timeout,
        // so the load must SUCCEED, while the injected catalog client observes ZERO requests
        // (it is no longer used for loads).
        var catalogHandler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("the 4 s catalog client must not be used for the preload load call"));
        var preloadHandler = new DelayingHttpMessageHandler(TimeSpan.FromMilliseconds(4500), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var factory = BuildPreloadLoadFactory(TimeSpan.FromSeconds(20), preloadHandler);
        var sut = new OllamaModelCatalogClient(new HttpClient(catalogHandler), factory);

        var started = Environment.TickCount64;
        var outcome = await sut.PreloadModelAsync("http://127.0.0.1:11435", "big-model:latest", -1, 262144);
        var elapsedMs = Environment.TickCount64 - started;

        Assert.True(outcome.Accepted);
        Assert.Null(outcome.Error);
        Assert.InRange(elapsedMs, 4000, 20_000); // the ~4.5 s slow load completed (the old 4 s bound would have canceled it)
        Assert.Null(catalogHandler.LastRequest); // the catalog client was never touched for the load
        Assert.Equal("http://127.0.0.1:11435/api/generate", preloadHandler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task PreloadModelAsync_LoadSlowerThanPreloadBound_FailSoftCancellationWithoutThrow()
    {
        // AC4: the "PreloadLoad" client is bounded to 1 s while the simulated load takes ~5 s —
        // the call must complete in ~1 s (NOT the old 4 s catalog bound) with a fail-soft
        // PreloadOutcome(accepted: false, cancellation message), never throwing.
        var factory = BuildPreloadLoadFactory(TimeSpan.FromSeconds(1),
            new DelayingHttpMessageHandler(TimeSpan.FromSeconds(5), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }));
        var sut = new OllamaModelCatalogClient(
            new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("must not be called"))),
            factory);

        var started = Environment.TickCount64;
        var outcome = await sut.PreloadModelAsync("http://127.0.0.1:11435", "big-model:latest", -1, 262144);
        var elapsedMs = Environment.TickCount64 - started;

        Assert.False(outcome.Accepted);
        Assert.NotNull(outcome.Error);
        Assert.Contains("canceled", outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(elapsedMs, 0, 4000); // bounded by the 1 s preload client timeout
    }

    [Fact]
    public async Task NonLoadCatalogCalls_StillRideTheInjectedCatalogClient_NotThePreloadClient()
    {
        // AC3: the /api/ps residency call (and every other non-load call) keeps the injected
        // catalog client; the dedicated "PreloadLoad" client sees no requests.
        var preloadHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var factory = BuildPreloadLoadFactory(TimeSpan.FromSeconds(20), preloadHandler);
        var catalogHandler = new FakeHttpMessageHandler(_ => JsonResponse("""{ "models": [ { "name": "llama3:8b" } ] }"""));
        var sut = new OllamaModelCatalogClient(new HttpClient(catalogHandler), factory);

        var running = await sut.GetRunningModelNamesAsync("http://127.0.0.1:11435");

        Assert.Equal(["llama3:8b"], running);
        Assert.Equal("http://127.0.0.1:11435/api/ps", catalogHandler.LastRequest?.RequestUri?.ToString());
        Assert.Null(preloadHandler.LastRequest);
    }

    private sealed class SlowFakeHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Simulates an unresponsive provider that observes cancellation (like a dead socket).
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static OllamaTargetOptions OpenAiTarget(string? apiKey, string url = "http://h:8000/v1") =>
        new() { Name = "Provider", Url = url, Type = TargetType.OpenAI, ApiKey = apiKey };

    // --- FetchOpenAiModelsAsync (task #62) -------------------------------------------------

    [Fact]
    public async Task FetchOpenAiModelsAsync_ValidData_ReturnsParsedArray()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("""{ "object": "list", "data": [ { "id": "gpt-4o" }, { "id": "llama-3.3-70b" } ] }"""));

        var (data, succeeded) = await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-test"), TimeSpan.FromSeconds(4));

        Assert.True(succeeded);
        Assert.NotNull(data);
        Assert.Equal(2, data!.Count);
        Assert.Equal("gpt-4o", data[0]!["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task FetchOpenAiModelsAsync_EmptyData_IsASucceededEmptyCatalog()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("""{ "object": "list", "data": [] }"""));

        var (data, succeeded) = await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-test"), TimeSpan.FromSeconds(4));

        Assert.True(succeeded);
        Assert.NotNull(data);
        Assert.Empty(data!);
    }

    [Fact]
    public async Task FetchOpenAiModelsAsync_MissingDataProperty_ReturnsFailedFetch()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("""{ "object": "list" }"""));

        var (data, succeeded) = await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-test"), TimeSpan.FromSeconds(4));

        Assert.False(succeeded);
        Assert.Null(data);
    }

    [Fact]
    public async Task FetchOpenAiModelsAsync_InvalidJson_ReturnsFailedFetch()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("not json"));

        var (data, succeeded) = await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-test"), TimeSpan.FromSeconds(4));

        Assert.False(succeeded);
        Assert.Null(data);
    }

    [Fact]
    public async Task FetchOpenAiModelsAsync_Unauthorized_ReturnsFailedFetch()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var (data, succeeded) = await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-bad"), TimeSpan.FromSeconds(4));

        Assert.False(succeeded);
        Assert.Null(data);
    }

    [Fact]
    public async Task FetchOpenAiModelsAsync_ThrowingHandler_ReturnsFailedFetch()
    {
        var (sut, _) = CreateSut(_ => throw new HttpRequestException("connection refused"));

        var (data, succeeded) = await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-test"), TimeSpan.FromSeconds(4));

        Assert.False(succeeded);
        Assert.Null(data);
    }

    [Fact]
    public async Task FetchOpenAiModelsAsync_ApiKeyConfigured_SendsBearerHeader()
    {
        var (sut, handler) = CreateSut(_ => JsonResponse("""{ "data": [] }"""));

        await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-super-secret"), TimeSpan.FromSeconds(4));

        var authorization = handler.LastRequest?.Headers.Authorization;
        Assert.NotNull(authorization);
        Assert.Equal("Bearer", authorization!.Scheme);
        Assert.Equal("sk-super-secret", authorization.Parameter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FetchOpenAiModelsAsync_ApiKeyBlank_NoAuthorizationHeader(string? apiKey)
    {
        var (sut, handler) = CreateSut(_ => JsonResponse("""{ "data": [] }"""));

        await sut.FetchOpenAiModelsAsync(OpenAiTarget(apiKey), TimeSpan.FromSeconds(4));

        Assert.Null(handler.LastRequest?.Headers.Authorization);
    }

    [Theory]
    [InlineData("http://h:8000/v1", "http://h:8000/v1/models")]
    [InlineData("http://h:8000", "http://h:8000/models")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/models")]
    [InlineData("http://h:8000/v1/", "http://h:8000/v1/models")]
    public async Task FetchOpenAiModelsAsync_ComposesModelsPath_AgainstPathPrefixedBases(string baseUrl, string expectedUrl)
    {
        var (sut, handler) = CreateSut(_ => JsonResponse("""{ "data": [] }"""));

        await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-test", baseUrl), TimeSpan.FromSeconds(4));

        Assert.Equal(expectedUrl, handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task FetchOpenAiModelsAsync_HungProvider_ReturnsFailedFetch_WithinBudget()
    {
        var handler = new SlowFakeHttpMessageHandler();
        var sut = new OllamaModelCatalogClient(new HttpClient(handler));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (data, succeeded) = await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-test"), TimeSpan.FromMilliseconds(300));
        sw.Stop();

        Assert.False(succeeded);
        Assert.Null(data);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Took {sw.Elapsed.TotalMilliseconds:F0} ms; the budget must bound the fetch.");
    }

    [Fact]
    public async Task FetchOpenAiModelsAsync_OuterCancellation_Propagates()
    {
        // A hanging provider + an already-aborted caller: the cancellation must propagate
        // (budget expiries are swallowed, but caller aborts are not).
        var handler = new SlowFakeHttpMessageHandler();
        var sut = new OllamaModelCatalogClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-test"), TimeSpan.FromSeconds(4), cts.Token));
    }

    [Fact]
    public async Task FetchOpenAiModelsAsync_NoUrl_ReturnsFailedFetch_WithoutHttp()
    {
        var (sut, handler) = CreateSut(_ => JsonResponse("""{ "data": [] }"""));

        var (data, succeeded) = await sut.FetchOpenAiModelsAsync(OpenAiTarget("sk-test", ""), TimeSpan.FromSeconds(4));

        Assert.False(succeeded);
        Assert.Null(data);
        Assert.Null(handler.LastRequest);
    }
}
