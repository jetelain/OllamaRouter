using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Options;

/// <summary>
/// Task #110: the bind-time unwrap of <c>keyring:&lt;targetName&gt;</c> markers in
/// <see cref="TargetApiKeyUnwrapPostProcessor"/> — resolution through the
/// <see cref="ICredentialStore"/> with the fixed addressing triple
/// (<c>Pmad.OllamaRouter</c>, <c>openai</c>, username), fail-soft degradation to null (never a
/// startup crash), and regression parity of the legacy <c>v1:</c> / plaintext paths.
/// </summary>
public class TargetApiKeyUnwrapPostProcessorTests
{
    [Fact]
    public void KeyringMarker_ResolvesPlaintextFromStore()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        store.Seed(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "openai", "sk-abc");
        var options = TargetsWithKeys("keyring:openai");

        new TargetApiKeyUnwrapPostProcessor(null, store).PostConfigure(null, options);

        Assert.Equal("sk-abc", options.Targets[0].ApiKey);
        // The EXACT documented addressing triple was requested for the lookup.
        Assert.Equal("sk-abc", store.GetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "openai"));
        Assert.Equal("Pmad.OllamaRouter", ICredentialStore.PackageId);
        Assert.Equal("openai", ICredentialStore.OpenAiService);
    }

    [Fact]
    public void KeyringMarker_StoreUnavailable_DegradesToNull_NeverThrows()
    {
        var store = new FakeCredentialStore { IsAvailable = false };
        store.Seed(ICredentialStore.PackageId, ICredentialStore.OpenAiService, "groq", "sk-hidden");
        var options = TargetsWithKeys("keyring:groq");

        new TargetApiKeyUnwrapPostProcessor(null, store).PostConfigure(null, options);

        Assert.Null(options.Targets[0].ApiKey);
        Assert.Empty(store.DeleteCalls); // no side effects of any kind
    }

    [Fact]
    public void KeyringMarker_CredentialMissing_DegradesToNull_NeverThrows()
    {
        var store = new FakeCredentialStore { IsAvailable = true }; // no entry seeded
        var options = TargetsWithKeys("keyring:groq");

        new TargetApiKeyUnwrapPostProcessor(null, store).PostConfigure(null, options);

        Assert.Null(options.Targets[0].ApiKey);
    }

    [Fact]
    public void KeyringMarker_StoreThrowsOnGet_DegradesToNull_NeverThrows()
    {
        var store = new FakeCredentialStore { IsAvailable = true, ThrowOnGet = true };
        var options = TargetsWithKeys("keyring:openrouter");

        new TargetApiKeyUnwrapPostProcessor(null, store).PostConfigure(null, options);

        Assert.Null(options.Targets[0].ApiKey);
    }

    [Fact]
    public void KeyringMarker_BlankUsername_DegradesToNull()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        var options = TargetsWithKeys("keyring:");

        new TargetApiKeyUnwrapPostProcessor(null, store).PostConfigure(null, options);

        Assert.Null(options.Targets[0].ApiKey);
    }

    [Fact]
    public void NoStoreRegistered_MarkerDegradesToNull()
    {
        var options = TargetsWithKeys("keyring:groq");

        new TargetApiKeyUnwrapPostProcessor(null, credentialStore: null).PostConfigure(null, options);

        Assert.Null(options.Targets[0].ApiKey);
    }

    [Fact]
    public void V1Envelope_StillUnwraps()
    {
        var protector = new PlainKeyProtector();
        var stored = $"{TargetApiKeyUnwrapPostProcessor.WrappedPrefix}{Convert.ToBase64String(protector.Protect(System.Text.Encoding.UTF8.GetBytes("sk-envelope"))) }";
        var options = TargetsWithKeys(stored);

        new TargetApiKeyUnwrapPostProcessor(protector).PostConfigure(null, options);

        Assert.Equal("sk-envelope", options.Targets[0].ApiKey);
    }

    [Fact]
    public void LegacyPlaintext_PassesThrough()
    {
        var store = new FakeCredentialStore { IsAvailable = true };
        var options = TargetsWithKeys("sk-legacy");

        new TargetApiKeyUnwrapPostProcessor(new PlainKeyProtector(), store).PostConfigure(null, options);

        Assert.Equal("sk-legacy", options.Targets[0].ApiKey);
    }

    private static OllamaRouterOptions TargetsWithKeys(params string[] keys) => new()
    {
        Targets = keys.Select((key, i) => new OllamaTargetOptions
        {
            Name = $"T{i}",
            Url = $"http://h:{8000 + i}",
            Type = TargetType.OpenAI,
            ApiKey = key
        }).ToList()
    };
}
