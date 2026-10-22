using System.Net;
using System.Text;
using Moq;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

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
}
