using Microsoft.Extensions.AI;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services;
using OllamaRouter.Services.Clients;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Task #61: <see cref="TargetChatClientProvider"/> — live per-name resolution of a target's
/// <see cref="IChatClient"/> (Ollama → pooled <c>OllamaChatClient</c>, OpenAI → pooled
/// <c>OpenAiHttpChatClient</c>, OsNative → no client) plus the options-change RECONCILIATION
/// that drops the pooled clients of removed/reconfigured targets (no connection-pool leaks).
/// </summary>
public class TargetChatClientProviderTests
{
    private sealed class RecordingOllamaFactory : IOllamaChatClientFactory
    {
        private int _counter;
        private readonly Dictionary<Uri, StubChatClient> _pool = new();

        public List<Uri> GetCalls { get; } = [];
        public List<Uri> DropCalls { get; } = [];
        public int CreateCount { get; private set; }

        public IChatClient GetClient(Uri baseAddress)
        {
            GetCalls.Add(baseAddress);
            // Mirror the real factory's pooling contract: one client per base address.
            if (!_pool.TryGetValue(baseAddress, out var client))
            {
                client = new StubChatClient($"ollama-{Interlocked.Increment(ref _counter)}");
                CreateCount++;
                _pool[baseAddress] = client;
            }

            return client;
        }

        public void DropClient(Uri baseAddress)
        {
            DropCalls.Add(baseAddress);
            _pool.Remove(baseAddress); // the real factory evicts the pooled client
        }
    }

    private sealed class RecordingOpenAiFactory : IOpenAiChatClientFactory
    {
        private int _counter;

        public List<OpenAiTargetEndpoint> GetCalls { get; } = [];
        public List<OpenAiTargetEndpoint> DropCalls { get; } = [];

        public IChatClient GetClient(OpenAiTargetEndpoint endpoint)
        {
            GetCalls.Add(endpoint);
            return new StubChatClient($"openai-{Interlocked.Increment(ref _counter)}");
        }

        public void DropClient(OpenAiTargetEndpoint endpoint) => DropCalls.Add(endpoint);
    }

    private sealed class StubChatClient(string id) : IChatClient
    {
        public string Id { get; } = id;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private static (OllamaRouterOptions Options, TestOptionsMonitor<OllamaRouterOptions> Monitor) OptionsWithTargets(
        params OllamaTargetOptions[] targets)
    {
        var options = new OllamaRouterOptions { Targets = targets.ToList() };
        return (options, new TestOptionsMonitor<OllamaRouterOptions>(options));
    }

    [Fact]
    public void TryResolve_OllamaTarget_ReturnsPooledClientPerBaseAddress()
    {
        var (_, monitor) = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "Local",
            Url = "http://daemon:11434/",
            Type = TargetType.Ollama
        });
        var ollamaFactory = new RecordingOllamaFactory();
        using var provider = new TargetChatClientProvider(monitor, new RecordingOpenAiFactory(), ollamaFactory);

        Assert.True(provider.TryResolve("LOCAL", out var client)); // case-insensitive name match
        Assert.NotNull(client);
        Assert.Same(client, provider.GetClient("local")); // pooled: the same instance is returned
        Assert.Equal(1, ollamaFactory.CreateCount); // the pool minted exactly one client
        Assert.All(ollamaFactory.GetCalls, uri => Assert.Equal(new Uri("http://daemon:11434/"), uri));
    }

    [Fact]
    public void TryResolve_OpenAiTarget_PassesProviderLabelUrlAndKeyToTheFactory()
    {
        var (_, monitor) = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "OpenRouter",
            Url = "https://openrouter.ai/api",
            Type = TargetType.OpenAI,
            ApiKey = "sk-secret",
            Provider = "openrouter"
        });
        var openAiFactory = new RecordingOpenAiFactory();
        using var provider = new TargetChatClientProvider(monitor, openAiFactory, new RecordingOllamaFactory());

        Assert.True(provider.TryResolve("OpenRouter", out _));

        var call = Assert.Single(openAiFactory.GetCalls);
        Assert.Equal("openrouter", call.TargetName); // provider label wins over the target name
        Assert.Equal(new Uri("https://openrouter.ai/api"), call.BaseAddress);
        Assert.Equal("sk-secret", call.ApiKey);
        Assert.Empty(openAiFactory.DropCalls);
    }

    [Fact]
    public void TryResolve_OpenAiTargetWithoutProviderLabel_FallsBackToTargetName()
    {
        var (_, monitor) = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "MyCloud",
            Url = "http://cloud:8000/v1",
            Type = TargetType.OpenAI
        });
        var openAiFactory = new RecordingOpenAiFactory();
        using var provider = new TargetChatClientProvider(monitor, openAiFactory, new RecordingOllamaFactory());

        Assert.True(provider.TryResolve("MyCloud", out _));
        Assert.Equal("MyCloud", openAiFactory.GetCalls.Single().TargetName);
    }

    [Fact]
    public void TryResolve_OsNativeTarget_ReturnsFalse_NeverResolvesAClient()
    {
        var (_, monitor) = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "Native",
            Url = "native://inprocess",
            Type = TargetType.OsNative
        });
        var ollamaFactory = new RecordingOllamaFactory();
        var openAiFactory = new RecordingOpenAiFactory();
        using var provider = new TargetChatClientProvider(monitor, openAiFactory, ollamaFactory);

        Assert.False(provider.TryResolve("Native", out var client));
        Assert.Null(client);
        Assert.Null(provider.GetClient("Native"));
        Assert.Empty(ollamaFactory.GetCalls);
        Assert.Empty(openAiFactory.GetCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_BlankName_ReturnsFalse(string? name)
    {
        var (_, monitor) = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "Local",
            Url = "http://daemon:11434"
        });
        using var provider = new TargetChatClientProvider(monitor, new RecordingOpenAiFactory(), new RecordingOllamaFactory());
        Assert.False(provider.TryResolve(name!, out _));
    }

    [Fact]
    public void TryResolve_UnknownTarget_ReturnsFalse()
    {
        var (_, monitor) = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "Local",
            Url = "http://daemon:11434"
        });
        using var provider = new TargetChatClientProvider(monitor, new RecordingOpenAiFactory(), new RecordingOllamaFactory());
        Assert.False(provider.TryResolve("Ghost", out _));
        Assert.Null(provider.GetClient("Ghost"));
    }

    [Fact]
    public void OptionsChange_RemovedTarget_DropsItsPooledClient()
    {
        var (_, monitor) = OptionsWithTargets(
            new OllamaTargetOptions { Name = "Local", Url = "http://daemon-a:11434" },
            new OllamaTargetOptions { Name = "Remote", Url = "http://daemon-b:11434" });
        var ollamaFactory = new RecordingOllamaFactory();
        using var provider = new TargetChatClientProvider(monitor, new RecordingOpenAiFactory(), ollamaFactory);
        provider.GetClient("Local");
        provider.GetClient("Remote");
        Assert.Equal(2, ollamaFactory.GetCalls.Count);

        // Setup-API removal: the target list no longer contains "Remote".
        var updated = new OllamaRouterOptions
        {
            Targets = [new OllamaTargetOptions { Name = "Local", Url = "http://daemon-a:11434" }]
        };
        monitor.SetValue(updated);

        var dropped = Assert.Single(ollamaFactory.DropCalls);
        Assert.Equal(new Uri("http://daemon-b:11434"), dropped);
    }

    [Fact]
    public void OptionsChange_ReconfiguredUrl_DropsTheStalePooledClient()
    {
        var (_, monitor) = OptionsWithTargets(
            new OllamaTargetOptions { Name = "Local", Url = "http://daemon-a:11434" });
        var ollamaFactory = new RecordingOllamaFactory();
        using var provider = new TargetChatClientProvider(monitor, new RecordingOpenAiFactory(), ollamaFactory);
        provider.GetClient("Local");

        var updated = new OllamaRouterOptions
        {
            Targets = [new OllamaTargetOptions { Name = "Local", Url = "http://daemon-b:11434" }]
        };
        monitor.SetValue(updated);

        var dropped = Assert.Single(ollamaFactory.DropCalls);
        Assert.Equal(new Uri("http://daemon-a:11434"), dropped);

        // The next resolution uses the NEW address.
        provider.GetClient("Local");
        Assert.Equal(new Uri("http://daemon-b:11434"), ollamaFactory.GetCalls[^1]);
    }

    [Fact]
    public void OptionsChange_OpenAiKeyChange_DropsTheOpenAiPooledClient()
    {
        var (_, monitor) = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "OpenRouter",
            Url = "https://openrouter.ai/api",
            Type = TargetType.OpenAI,
            ApiKey = "sk-old"
        });
        var openAiFactory = new RecordingOpenAiFactory();
        using var provider = new TargetChatClientProvider(monitor, openAiFactory, new RecordingOllamaFactory());
        provider.GetClient("OpenRouter");
        Assert.Equal("sk-old", openAiFactory.GetCalls.Single().ApiKey);

        var updated = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions
                {
                    Name = "OpenRouter",
                    Url = "https://openrouter.ai/api",
                    Type = TargetType.OpenAI,
                    ApiKey = "sk-new"
                }
            ]
        };
        monitor.SetValue(updated);

        var dropped = Assert.Single(openAiFactory.DropCalls);
        Assert.Equal("sk-old", dropped.ApiKey);

        provider.GetClient("OpenRouter");
        Assert.Equal("sk-new", openAiFactory.GetCalls[^1].ApiKey);
    }

    [Fact]
    public void OptionsChange_UnrelatedEdit_DropsNothing()
    {
        var (_, monitor) = OptionsWithTargets(
            new OllamaTargetOptions { Name = "Local", Url = "http://daemon-a:11434", MaxParallel = 1 });
        var ollamaFactory = new RecordingOllamaFactory();
        var openAiFactory = new RecordingOpenAiFactory();
        using var provider = new TargetChatClientProvider(monitor, openAiFactory, ollamaFactory);
        provider.GetClient("Local");

        var updated = new OllamaRouterOptions
        {
            Targets = [new OllamaTargetOptions { Name = "Local", Url = "http://daemon-a:11434", MaxParallel = 4 }]
        };
        monitor.SetValue(updated);

        Assert.Empty(ollamaFactory.DropCalls);
        Assert.Empty(openAiFactory.DropCalls);
    }
}
