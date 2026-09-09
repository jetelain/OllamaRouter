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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static (OllamaModelCatalogClient Sut, FakeHttpMessageHandler Handler) CreateSut(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHttpMessageHandler(respond);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        return (new OllamaModelCatalogClient(httpClientFactory.Object), handler);
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
    public async Task GetMergedTagsAsync_UsesGetTagsAsync_ForBothInstances_LocalTakesPriority()
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var localHandler = new FakeHttpMessageHandler(req => req.RequestUri!.ToString().Contains("local")
            ? JsonResponse("""{ "models": [ { "name": "shared", "source": "local" } ] }""")
            : JsonResponse("""{ "models": [] }"""));
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(localHandler));
        var sut = new OllamaModelCatalogClient(httpClientFactory.Object);

        var merged = await sut.GetMergedTagsAsync("http://local", "http://remote");

        Assert.Single(merged);
        Assert.Equal("local", merged[0]!["source"]!.ToString());
    }
}
