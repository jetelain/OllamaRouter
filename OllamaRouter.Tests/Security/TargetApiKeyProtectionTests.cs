using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Configuration;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Security;

/// <summary>
/// Task #61 (maintainer decision 3): the per-target provider API key at-rest envelope.
/// <c>config/config.json</c> stores <c>v1:</c> + Base64(<see cref="IKeyProtector.Protect"/>(UTF8(key)));
/// the bind-time <see cref="TargetApiKeyUnwrapPostProcessor"/> restores the plaintext in memory and
/// degrades corrupt/foreign blobs to null (never a crash); the persistence layer wraps on write
/// and removes the node on clear.
/// </summary>
public class TargetApiKeyProtectionTests
{
    // ------------------------------------------------------------------
    //  Bind-time unwrap (TargetApiKeyUnwrapPostProcessor)
    // ------------------------------------------------------------------

    [Fact]
    public void Unwrap_WrappedKey_RestoresPlaintext()
    {
        var protector = new PlainKeyProtector();
        var stored = WrapFor("sk-test-123", protector);
        var options = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "T",
            Url = "http://h:8000",
            ApiKey = stored
        });

        new TargetApiKeyUnwrapPostProcessor(protector).PostConfigure(null, options);

        Assert.Equal("sk-test-123", options.Targets[0].ApiKey);
    }

    [Fact]
    public void Unwrap_LegacyPlaintext_PassesThroughUntouched()
    {
        var options = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "T",
            Url = "http://h:8000",
            ApiKey = "sk-legacy-plain"
        });

        new TargetApiKeyUnwrapPostProcessor(new PlainKeyProtector()).PostConfigure(null, options);

        Assert.Equal("sk-legacy-plain", options.Targets[0].ApiKey);
    }

    [Fact]
    public void Unwrap_BadBase64Envelope_DegradesToNull_NeverThrows()
    {
        var options = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "T",
            Url = "http://h:8000",
            ApiKey = "v1:!!!not-base64!!!"
        });

        new TargetApiKeyUnwrapPostProcessor(new PlainKeyProtector()).PostConfigure(null, options);

        Assert.Null(options.Targets[0].ApiKey);
    }

    [Fact]
    public void Unwrap_ForeignBlob_DegradesToNull_NeverThrows()
    {
        // A protector that cannot open the blob (DPAPI from another account/machine behaves this way).
        var throwing = new ThrowingKeyProtector();
        var options = OptionsWithTargets(new OllamaTargetOptions
        {
            Name = "T",
            Url = "http://h:8000",
            ApiKey = "v1:" + Convert.ToBase64String(new byte[] { 1, 2, 3 })
        });

        new TargetApiKeyUnwrapPostProcessor(throwing).PostConfigure(null, options);

        Assert.Null(options.Targets[0].ApiKey);
    }

    [Fact]
    public void Unwrap_NoProtectorAvailable_WrappedKeyDegradesToNull_LegacyPasses()
    {
        var options = OptionsWithTargets(
            new OllamaTargetOptions { Name = "A", Url = "http://h:8000", ApiKey = "v1:AAAA" },
            new OllamaTargetOptions { Name = "B", Url = "http://h:8001", ApiKey = "sk-plain" },
            new OllamaTargetOptions { Name = "C", Url = "http://h:8002", ApiKey = null });

        new TargetApiKeyUnwrapPostProcessor(keyProtector: null).PostConfigure(null, options);

        Assert.Null(options.Targets[0].ApiKey);
        Assert.Equal("sk-plain", options.Targets[1].ApiKey);
        Assert.Null(options.Targets[2].ApiKey);
    }

    // ------------------------------------------------------------------
    //  Persistence round-trip (RouterConfigPersistence)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PersistTargets_StoresKeyWrappedAndBindUnwrapRestoresIt()
    {
        var tempDir = await CreateTempDirAsync();
        try
        {
            var options = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions());
            var persistence = CreatePersistence(options, tempDir, new PlainKeyProtector());

            var plaintext = "sk-roundtrip-secret-987";
            var result = await persistence.PersistTargetsAsync(new List<OllamaTargetOptions>
            {
                new()
                {
                    Name = "OpenRouter",
                    Url = "https://openrouter.ai/api",
                    Type = TargetType.OpenAI,
                    Provider = "openrouter",
                    ApiKey = plaintext,
                    Models =
                    [
                        new TargetModelConfig { Name = "openai/gpt-4o", ContextLength = 128000 }
                    ]
                }
            });

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(persistence.ConfigFilePath));

            var fileContent = File.ReadAllText(persistence.ConfigFilePath);
            var root = (JsonObject)JsonNode.Parse(fileContent)!;
            var targetNode = (JsonObject)((JsonArray)root["OllamaRouter"]!["Targets"]!)[0]!;

            // The raw plaintext NEVER lands on disk; the wrapped envelope does.
            Assert.DoesNotContain(plaintext, fileContent);
            var stored = targetNode["ApiKey"]!.GetValue<string>();
            Assert.StartsWith(TargetApiKeyUnwrapPostProcessor.WrappedPrefix, stored);
            var storedPayload = stored[TargetApiKeyUnwrapPostProcessor.WrappedPrefix.Length..];
            var unwrappedProtected = Convert.FromBase64String(storedPayload);
            Assert.Equal(plaintext, Encoding.UTF8.GetString(new PlainKeyProtector().Unprotect(unwrappedProtected)));

            // Modeled fields land on disk in the documented shape.
            Assert.Equal("OpenAI", targetNode["Type"]!.GetValue<string>());
            Assert.Equal("openrouter", targetNode["Provider"]!.GetValue<string>());
            var models = (JsonArray)targetNode["Models"]!;
            Assert.Equal("openai/gpt-4o", models[0]!["name"]!.GetValue<string>());
            Assert.Equal(128000, models[0]!["contextLength"]!.GetValue<int>());
            Assert.Null(models[0]!["quantization"]);

            // Bind-time unwrap of the persisted value restores the plaintext in memory.
            var rebindOptions = new OllamaRouterOptions
            {
                Targets = new List<OllamaTargetOptions>
                {
                    new() { Name = "OpenRouter", Url = "https://openrouter.ai/api", ApiKey = stored }
                }
            };
            new TargetApiKeyUnwrapPostProcessor(new PlainKeyProtector()).PostConfigure(null, rebindOptions);
            Assert.Equal(plaintext, rebindOptions.Targets[0].ApiKey);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PersistTargets_NullKeyRemovesTheStoredNode()
    {
        var tempDir = await CreateTempDirAsync();
        try
        {
            var options = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions());
            var persistence = CreatePersistence(options, tempDir, new PlainKeyProtector());

            // First write: a key is stored.
            var first = await persistence.PersistTargetsAsync(new List<OllamaTargetOptions>
            {
                new() { Name = "T", Url = "http://h:8000", Type = TargetType.OpenAI, ApiKey = "sk-one" }
            });
            Assert.True(first.Success, first.Error);
            var root = (JsonObject)JsonNode.Parse(File.ReadAllText(persistence.ConfigFilePath))!;
            Assert.NotNull(((JsonArray)root["OllamaRouter"]!["Targets"]!)[0]!["ApiKey"]);

            // Second write: the key was cleared (Setup API `apiKey: ""`) → the node is REMOVED.
            var second = await persistence.PersistTargetsAsync(new List<OllamaTargetOptions>
            {
                new() { Name = "T", Url = "http://h:8000", Type = TargetType.OpenAI, ApiKey = null }
            });
            Assert.True(second.Success, second.Error);
            var root2 = (JsonObject)JsonNode.Parse(File.ReadAllText(persistence.ConfigFilePath))!;
            Assert.Null(((JsonArray)root2["OllamaRouter"]!["Targets"]!)[0]!["ApiKey"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PersistTargets_LegacyPlaintextKey_IsRewrappedOnWrite()
    {
        var tempDir = await CreateTempDirAsync();
        try
        {
            var options = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions());
            var persistence = CreatePersistence(options, tempDir, new PlainKeyProtector());

            // Simulates a legacy config.json value (no v1: prefix) re-persisted through the API.
            var fileText = """
            { "OllamaRouter": { "Targets": [ { "Name": "T", "Url": "http://h:8000", "ApiKey": "sk-legacy" } ] } }
            """;
            Directory.CreateDirectory(Path.Combine(tempDir, "config"));
            File.WriteAllText(persistence.ConfigFilePath, fileText);

            var result = await persistence.PersistTargetsAsync(new List<OllamaTargetOptions>
            {
                new() { Name = "T", Url = "http://h:8000", Type = TargetType.OpenAI, ApiKey = "sk-legacy" }
            });
            Assert.True(result.Success, result.Error);

            var fileContent = File.ReadAllText(persistence.ConfigFilePath);
            var root = (JsonObject)JsonNode.Parse(fileContent)!;
            var stored = ((JsonArray)root["OllamaRouter"]!["Targets"]!)[0]!["ApiKey"]!.GetValue<string>();
            Assert.StartsWith(TargetApiKeyUnwrapPostProcessor.WrappedPrefix, stored);
            Assert.DoesNotContain("\"sk-legacy\"", fileContent);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PersistTargets_AlreadyWrappedInMemoryKey_IsPersistedAsIs_NeverDoubleWrapped()
    {
        var tempDir = await CreateTempDirAsync();
        try
        {
            var options = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions());
            var persistence = CreatePersistence(options, tempDir, new PlainKeyProtector());

            // Defensive path: an in-memory snapshot that skipped the bind-time unwrap carries
            // the ALREADY-WRAPPED envelope. Persisting it must NOT double-encode (a double
            // wrap would survive the single unwrap pass on the next reload as "v1:…").
            var protector = new PlainKeyProtector();
            var wrapped = $"{TargetApiKeyUnwrapPostProcessor.WrappedPrefix}{Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes("sk-inner")))}";
            var result = await persistence.PersistTargetsAsync(new List<OllamaTargetOptions>
            {
                new() { Name = "T", Url = "http://h:8000", Type = TargetType.OpenAI, ApiKey = wrapped }
            });
            Assert.True(result.Success, result.Error);

            var stored = ((JsonArray)((JsonObject)JsonNode.Parse(File.ReadAllText(persistence.ConfigFilePath))!)!["OllamaRouter"]!["Targets"]!)[0]!["ApiKey"]!.GetValue<string>();
            Assert.Equal(wrapped, stored); // persisted as-is, exactly one envelope layer

            // A SINGLE unwrap pass restores the plaintext.
            var rebind = new OllamaRouterOptions
            {
                Targets = new List<OllamaTargetOptions>
                {
                    new() { Name = "T", Url = "http://h:8000", ApiKey = stored }
                }
            };
            new TargetApiKeyUnwrapPostProcessor(protector).PostConfigure(null, rebind);
            Assert.Equal("sk-inner", rebind.Targets[0].ApiKey);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PersistTargets_OllamaTargetWithoutKey_OmitsKeyAndProviderNodes()
    {
        var tempDir = await CreateTempDirAsync();
        try
        {
            var options = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions());
            var persistence = CreatePersistence(options, tempDir, new PlainKeyProtector());

            var result = await persistence.PersistTargetsAsync(new List<OllamaTargetOptions>
            {
                new() { Name = "Local", Url = "http://127.0.0.1:11434" }
            });
            Assert.True(result.Success, result.Error);

            var root = (JsonObject)JsonNode.Parse(File.ReadAllText(persistence.ConfigFilePath))!;
            var targetsArray = (JsonArray)root["OllamaRouter"]!["Targets"]!;
            var node = (JsonObject)targetsArray[0]!;
            Assert.Equal("Ollama", node["Type"]!.GetValue<string>()); // enum default name
            Assert.Null(node["ApiKey"]);
            Assert.Null(node["Provider"]);
            Assert.Null(node["Models"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static string WrapFor(string plaintext, IKeyProtector protector) =>
        $"{TargetApiKeyUnwrapPostProcessor.WrappedPrefix}{Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(plaintext)))}";

    private static OllamaRouterOptions OptionsWithTargets(params OllamaTargetOptions[] targets) =>
        new() { Targets = targets.ToList() };

    private static async Task<string> CreateTempDirAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "task61-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        await Task.CompletedTask;
        return path;
    }

    private static RouterConfigPersistence CreatePersistence(
        IOptionsMonitor<OllamaRouterOptions> options,
        string contentRoot,
        IKeyProtector keyProtector)
    {
        var services = new ServiceCollection();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(h => h.ContentRootPath).Returns(contentRoot);
        environment.SetupGet(h => h.ApplicationName).Returns("OllamaRouter.Tests");
        environment.SetupGet(h => h.EnvironmentName).Returns("Development");
        services.AddSingleton(environment.Object);
        return new RouterConfigPersistence(options, services.BuildServiceProvider(), logger: null, keyProtector);
    }

    private sealed class ThrowingKeyProtector : IKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) => throw new CryptographicException("external blob");
    }
}
