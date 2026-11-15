using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Configuration;
using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Tests.TestHelpers;
using Pmad.LocalApi.Security;

namespace OllamaRouter.Tests.Configuration;

/// <summary>
/// Task #110: the OS-keyring persistence path of <see cref="RouterConfigPersistence"/> —
/// marker + <c>SetPassword</c> when the store is available, <c>v1:</c> fallback when it is
/// not, <c>DeletePassword</c> on clear/removal, the keyring-first write ordering with rollback
/// on failure (a 500 never leaves memory, file AND keyring diverged), and the defensive
/// pass-throughs (non-OpenAI target, already-marker values). All tests run against the
/// in-memory <see cref="FakeCredentialStore"/> — the host OS credential manager is never
/// touched.
/// </summary>
public class RouterConfigPersistenceTests
{
    [Fact]
    public async Task PersistTargets_KeyringAvailable_StoresMarkerAndCallsSetPassword()
    {
        await using var harness = await CreateHarnessAsync(new FakeCredentialStore { IsAvailable = true });

        var result = await harness.Persistence.PersistTargetsAsync(OpenAiTargets("T", "sk-plain-secret"));

        Assert.True(result.Success, result.Error);
        var fileContent = File.ReadAllText(harness.Persistence.ConfigFilePath);
        var targetNode = FirstTargetNode(harness.Persistence);

        // The file holds ONLY the marker — never the plaintext, never a v1: envelope.
        Assert.Equal($"{TargetApiKeyUnwrapPostProcessor.KeyringPrefix}T", targetNode["ApiKey"]!.GetValue<string>());
        Assert.DoesNotContain("sk-plain-secret", fileContent);
        Assert.DoesNotContain(TargetApiKeyUnwrapPostProcessor.WrappedPrefix, fileContent);

        // The exact addressing triple + plaintext was written to the (fake) keyring.
        Assert.Single(harness.Store.SetCalls);
        Assert.Equal(
            (ICredentialStore.PackageId, ICredentialStore.OpenAiService, "T", "sk-plain-secret"),
            harness.Store.SetCalls[0]);
    }

    [Fact]
    public async Task PersistTargets_KeyringUnavailable_FallsBackToV1Envelope()
    {
        var store = new FakeCredentialStore { IsAvailable = false };
        await using var harness = await CreateHarnessAsync(store);

        var result = await harness.Persistence.PersistTargetsAsync(OpenAiTargets("T", "sk-fallback"));

        Assert.True(result.Success, result.Error);
        var stored = FirstTargetNode(harness.Persistence)["ApiKey"]!.GetValue<string>();
        Assert.StartsWith(TargetApiKeyUnwrapPostProcessor.WrappedPrefix, stored);
        Assert.Empty(store.SetCalls); // the store was not even consulted for writes
    }

    [Fact]
    public async Task PersistTargets_KeyCleared_RemovesNodeAndDeletesCredential()
    {
        await using var harness = await CreateHarnessAsync(new FakeCredentialStore { IsAvailable = true });

        var first = await harness.Persistence.PersistTargetsAsync(OpenAiTargets("T", "sk-one"));
        Assert.True(first.Success, first.Error);
        Assert.Single(harness.Store.SetCalls);

        // Second persist with the key cleared (Setup API `apiKey: ""`) → node removed +
        // the credential deleted.
        var second = await harness.Persistence.PersistTargetsAsync(OpenAiTargets("T", null));
        Assert.True(second.Success, second.Error);
        Assert.Null(FirstTargetNode(harness.Persistence)["ApiKey"]);
        Assert.Single(harness.Store.DeleteCalls);
        Assert.Equal(
            (ICredentialStore.PackageId, ICredentialStore.OpenAiService, "T"),
            harness.Store.DeleteCalls[0]);
    }

    [Fact]
    public async Task PersistTargets_TargetRemoved_DeletesCredential()
    {
        await using var harness = await CreateHarnessAsync(new FakeCredentialStore { IsAvailable = true });

        var first = await harness.Persistence.PersistTargetsAsync(
            OpenAiTargets("A", "sk-a").Concat(OpenAiTargets("B", "sk-b")).ToList());
        Assert.True(first.Success, first.Error);
        Assert.Equal(2, harness.Store.SetCalls.Count);

        // Drop B from the list (DELETE /api/router/targets/B rides this path).
        var second = await harness.Persistence.PersistTargetsAsync(OpenAiTargets("A", "sk-a"));
        Assert.True(second.Success, second.Error);
        Assert.Contains(harness.Store.DeleteCalls,
            c => c is (ICredentialStore.PackageId, ICredentialStore.OpenAiService, "B"));
        Assert.DoesNotContain(harness.Store.DeleteCalls,
            c => c is (ICredentialStore.PackageId, ICredentialStore.OpenAiService, "A"));
    }

    [Fact]
    public async Task PersistTargets_SetPasswordThrows_FailsRollsBackKeyringAndLeavesFileUntouched()
    {
        var store = new FakeCredentialStore
        {
            IsAvailable = true,
            SetPasswordFailFor = "sk-b-new"
        };
        // Two targets with EXISTING keyring values: the first set succeeds, the second throws
        // → the whole pass must fail, roll back the executed set (restore the prior value),
        // and leave file + memory untouched.
        store.Seed(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "A", "sk-a-old");
        store.Seed(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "B", "sk-b-old");
        await using var harness = await CreateHarnessAsync(store);
        var inMemoryTargets = new List<OllamaTargetOptions>(harness.Options.CurrentValue.Targets);

        var result = await harness.Persistence.PersistTargetsAsync(
            OpenAiTargets("A", "sk-a-new").Concat(OpenAiTargets("B", "sk-b-new")).ToList());

        Assert.False(result.Success);
        // The file was NOT written (no marker for a secret that never landed in the keyring).
        Assert.False(File.Exists(harness.Persistence.ConfigFilePath));
        // The in-memory options were NOT touched.
        Assert.Equal(inMemoryTargets, harness.Options.CurrentValue.Targets, new OllamaTargetOptionsComparer());
        // The rollback restored the prior value of the EXECUTED set (A); B's failed write left
        // its prior value in place.
        Assert.Contains(("A", "sk-a-new"), harness.Store.SetCalls.Select(c => (c.Username, c.Password)));
        Assert.Contains(("B", "sk-b-new"), harness.Store.SetCalls.Select(c => (c.Username, c.Password)));
        Assert.Contains(("A", "sk-a-old"), harness.Store.SetCalls.Select(c => (c.Username, c.Password)));
        Assert.Equal("sk-a-old", store.GetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "A"));
        Assert.Equal("sk-b-old", store.GetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "B"));
        Assert.Empty(store.DeleteCalls);
    }

    [Fact]
    public async Task PersistTargets_SetPasswordThrows_NoPriorValue_DeletesExecutedSetOnRollback()
    {
        var store = new FakeCredentialStore
        {
            IsAvailable = true,
            SetPasswordFailFor = "sk-b-new"
        };
        // No pre-seeded entries: the rollback of the executed set A (no prior value) must
        // DELETE it (not write it), restoring the prior "absent" state.
        await using var harness = await CreateHarnessAsync(store);

        var result = await harness.Persistence.PersistTargetsAsync(
            OpenAiTargets("A", "sk-a-new").Concat(OpenAiTargets("B", "sk-b-new")).ToList());

        Assert.False(result.Success);
        Assert.False(File.Exists(harness.Persistence.ConfigFilePath));
        Assert.Contains(harness.Store.DeleteCalls,
            c => c is (ICredentialStore.PackageId, ICredentialStore.OpenAiService, "A"));
        Assert.Null(store.GetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "A"));
        Assert.Null(store.GetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "B"));
    }

    [Fact]
    public async Task PersistTargets_NonOpenAiTargetWithKey_UsesV1FallbackEvenWhenKeyringAvailable()
    {
        await using var harness = await CreateHarnessAsync(new FakeCredentialStore { IsAvailable = true });

        var result = await harness.Persistence.PersistTargetsAsync(
            new List<OllamaTargetOptions>
            {
                new() { Name = "Local", Url = "http://h:8000", Type = TargetType.Ollama, ApiKey = "sk-odd" }
            });

        // Defensive gate (D2): only OpenAI targets ride the keyring path.
        Assert.True(result.Success, result.Error);
        var stored = FirstTargetNode(harness.Persistence)["ApiKey"]!.GetValue<string>();
        Assert.StartsWith(TargetApiKeyUnwrapPostProcessor.WrappedPrefix, stored);
        Assert.Empty(harness.Store.SetCalls);
    }

    [Fact]
    public async Task PersistTargets_InMemoryValueAlreadyMarker_PersistedAsIs_NoSetPassword()
    {
        await using var harness = await CreateHarnessAsync(new FakeCredentialStore { IsAvailable = true });

        // Defensive path: an in-memory snapshot already carries the marker (e.g. copied from
        // the disk state without a bind-time unwrap) — it is persisted AS-IS, never re-stored
        // into the keyring, never double-processed.
        var marker = $"{TargetApiKeyUnwrapPostProcessor.KeyringPrefix}T";
        var result = await harness.Persistence.PersistTargetsAsync(OpenAiTargets("T", marker));

        Assert.True(result.Success, result.Error);
        Assert.Equal(marker, FirstTargetNode(harness.Persistence)["ApiKey"]!.GetValue<string>());
        Assert.Empty(harness.Store.SetCalls);
    }

    [Fact]
    public async Task PersistTargets_KeyringRoundTrip_MarkerUnwrapsViaPostProcessor()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        await using var harness = await CreateHarnessAsync(store);

        var plaintext = "sk-roundtrip-42";
        var result = await harness.Persistence.PersistTargetsAsync(OpenAiTargets("T", plaintext));
        Assert.True(result.Success, result.Error);

        var stored = FirstTargetNode(harness.Persistence)["ApiKey"]!.GetValue<string>();
        Assert.Equal($"{TargetApiKeyUnwrapPostProcessor.KeyringPrefix}T", stored);

        // End-to-end unit round trip: re-bind the persisted marker through the post-processor
        // with the SAME store → the plaintext is restored in memory.
        var rebind = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "T", Url = "http://h:8000", Type = TargetType.OpenAI, ApiKey = stored }
            ]
        };
        new TargetApiKeyUnwrapPostProcessor(new PlainKeyProtector(), store).PostConfigure(null, rebind);
        Assert.Equal(plaintext, rebind.Targets[0].ApiKey);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static List<OllamaTargetOptions> OpenAiTargets(string name, string? apiKey) =>
    [
        new()
        {
            Name = name,
            Url = $"http://{name}:8000",
            Type = TargetType.OpenAI,
            Provider = "test",
            ApiKey = apiKey
        }
    ];

    private static JsonObject FirstTargetNode(IRouterConfigPersistence persistence)
    {
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(persistence.ConfigFilePath))!;
        return (JsonObject)((JsonArray)root["OllamaRouter"]!["Targets"]!)[0]!;
    }

    private static async Task<Harness> CreateHarnessAsync(FakeCredentialStore store)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "task110-keyring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        var options = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions());
        var services = new ServiceCollection();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(h => h.ContentRootPath).Returns(contentRoot);
        environment.SetupGet(h => h.ApplicationName).Returns("OllamaRouter.Tests");
        environment.SetupGet(h => h.EnvironmentName).Returns("Development");
        services.AddSingleton(environment.Object);
        var persistence = new RouterConfigPersistence(
            options,
            services.BuildServiceProvider(),
            logger: null,
            keyProtector: new PlainKeyProtector(),
            credentialStore: store);
        await Task.CompletedTask;
        return new Harness(persistence, options, store, contentRoot);
    }

    private sealed class OllamaTargetOptionsComparer : IEqualityComparer<OllamaTargetOptions>
    {
        public bool Equals(OllamaTargetOptions? x, OllamaTargetOptions? y) =>
            x is not null && y is not null &&
            x.Name == y.Name && x.Url == y.Url && x.Type == y.Type &&
            x.ApiKey == y.ApiKey && x.Provider == y.Provider && x.Enabled == y.Enabled;

        public int GetHashCode(OllamaTargetOptions obj) => obj.Name?.GetHashCode() ?? 0;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(IRouterConfigPersistence persistence, TestOptionsMonitor<OllamaRouterOptions> options, FakeCredentialStore store, string contentRoot)
        {
            Persistence = persistence;
            Options = options;
            Store = store;
            ContentRoot = contentRoot;
        }

        public IRouterConfigPersistence Persistence { get; }

        public TestOptionsMonitor<OllamaRouterOptions> Options { get; }

        public FakeCredentialStore Store { get; }

        public string ContentRoot { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(ContentRoot))
                {
                    Directory.Delete(ContentRoot, recursive: true);
                }
            }
            catch
            {
                // Temp cleanup must never mask the test outcome.
            }
            await Task.CompletedTask;
        }
    }
}
