using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Options;
using OllamaRouter.Tests.TestHelpers;
using Pmad.LocalApi.Security;

namespace OllamaRouter.Tests.Security;

/// <summary>
/// Unit tests for the API key store (task #50): key generation format, load-or-create (missing
/// → regenerate; corrupt → regenerate, never crash), protected wrapping (fake protector so real
/// DPAPI is never touched), hash-only user-key persistence (raw keys never on disk), expiry
/// logic (none / past / future), rotation logic (fresh → no-op; stale → replaced; live option
/// rebind) and 0600 Unix permissions. Each test gets a fresh temp content root.
/// </summary>
public class ApiKeyStoreTests : IAsyncLifetime
{
    /// <summary>
    /// Recording stand-in for the platform protector: prefixes the wrapped bytes with "ENC" so
    /// tests can assert the file is NOT plaintext and can seed/validate wrapped envelopes. Real
    /// DPAPI is never touched on any OS.
    /// </summary>
    private sealed class FakeKeyProtector : IKeyProtector
    {
        private static readonly byte[] Prefix = [0x45, 0x4E, 0x43]; // "ENC"

        public int ProtectCalls { get; private set; }

        public int UnprotectCalls { get; private set; }

        public byte[] Protect(byte[] plaintext)
        {
            ProtectCalls++;
            return [.. Prefix, .. plaintext];
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            UnprotectCalls++;
            if (protectedData.Length < 3 ||
                protectedData[0] != Prefix[0] ||
                protectedData[1] != Prefix[1] ||
                protectedData[2] != Prefix[2])
            {
                throw new CryptographicException("not a protected blob");
            }

            return [.. protectedData[3..]];
        }
    }

    private string _contentRoot = null!;

    public async Task InitializeAsync()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), $"ollamarouter-keystore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_contentRoot);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_contentRoot))
            {
                Directory.Delete(_contentRoot, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup must never mask the test outcome.
        }

        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static ApiKeyStore CreateStore(string contentRoot, IKeyProtector protector, ILogger<ApiKeyStore>? logger = null)
        => new(Path.Combine(contentRoot, "config"), protector, logger);

    /// <summary>
    /// Capturing <see cref="ILogger{TCategoryName}"/> (pattern: DynamicContextSizingTests.TestLogger):
    /// records the rendered log messages so tests can assert the prominent invalidation warning.
    /// </summary>
    private sealed class CapturingLogger : ILogger<ApiKeyStore>
    {
        private readonly List<string> _messages = [];

        public List<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }

        public bool AnyWarningContains(params string[] fragments)
        {
            lock (_messages)
            {
                return _messages.Any(m => fragments.All(f => m.Contains(f, StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    private static string DefaultKeyPath(string contentRoot) => Path.Combine(contentRoot, "config", "default-key.dat");

    private static string UserKeysPath(string contentRoot) => Path.Combine(contentRoot, "config", "api-keys.json");

    /// <summary>Seeds a (protected) default key envelope with an explicit generatedAt.</summary>
    private static void SeedDefaultKey(string contentRoot, IKeyProtector protector, string key, DateTimeOffset generatedAt)
    {
        var path = DefaultKeyPath(contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var envelope = new JsonObject
        {
            ["key"] = key,
            ["generatedAt"] = generatedAt.ToString("O")
        };
        File.WriteAllBytes(path, protector.Protect(System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString())));
    }

    /// <summary>Seeds the (format v2) user key salt+hash store with the given (name, rawKey, expiresAt) entries — each entry gets a FRESH random 16-byte salt.</summary>
    private static void SeedUserKeys(
        string contentRoot,
        params (string Name, string RawKey, DateTimeOffset? ExpiresAt)[] entries)
    {
        var path = UserKeysPath(contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var keys = new JsonArray();
        foreach (var (name, rawKey, expiresAt) in entries)
        {
            var salt = ApiKeyCrypto.GenerateSalt();
            keys.Add(new JsonObject
            {
                ["name"] = name,
                ["salt"] = Convert.ToBase64String(salt),
                ["hash"] = Convert.ToBase64String(ApiKeyCrypto.HashKey(rawKey, salt)),
                ["createdAt"] = DateTimeOffset.UtcNow.AddHours(-2).ToString("O"),
                ["expiresAt"] = expiresAt is { } e ? e.ToString("O") : null
            });
        }

        File.WriteAllText(path, new JsonObject { ["version"] = 2, ["keys"] = keys }.ToJsonString());
    }

    /// <summary>Seeds a LEGACY format-v1 file (unsalted hash, no per-entry salt) to exercise the v2 upgrade path.</summary>
    private static void SeedLegacyV1UserKeys(string contentRoot, (string Name, string RawKey)[] entries)
    {
        var path = UserKeysPath(contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var keys = new JsonArray();
        foreach (var (name, rawKey) in entries)
        {
            keys.Add(new JsonObject
            {
                ["name"] = name,
                ["hash"] = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey))),
                ["createdAt"] = DateTimeOffset.UtcNow.AddHours(-2).ToString("O"),
                ["expiresAt"] = null
            });
        }

        File.WriteAllText(path, new JsonObject { ["version"] = 1, ["keys"] = keys }.ToJsonString());
    }

    // ------------------------------------------------------------------
    //  Default key: load-or-create
    // ------------------------------------------------------------------

    [Fact]
    public void FirstStart_CreatesDefaultKeyFile_RoundTripsKeyAndGeneratedAt()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var key = store.GetDefaultKey();

        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(key));
        Assert.True(File.Exists(DefaultKeyPath(_contentRoot)), "default-key.dat must exist after first use");

        // Plain protector: the file is the plain envelope (JSON) and round-trips.
        var envelope = (JsonObject)JsonNode.Parse(File.ReadAllText(DefaultKeyPath(_contentRoot)))!;
        Assert.Equal(key, envelope["key"]!.GetValue<string>());
        var generatedAt = DateTimeOffset.Parse(envelope["generatedAt"]!.GetValue<string>()!);
        Assert.True(
            DateTimeOffset.UtcNow - generatedAt < TimeSpan.FromMinutes(1),
            "generatedAt must be fresh on first start");
        Assert.Equal(generatedAt, store.GetDefaultKeyGeneratedAt());
        Assert.True(store.ValidateKey(key));
    }

    [Fact]
    public void FirstStart_WithEncryptingProtector_FileBytesAreNotPlaintext()
    {
        var protector = new FakeKeyProtector();
        var store = CreateStore(_contentRoot, protector);
        var key = store.GetDefaultKey();

        var raw = File.ReadAllBytes(DefaultKeyPath(_contentRoot));
        Assert.True(raw.Length > 3 && raw[0] == 0x45 && raw[1] == 0x4E && raw[2] == 0x43, "file must carry the protector prefix");
        var fileText = System.Text.Encoding.UTF8.GetString(raw);
        // The file must NOT be a readable envelope (it went through the protector on write);
        // only the protector itself can read it back.
        bool parsedAsJson = true;
        try
        {
            JsonDocument.Parse(fileText);
        }
        catch (System.Text.Json.JsonException)
        {
            parsedAsJson = false;
        }

        Assert.False(parsedAsJson, "the protected file must not parse as a plain JSON envelope");
        Assert.True(protector.ProtectCalls >= 1, "the file must have been wrapped through the protector on write");
        Assert.True(store.ValidateKey(key));
    }

    [Fact]
    public void CorruptDefaultKeyFile_RegeneratesWithoutThrowing()
    {
        var protector = new FakeKeyProtector();
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultKeyPath(_contentRoot))!);
        // Unwrappable garbage → regenerate.
        File.WriteAllBytes(DefaultKeyPath(_contentRoot), [0x00, 0x01, 0x02, 0x03]);

        var store = CreateStore(_contentRoot, protector);
        var key = store.GetDefaultKey();
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(key));
        Assert.True(store.ValidateKey(key));
        var raw = File.ReadAllBytes(DefaultKeyPath(_contentRoot));
        Assert.True(raw[0] == 0x45, "the regenerated file must be re-wrapped");
    }

    [Fact]
    public void WrappedButMalformedEnvelope_RegeneratesWithoutThrowing()
    {
        var protector = new FakeKeyProtector();
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultKeyPath(_contentRoot))!);
        var badEnvelope = new JsonObject { ["key"] = "too-short", ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O") };
        File.WriteAllBytes(
            DefaultKeyPath(_contentRoot),
            protector.Protect(System.Text.Encoding.UTF8.GetBytes(badEnvelope.ToJsonString())));

        var store = CreateStore(_contentRoot, protector);
        var key = store.GetDefaultKey();
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(key));
        Assert.True(store.ValidateKey(key));
    }

    [Fact]
    public void SeededDefaultKey_IsLoadedAndValidates()
    {
        var protector = new FakeKeyProtector();
        var known = ApiKeyCrypto.GenerateKey();
        SeedDefaultKey(_contentRoot, protector, known, DateTimeOffset.UtcNow.AddMinutes(-5));

        var store = CreateStore(_contentRoot, protector);
        Assert.Equal(known, store.GetDefaultKey());
        Assert.True(protector.UnprotectCalls >= 1, "the seeded file must be unwrapped through the protector on load");
        Assert.True(store.ValidateKey(known));
        Assert.False(store.ValidateKey(ApiKeyCrypto.GenerateKey()));
        Assert.True(
            DateTimeOffset.UtcNow - store.GetDefaultKeyGeneratedAt() > TimeSpan.FromMinutes(4),
            "generatedAt must be the seeded (older) value");
    }

    // ------------------------------------------------------------------
    //  User keys: hash-only persistence, CRUD, expiry
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateUserKey_PersistsHashOnly_NeverTheRawKey()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        _ = store.GetDefaultKey(); // force init

        var result = await store.CreateUserKeyAsync("worker", 168);
        Assert.True(result.Success);
        Assert.NotNull(result.Created);
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(result.Created!.Key));
        Assert.Equal("worker", result.Created.Name);
        Assert.True(
            result.Created.ExpiresAt is { } exp && exp - DateTimeOffset.UtcNow > TimeSpan.FromHours(160),
            "expiresAt must be ~168h in the future");

        Assert.True(File.Exists(UserKeysPath(_contentRoot)));
        var fileText = File.ReadAllText(UserKeysPath(_contentRoot));
        Assert.DoesNotContain(result.Created.Key, fileText, StringComparison.Ordinal);
        Assert.Contains("worker", fileText, StringComparison.Ordinal);

        var root = (JsonObject)JsonNode.Parse(fileText)!;
        var entry = ((JsonArray)root["keys"]!)![0]! as JsonObject;
        Assert.NotNull(entry);
        Assert.Equal("worker", entry!["name"]!.GetValue<string>());
        // v2: the salt is persisted (16 bytes, standard base64) and the hash is the SALTED
        // SHA-256 — verified independently from the file content.
        var storedSalt = Convert.FromBase64String(entry!["salt"]!.GetValue<string>()!);
        Assert.Equal(ApiKeyCrypto.SaltByteLength, storedSalt.Length);
        var storedHash = Convert.FromBase64String(entry!["hash"]!.GetValue<string>()!);
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData([.. storedSalt, .. System.Text.Encoding.UTF8.GetBytes(result.Created.Key)]),
            storedHash);
        Assert.Equal(5, entry.Count);

        Assert.True(store.ValidateKey(result.Created.Key), "a fresh user key must validate");
    }

    [Fact]
    public async Task CreateUserKey_TwoKeys_NeverShareASalt()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var first = await store.CreateUserKeyAsync("a", null);
        var second = await store.CreateUserKeyAsync("b", null);
        Assert.True(first.Success && second.Success);

        var fileText = File.ReadAllText(UserKeysPath(_contentRoot));
        var root = (JsonObject)JsonNode.Parse(fileText)!;
        var keys = (JsonArray)root["keys"]!;
        var saltA = Convert.FromBase64String(keys![0]!["salt"]!.GetValue<string>()!);
        var saltB = Convert.FromBase64String(keys[1]["salt"]!.GetValue<string>()!);
        Assert.Equal(ApiKeyCrypto.SaltByteLength, saltA.Length);
        Assert.Equal(ApiKeyCrypto.SaltByteLength, saltB.Length);
        Assert.False(
            System.Linq.Enumerable.SequenceEqual(saltA, saltB),
            "two keys must never share a salt (unique per-key random salt)");

        // Even the SAME raw value hashed with the two salts yields different stored hashes.
        var rawKey = first.Created!.Key;
        Assert.NotEqual(ApiKeyCrypto.HashKey(rawKey, saltA), ApiKeyCrypto.HashKey(rawKey, saltB));
    }

    [Fact]
    public async Task V1File_EmptyStore_ProminentInvalidationWarning_V2RewrittenOnNextMutation()
    {
        var legacyKey = ApiKeyCrypto.GenerateKey();
        SeedLegacyV1UserKeys(_contentRoot, [("legacy", legacyKey)]);
        var logger = new CapturingLogger();
        var store = CreateStore(_contentRoot, new PlainKeyProtector(), logger);

        Assert.Empty(store.GetUserKeys());
        Assert.False(store.ValidateKey(legacyKey), "legacy unsalted-hash keys must no longer validate");
        Assert.True(
            logger.AnyWarningContains("loaded EMPTY", "was rejected"),
            "the prominent invalidation warning must be logged (fail-open, store rejected)");

        // Next mutation re-writes the file as v2 with ONLY the new entry.
        var result = await store.CreateUserKeyAsync("fresh", null);
        Assert.True(result.Success);
        var fileText = File.ReadAllText(UserKeysPath(_contentRoot));
        var root = (JsonObject)JsonNode.Parse(fileText)!;
        Assert.Equal(2, root["version"]!.GetValue<int>());
        var keys = (JsonArray)root["keys"]!;
        var entry = Assert.Single(keys)! as JsonObject;
        Assert.Equal("fresh", entry!["name"]!.GetValue<string>());
        Assert.NotNull(entry["salt"]);
        Assert.DoesNotContain("legacy", fileText, StringComparison.Ordinal);
    }

    [Fact]
    public void V2File_MissingSalt_EmptyStore_WithWarning()
    {
        // Rule D1: a v2 file with ANY malformed entry (here: missing "salt") invalidates the
        // WHOLE store — no per-entry skip.
        var path = UserKeysPath(_contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var keys = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "ok",
                ["hash"] = Convert.ToBase64String(ApiKeyCrypto.HashKey(ApiKeyCrypto.GenerateKey(), ApiKeyCrypto.GenerateSalt())),
                ["createdAt"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
                ["expiresAt"] = null
            },
            new JsonObject
            {
                ["name"] = "no-salt",
                ["hash"] = Convert.ToBase64String(ApiKeyCrypto.HashKey(ApiKeyCrypto.GenerateKey(), ApiKeyCrypto.GenerateSalt())),
                ["createdAt"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
                ["expiresAt"] = null
            }
        };
        File.WriteAllText(path, new JsonObject { ["version"] = 2, ["keys"] = keys }.ToJsonString());

        var logger = new CapturingLogger();
        var store = CreateStore(_contentRoot, new PlainKeyProtector(), logger);
        Assert.Empty(store.GetUserKeys());
        Assert.True(logger.AnyWarningContains("loaded EMPTY", "was rejected"), "the invalidation warning must be logged (rule D1: missing salt)");
    }

    [Fact]
    public void V2File_WrongLengthSalt_EmptyStore_WithWarning()
    {
        // An 8-byte salt (≠ 16) is malformed → whole store empty + warning, no exception.
        var path = UserKeysPath(_contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var keys = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "short-salt",
                ["salt"] = Convert.ToBase64String(new byte[8]),
                ["hash"] = Convert.ToBase64String(ApiKeyCrypto.HashKey(ApiKeyCrypto.GenerateKey(), new byte[8])),
                ["createdAt"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
                ["expiresAt"] = null
            }
        };
        File.WriteAllText(path, new JsonObject { ["version"] = 2, ["keys"] = keys }.ToJsonString());

        var logger = new CapturingLogger();
        var store = CreateStore(_contentRoot, new PlainKeyProtector(), logger);
        Assert.Empty(store.GetUserKeys());
        Assert.True(logger.AnyWarningContains("loaded EMPTY", "was rejected"), "the invalidation warning must be logged (rule D1: 8-byte salt)");
    }

    [Fact]
    public void V2File_NonBase64Salt_EmptyStore_WithWarning()
    {
        var path = UserKeysPath(_contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var keys = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "bad-salt",
                ["salt"] = "not!valid;base64!!!",
                ["hash"] = Convert.ToBase64String(ApiKeyCrypto.HashKey(ApiKeyCrypto.GenerateKey(), ApiKeyCrypto.GenerateSalt())),
                ["createdAt"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
                ["expiresAt"] = null
            }
        };
        File.WriteAllText(path, new JsonObject { ["version"] = 2, ["keys"] = keys }.ToJsonString());

        var logger = new CapturingLogger();
        var store = CreateStore(_contentRoot, new PlainKeyProtector(), logger);
        Assert.Empty(store.GetUserKeys());
        Assert.True(logger.AnyWarningContains("loaded EMPTY", "was rejected"), "the invalidation warning must be logged (rule D1: non-base64 salt)");
    }

    [Fact]
    public async Task CreateUserKey_NoExpiry_ExpiresAtIsNull_AndKeyNeverExpires()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var result = await store.CreateUserKeyAsync("forever", null);
        Assert.True(result.Success);
        Assert.Null(result.Created!.ExpiresAt);
        var fileText = File.ReadAllText(UserKeysPath(_contentRoot));
        Assert.Contains("\"expiresAt\": null", fileText, StringComparison.Ordinal);
        Assert.True(store.ValidateKey(result.Created.Key));
    }

    [Fact]
    public async Task CreateUserKey_DuplicateName_ReturnsDuplicate()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var first = await store.CreateUserKeyAsync("dup", null);
        Assert.True(first.Success);

        var second = await store.CreateUserKeyAsync("DUP", null); // case-insensitive
        Assert.False(second.Success);
        Assert.Equal(ApiKeyCreateFailure.Duplicate, second.Failure);
        Assert.Single(store.GetUserKeys());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has\\slash")]
    [InlineData("has:colon")]
    [InlineData("tab\tname")]
    public async Task CreateUserKey_InvalidName_ReturnsInvalidInput(string? name)
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var result = await store.CreateUserKeyAsync(name!, null);
        Assert.False(result.Success);
        Assert.Equal(ApiKeyCreateFailure.InvalidInput, result.Failure);
    }

    [Fact]
    public async Task CreateUserKey_NameTooLong_ReturnsInvalidInput()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var result = await store.CreateUserKeyAsync(new string('a', 65), null);
        Assert.False(result.Success);
        Assert.Equal(ApiKeyCreateFailure.InvalidInput, result.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task CreateUserKey_ValidForBelowOne_ReturnsInvalidInput(int validFor)
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var result = await store.CreateUserKeyAsync("hours", validFor);
        Assert.False(result.Success);
        Assert.Equal(ApiKeyCreateFailure.InvalidInput, result.Failure);
    }

    [Fact]
    public async Task DeleteUserKey_RemovesIt_AndUnknownNameReturnsFalse()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var created = (await store.CreateUserKeyAsync("gone", null)).Created!;

        Assert.True(await store.DeleteUserKeyAsync("GONE"));
        Assert.False(store.ValidateKey(created.Key), "the deleted key must stop validating");
        Assert.Empty(store.GetUserKeys());

        Assert.False(await store.DeleteUserKeyAsync("never-existed"));
        Assert.False(await store.DeleteUserKeyAsync(""));
    }

    [Fact]
    public void GetUserKeys_IsMasked_NeverExposesHashOrRaw()
    {
        var rawKey = ApiKeyCrypto.GenerateKey();
        SeedUserKeys(_contentRoot, ("masked", rawKey, null));
        var store = CreateStore(_contentRoot, new PlainKeyProtector());

        var list = store.GetUserKeys();
        var info = Assert.Single(list);
        Assert.Equal("masked", info.Name);
        Assert.Null(info.ExpiresAt);
        Assert.NotEqual(DateTimeOffset.MinValue, info.CreatedAt);
    }

    [Fact]
    public void ValidateKey_UserKeyExpiry_PastFutureNone()
    {
        var past = ApiKeyCrypto.GenerateKey();
        var future = ApiKeyCrypto.GenerateKey();
        var none = ApiKeyCrypto.GenerateKey();
        SeedUserKeys(
            _contentRoot,
            ("past", past, DateTimeOffset.UtcNow.AddMinutes(-1)),
            ("future", future, DateTimeOffset.UtcNow.AddHours(2)),
            ("none", none, null));

        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        Assert.False(store.ValidateKey(past), "an expired user key must be rejected");
        Assert.True(store.ValidateKey(future), "a non-expired user key must validate");
        Assert.True(store.ValidateKey(none), "a key without expiry must validate");
    }

    [Fact]
    public void CorruptUserKeysFile_TreatedAsEmptyStore_WithoutCrash()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserKeysPath(_contentRoot))!);
        File.WriteAllText(UserKeysPath(_contentRoot), "{{{ not json");
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        Assert.Empty(store.GetUserKeys());
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(store.GetDefaultKey()));
    }

    [Fact]
    public void UserKeysFile_IsCreatedOnlyOnFirstMutation()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        _ = store.GetDefaultKey();
        _ = store.GetUserKeys();
        Assert.False(File.Exists(UserKeysPath(_contentRoot)), "no user keys yet: the file must not exist");
    }

    // ------------------------------------------------------------------
    //  Rotation
    // ------------------------------------------------------------------

    [Fact]
    public async Task RotateIfStale_FreshKey_NoRotation()
    {
        var protector = new FakeKeyProtector();
        var known = ApiKeyCrypto.GenerateKey();
        SeedDefaultKey(_contentRoot, protector, known, DateTimeOffset.UtcNow.AddMinutes(-5));

        var store = CreateStore(_contentRoot, protector);
        Assert.False(await store.RotateDefaultKeyIfStaleAsync(TimeSpan.FromHours(24)), "a fresh key (24h interval) must not rotate");
        Assert.Equal(known, store.GetDefaultKey());
        Assert.True(store.ValidateKey(known));
    }

    [Fact]
    public async Task RotateIfStale_StaleKey_Rotates_OldKeyDeadImmediately_NoGracePeriod()
    {
        var protector = new FakeKeyProtector();
        var oldKey = ApiKeyCrypto.GenerateKey();
        SeedDefaultKey(_contentRoot, protector, oldKey, DateTimeOffset.UtcNow.AddHours(-25)); // 25h > 24h default

        var store = CreateStore(_contentRoot, protector);
        Assert.False(store.ValidateKey("nonsense"), "sanity");
        Assert.True(await store.RotateDefaultKeyIfStaleAsync(TimeSpan.FromHours(24)), "a 25h-old key must rotate at the 24h default");

        var newKey = store.GetDefaultKey();
        Assert.NotEqual(oldKey, newKey);
        Assert.False(store.ValidateKey(oldKey), "the old key must be dead IMMEDIATELY (no grace period)");
        Assert.True(store.ValidateKey(newKey), "the new key must work");
        Assert.True(
            DateTimeOffset.UtcNow - store.GetDefaultKeyGeneratedAt() < TimeSpan.FromMinutes(1),
            "generatedAt must advance to the rotation time");
    }

    [Fact]
    public async Task RotateIfStale_UsesLiveOptionValue_WithMonitorRebind()
    {
        var protector = new FakeKeyProtector();
        var known = ApiKeyCrypto.GenerateKey();
        SeedDefaultKey(_contentRoot, protector, known, DateTimeOffset.UtcNow.AddHours(-2));

        // Task #115: the store no longer holds the options monitor — the caller computes the
        // live interval from the monitor on every check, exactly like
        // DefaultKeyRotationHostedService.TickAsync does.
        var monitor = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions()); // 24h default
        var store = CreateStore(_contentRoot, protector);
        Assert.False(
            await store.RotateDefaultKeyIfStaleAsync(TimeSpan.FromHours(monitor.CurrentValue.ApiSecurity.DefaultKeyRotationHours)),
            "2h old at a 24h interval: no rotation");
        Assert.Equal(known, store.GetDefaultKey());

        // LIVE rebind: the monitor now reports a 1h interval → the 2h-old key is stale.
        monitor.SetValue(new OllamaRouterOptions { ApiSecurity = new ApiSecurityOptions { DefaultKeyRotationHours = 1 } });
        Assert.True(
            await store.RotateDefaultKeyIfStaleAsync(TimeSpan.FromHours(monitor.CurrentValue.ApiSecurity.DefaultKeyRotationHours)),
            "after the live rebind to 1h, the stale key must rotate");
        Assert.NotEqual(known, store.GetDefaultKey());
    }

    [Fact]
    public void RotationHostedService_Constructs_WithStore_LiveMonitor_AndLogger()
    {
        // Task #115: the hosted service now takes the live IOptionsMonitor itself (the store no
        // longer holds it); the 5-minute tick loop itself stays untested, as before.
        var monitor = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions());
        var service = new OllamaRouter.Security.DefaultKeyRotationHostedService(
            CreateStore(_contentRoot, new PlainKeyProtector()),
            monitor,
            NullLogger<OllamaRouter.Security.DefaultKeyRotationHostedService>.Instance);
        Assert.NotNull(service);
        service.Dispose();
    }

    [Fact]
    public async Task RotateDefaultKeyAsync_AlwaysRotates_ManualSemantics()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var first = store.GetDefaultKey();
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(first));

        var second = await store.RotateDefaultKeyAsync();
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(second));
        Assert.NotEqual(first, second);
        Assert.False(store.ValidateKey(first), "manual rotation kills the previous key immediately");
        Assert.True(store.ValidateKey(second));
        Assert.Equal(second, store.GetDefaultKey());
    }

    [Fact]
    public async Task RotateDefaultKeyAsync_InvalidatesNothingElse_UserKeysSurvive()
    {
        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        var user = (await store.CreateUserKeyAsync("survivor", null)).Created!;
        await store.RotateDefaultKeyAsync();
        Assert.True(store.ValidateKey(user.Key), "a default-key rotation must not touch user keys");
    }

    // ------------------------------------------------------------------
    //  Unix permissions (0600)
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnixFiles_CarryOwnerOnlyPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 0600 is a Unix-only concern; no-op on Windows by design.
        }

        var store = CreateStore(_contentRoot, new PlainKeyProtector());
        _ = store.GetDefaultKey();
        var created = (await store.CreateUserKeyAsync("perm", null)).Created!;
        _ = created;

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, File.GetUnixFileMode(DefaultKeyPath(_contentRoot)));
        Assert.Equal(expected, File.GetUnixFileMode(UserKeysPath(_contentRoot)));
    }
}
