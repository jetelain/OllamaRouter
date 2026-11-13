using OllamaRouter.Security;

namespace OllamaRouter.Tests.Security;

/// <summary>
/// Task #110: deterministic NO-THROW smoke tests of the REAL <see cref="KeySharpCredentialStore"/>
/// wrapper. Only read-only probes are exercised (never a write — the suite must leave the host
/// OS credential manager untouched): <c>IsAvailable</c> always returns a bool (the value is
/// environment-dependent: headless/CI → false, a dev machine with a live keyring → true) and a
/// <c>GetPassword</c> of a nonexistent random entry returns null without side effects (store
/// unavailable → null without native traffic; store available → the native NotFound path is
/// swallowed to null).
/// </summary>
public class KeySharpCredentialStoreTests
{
    [Fact]
    public void IsAvailable_NeverThrows_ReturnsBool()
    {
        var store = new KeySharpCredentialStore();

        var available = store.IsAvailable; // must not throw on any platform / environment

        _ = available; // the value itself is environment-dependent; only the no-throw invariant is asserted
    }

    [Fact]
    public void GetPassword_NonexistentRandomEntry_ReturnsNull_NoThrow()
    {
        var store = new KeySharpCredentialStore();

        // A random (package, service, username) triple: a test must never collide with a real
        // credential, and the call is read-only (no keyring entry is ever created).
        var username = "ollamarouter-test-noexist-" + Guid.NewGuid().ToString("N");

        var password = store.GetPassword(ICredentialStore.PackageId, "ollamarouter-test", username);

        Assert.Null(password);
    }
}
