using OllamaRouter.Security;

namespace OllamaRouter.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="ICredentialStore"/> for the task #110 keyring tests: a
/// (package, service, username) → password dictionary with a settable
/// <see cref="IsAvailable"/> switch and failure injection for the write path. Records every
/// <see cref="SetPassword"/> / <see cref="DeletePassword"/> call (argument triple + password
/// for sets) so the persistence / endpoint tests can assert the exact keyring traffic —
/// NO test ever touches the host OS credential manager with the fake in the graph.
/// </summary>
public sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<(string Package, string Service, string Username), string> _entries = new();

    /// <inheritdoc />
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// When non-null, <see cref="SetPassword"/> throws for the exact matching password (the
    /// call is still recorded first) — simulates a keyring backend failure for ONE specific
    /// write while letting the persistence-layer ROLLBACK write (a different prior value) or
    /// delete succeed, so the rollback path is exercised deterministically.
    /// </summary>
    public string? SetPasswordFailFor { get; set; }

    /// <summary>
    /// When true, <see cref="GetPassword"/> throws (simulating a backend error other than
    /// NotFound) so the fail-soft read path can be exercised deterministically.
    /// </summary>
    public bool ThrowOnGet { get; set; }

    /// <summary>Recorded <see cref="SetPassword"/> calls in order: (package, service, username, password).</summary>
    public List<(string Package, string Service, string Username, string Password)> SetCalls { get; } = [];

    /// <summary>Recorded <see cref="DeletePassword"/> calls in order: (package, service, username).</summary>
    public List<(string Package, string Service, string Username)> DeleteCalls { get; } = [];

    /// <summary>Pre-seeds (or overwrites) an entry without recording a set call.</summary>
    public void Seed(string package, string service, string username, string password) =>
        _entries[(package, service, username)] = password;

    /// <inheritdoc />
    public string? GetPassword(string package, string service, string username)
    {
        if (ThrowOnGet)
        {
            throw new InvalidOperationException("fake keyring backend failure (test injection)");
        }

        return _entries.TryGetValue((package, service, username), out var password) ? password : null;
    }

    /// <inheritdoc />
    public void SetPassword(string package, string service, string username, string password)
    {
        SetCalls.Add((package, service, username, password));
        if (SetPasswordFailFor is not null && password == SetPasswordFailFor)
        {
            throw new InvalidOperationException($"fake keyring backend failure for password (test injection)");
        }

        _entries[(package, service, username)] = password;
    }

    /// <inheritdoc />
    public void DeletePassword(string package, string service, string username)
    {
        DeleteCalls.Add((package, service, username));
        _entries.Remove((package, service, username));
    }
}
