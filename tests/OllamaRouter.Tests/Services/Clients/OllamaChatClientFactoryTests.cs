using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.AI;
using OllamaRouter.Services.Clients;

namespace OllamaRouter.Tests.Services.Clients;

/// <summary>
/// Task #111: <see cref="OllamaChatClientFactory"/> — pooling of the native OllamaSharp-backed
/// <see cref="OllamaChatClient"/> adapters per normalized base URI. Covers pool-key normalization
/// (scheme case / trailing slash / default port never double-pool), instance reuse, the
/// codebase-standard handler policy (10 s lifetime, 2 min idle, infinite client timeout), and
/// the non-owning disposal contract (consumer disposal must never tear down the shared handler;
/// DropClient / factory Dispose must).
/// </summary>
public class OllamaChatClientFactoryTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static (OllamaChatClientFactory Factory, List<RecordingHandler> Handlers, RecordingHandler ImageHandler) CreateTestFactory()
    {
        var handlers = new List<RecordingHandler>();
        var imageHandler = new RecordingHandler();
        var factory = new OllamaChatClientFactory(
            _ =>
            {
                var handler = new RecordingHandler();
                handlers.Add(handler);
                return handler;
            },
            () => imageHandler);
        return (factory, handlers, imageHandler);
    }

    [Fact]
    public void GetClient_ReturnsSameInstanceForEquivalentUris()
    {
        var (factory, handlers, _) = CreateTestFactory();
        using (factory)
        {
            var client = factory.GetClient(new Uri("http://Daemon:11434"));
            Assert.IsType<OllamaChatClient>(client);

            // Authority case and trailing-slash differences must resolve to the same pooled
            // entry (one handler, one client per daemon).
            Assert.Same(client, factory.GetClient(new Uri("HTTP://DAEMON:11434")));
            Assert.Same(client, factory.GetClient(new Uri("http://daemon:11434")));
            Assert.Same(client, factory.GetClient(new Uri("http://daemon:11434/")));

            // Scheme-default port equivalence (80 for http).
            var defaultPort = factory.GetClient(new Uri("http://hostA:80"));
            Assert.Same(defaultPort, factory.GetClient(new Uri("http://hostA")));

            // A different host is a different entry.
            Assert.NotSame(client, factory.GetClient(new Uri("http://other:11434")));
            Assert.Equal(3, handlers.Count);
            Assert.All(handlers, h => Assert.False(h.Disposed));
        }
    }

    [Fact]
    public void GetClient_HttpsDefaultPort_NotConfusedWithExplicit443()
    {
        var (factory, handlers, _) = CreateTestFactory();
        using (factory)
        {
            var client = factory.GetClient(new Uri("https://daemon.example:443/"));
            Assert.Same(client, factory.GetClient(new Uri("https://daemon.example")));
            Assert.Single(handlers);
        }
    }

    [Fact]
    public void ProductionDefault_StandardPoolingPolicyOnHandlerAndInfiniteClientTimeout()
    {
        using var factory = new OllamaChatClientFactory();
        factory.GetClient(new Uri("http://a:11434"));

        var entry = GetSingleEntry(factory);
        var httpClient = Assert.IsType<HttpClient>(entry.GetType().GetProperty("HttpClient")!.GetValue(entry));
        // Long generations must survive: no client-level timeout.
        Assert.Equal(Timeout.InfiniteTimeSpan, httpClient.Timeout);

        // The production constructor wires in CreateStandardHandler() (10 s lifetime / 2 min idle).
        var handler = OllamaChatClientFactory.CreateStandardHandler();
        Assert.Equal(TimeSpan.FromSeconds(10), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionIdleTimeout);
    }

    [Fact]
    public async Task GetClient_PathPrefixedBase_ComposesApiChatCorrectly()
    {
        var (factory, handlers, _) = CreateTestFactory();
        using (factory)
        {
            var client = factory.GetClient(new Uri("http://daemon:11434/ollama"));
            await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
                new ChatOptions { ModelId = "m" }).ToListAsync();

            // The factory slash-terminates the BaseAddress so "api/chat" composes to
            // /ollama/api/chat (RFC 3986 relative merge would otherwise drop /ollama).
            Assert.Equal("/ollama/api/chat", handlers[0].RequestPaths.Single());
        }
    }

    [Fact]
    public void DropClient_RemovesAndDisposesHandler_AndSubsequentGetClientCreatesFreshEntry()
    {
        var (factory, handlers, _) = CreateTestFactory();
        using (factory)
        {
            var first = factory.GetClient(new Uri("http://daemon:11434"));
            Assert.Single(handlers);

            factory.DropClient(new Uri("http://daemon:11434/")); // equivalent URI — same key
            Assert.True(handlers[0].Disposed);
            Assert.Empty(GetPool(factory));

            var second = factory.GetClient(new Uri("http://daemon:11434"));
            Assert.NotSame(first, second);
            Assert.Equal(2, handlers.Count);
            Assert.False(handlers[1].Disposed);
        }
    }

    [Fact]
    public void Dispose_DisposesEveryEntryAndTheSharedImageFetchHandler()
    {
        var (factory, handlers, imageHandler) = CreateTestFactory();
        factory.GetClient(new Uri("http://a:11434"));
        factory.GetClient(new Uri("http://b:11434"));

        factory.Dispose();

        Assert.All(handlers, h => Assert.True(h.Disposed));
        Assert.True(imageHandler.Disposed);
    }

    [Fact]
    public void ConsumerDisposingAPooledClient_NeverDisposesTheSharedHandler()
    {
        var (factory, handlers, _) = CreateTestFactory();
        using (factory)
        {
            var client = factory.GetClient(new Uri("http://daemon:11434"));
            ((IDisposable)client).Dispose(); // e.g. an MEAI consumer dropping its pooled client mid-fleet

            Assert.False(handlers[0].Disposed);

            // The pooled client is still served from the pool afterwards.
            Assert.Same(client, factory.GetClient(new Uri("http://daemon:11434")));
        }
    }

    private static object GetSingleEntry(OllamaChatClientFactory factory)
    {
        var entries = GetPool(factory);
        Assert.Single(entries);
        return entries[0];
    }

    private static List<object> GetPool(OllamaChatClientFactory factory)
    {
        var clientsField = typeof(OllamaChatClientFactory).GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dictionary = Assert.IsAssignableFrom<System.Collections.IDictionary>(clientsField.GetValue(factory)!);
        return dictionary.Keys.Cast<object>()
            .Select(key => dictionary[key]!)
            .ToList();
    }
}
