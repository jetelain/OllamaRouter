using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;

namespace OllamaRouter.Security;

/// <summary>
/// One operator-created user API key as reported by <c>GET /api/router/api-keys</c> (task #50):
/// the identity and the timestamps ONLY — never the hash or the raw key value.
/// </summary>
/// <param name="Name">The key name (case-insensitively unique).</param>
/// <param name="CreatedAt">Creation time (ISO 8601 UTC in the wire format).</param>
/// <param name="ExpiresAt">Expiry time; null = no expiry.</param>
public sealed record ApiKeyInfo(string Name, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>
/// Outcome of a successful user key creation (task #50). The <see cref="Key"/> is the raw
/// base64url key returned to the operator EXACTLY ONCE — only its SHA-256 hash is persisted.
/// </summary>
public sealed record ApiKeyCreated(string Name, string Key, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>
/// Categorizes a failed user key creation so the endpoint maps it to the right status code:
/// 400 (invalid name / validFor), 409 (duplicate name) or 500 (persistence failure).
/// </summary>
public enum ApiKeyCreateFailure
{
    /// <summary>No failure (the creation succeeded).</summary>
    None,

    /// <summary>Invalid key name or <c>validFor</c> value → 400.</summary>
    InvalidInput,

    /// <summary>A user key with the same name already exists (case-insensitive) → 409.</summary>
    Duplicate,

    /// <summary>The key file could not be written → 500 (the in-memory store is untouched).</summary>
    PersistError
}

/// <summary>
/// Outcome of <see cref="IApiKeyStore.CreateUserKeyAsync"/>: on success <see cref="Created"/>
/// carries the raw key (returned once); on failure <see cref="Error"/> + <see cref="Failure"/>
/// explain why. Never throws.
/// </summary>
public sealed record ApiKeyCreateResult(bool Success, ApiKeyCreated? Created, string? Error, ApiKeyCreateFailure Failure);

/// <summary>
/// The API key store (task #50): owns the two key files next to <c>config/config.json</c> —
/// <c>config/default-key.dat</c> (the auto-generated default key, wrapped by
/// <see cref="IKeyProtector"/>: DPAPI on Windows, plain + <c>0600</c> on Unix) and
/// <c>config/api-keys.json</c> (user keys as SHA-256 hashes + timestamps only; raw user keys
/// never touch disk).
/// <para>
/// Load semantics (lazy, once-only, on the first store operation): a MISSING
/// <c>default-key.dat</c> is generated + persisted + logged at INFO (raw key, by design); a
/// PRESENT but corrupt / unwrappable / malformed file is logged, regenerated and re-persisted
/// (fail-open — startup is never bricked). A missing <c>api-keys.json</c> is an empty user-key
/// store (the file is created only on the first user-key mutation); a corrupt one is treated as
/// empty with an error log. All file writes are atomic (same-directory temp file +
/// <see cref="File.Move(string, string, bool)"/>) with <c>0600</c> Unix permissions.
/// </para>
/// <para>
/// Validation is constant-time: a format pre-filter, then
/// <see cref="CryptographicOperations.FixedTimeEquals"/> against the default key and the
/// stored user-key hashes (a matched user key is rejected when expired). Expiry is checked
/// lazily at validation time — no sweeper.
/// </para>
/// </summary>
public interface IApiKeyStore
{
    /// <summary>Absolute path of the (protected) default key file.</summary>
    string DefaultKeyFilePath { get; }

    /// <summary>Absolute path of the user-key hash store file.</summary>
    string UserKeysFilePath { get; }

    /// <summary>Returns the current raw default key (triggers the lazy load-or-create).</summary>
    string GetDefaultKey();

    /// <summary>Returns the <c>generatedAt</c> of the current default key.</summary>
    DateTimeOffset GetDefaultKeyGeneratedAt();

    /// <summary>
    /// ALWAYS rotates the default key (manual rotation ignores the elapsed interval): generates
    /// a fresh key, atomically replaces <c>default-key.dat</c>, logs the new raw key at INFO and
    /// returns it. The previous key is invalid immediately (no grace period). Never throws.
    /// </summary>
    Task<string> RotateDefaultKeyAsync();

    /// <summary>
    /// Rotates the default key when its <c>generatedAt</c> is older than
    /// <c>ApiSecurity.DefaultKeyRotationHours</c> (option read LIVE from the monitor); returns
    /// whether a rotation happened. Same no-grace-period semantics as
    /// <see cref="RotateDefaultKeyAsync"/>. Never throws.
    /// </summary>
    Task<bool> RotateDefaultKeyIfStaleAsync();

    /// <summary>
    /// Creates a user API key: validates <paramref name="name"/> (non-empty, ≤ 64 chars, no
    /// whitespace / <c>/</c> / <c>\</c> / <c>:</c> / control chars, case-insensitively unique)
    /// and <paramref name="validForHours"/> (null = no expiry, else ≥ 1); generates the key,
    /// persists only its SHA-256 hash (+ timestamps, atomic, <c>0600</c> on Unix) and returns
    /// the raw key ONCE. Never throws (failures are reported via <see cref="ApiKeyCreateResult"/>).
    /// </summary>
    Task<ApiKeyCreateResult> CreateUserKeyAsync(string name, int? validForHours);

    /// <summary>Returns the masked user key list (name + timestamps only; never hashes or raw values).</summary>
    IReadOnlyList<ApiKeyInfo> GetUserKeys();

    /// <summary>Deletes the user key named <paramref name="name"/> (case-insensitive); returns false when unknown.</summary>
    Task<bool> DeleteUserKeyAsync(string name);

    /// <summary>
    /// Validates a presented key (default key or user key hash, constant-time); an expired user
    /// key is rejected. Never throws.
    /// </summary>
    bool ValidateKey(string? presentedKey);
}

/// <inheritdoc />
public sealed class ApiKeyStore : IApiKeyStore
{

    /// <summary>One persisted user key: the SHA-256 hash bytes (never the raw key) + timestamps.</summary>
    private sealed record StoredUserKey(string Name, byte[] Hash, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly IKeyProtector _keyProtector;
    private readonly ILogger<ApiKeyStore>? _logger;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private volatile bool _loaded;

    /// <summary>Immutable snapshot of the default key (a struct cannot be volatile).</summary>
    private sealed record DefaultKeyState(string Key, DateTimeOffset GeneratedAt);

    private volatile DefaultKeyState? _defaultKeyState;
    private volatile IReadOnlyList<StoredUserKey> _userKeys = Array.Empty<StoredUserKey>();

    private string _defaultKey => _defaultKeyState!.Key;
    private DateTimeOffset _defaultKeyGeneratedAt => _defaultKeyState!.GeneratedAt;

    /// <inheritdoc />
    public string DefaultKeyFilePath { get; }

    /// <inheritdoc />
    public string UserKeysFilePath { get; }

    /// <summary>
    /// Creates the key store. Mirrors <c>RouterConfigPersistence</c>: the host environment is
    /// resolved DEFENSIVELY from <paramref name="serviceProvider"/> (a bare test
    /// ServiceCollection host without one falls back to the assembly base directory). The
    /// key FILES are touched only on the first store operation (lazy load), so a host that
    /// never resolves the store never writes to the filesystem.
    /// </summary>
    /// <param name="options">The live options monitor (supplies the LIVE rotation interval).</param>
    /// <param name="serviceProvider">The application service provider (IHostEnvironment resolution).</param>
    /// <param name="keyProtector">The platform at-rest protector (DPAPI on Windows, plain on Unix).</param>
    /// <param name="logger">Optional logger.</param>
    public ApiKeyStore(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        IKeyProtector keyProtector,
        ILogger<ApiKeyStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(keyProtector);

        _options = options;
        _keyProtector = keyProtector;
        _logger = logger;

        var environment = serviceProvider.GetService<IHostEnvironment>();
        var contentRoot = string.IsNullOrWhiteSpace(environment?.ContentRootPath)
            ? AppContext.BaseDirectory
            : environment.ContentRootPath;
        var configDirectory = Path.Combine(contentRoot, "config");
        DefaultKeyFilePath = Path.Combine(configDirectory, "default-key.dat");
        UserKeysFilePath = Path.Combine(configDirectory, "api-keys.json");
    }

    /// <inheritdoc />
    public string GetDefaultKey()
    {
        EnsureLoaded();
        return _defaultKey;
    }

    /// <inheritdoc />
    public DateTimeOffset GetDefaultKeyGeneratedAt()
    {
        EnsureLoaded();
        return _defaultKeyGeneratedAt;
    }

    /// <inheritdoc />
    public async Task<string> RotateDefaultKeyAsync()
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLoadedCore();
            return RotateCore();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RotateDefaultKeyIfStaleAsync()
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLoadedCore();
            var rotationHours = _options.CurrentValue.ApiSecurity.DefaultKeyRotationHours;
            if (DateTimeOffset.UtcNow - _defaultKeyGeneratedAt < TimeSpan.FromHours(rotationHours))
            {
                return false;
            }

            RotateCore();
            return true;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ApiKeyCreateResult> CreateUserKeyAsync(string name, int? validForHours)
    {
        var nameError = ValidateKeyName(name);
        if (nameError is not null)
        {
            return new ApiKeyCreateResult(false, null, nameError, ApiKeyCreateFailure.InvalidInput);
        }

        if (validForHours is { } hours && hours < 1)
        {
            return new ApiKeyCreateResult(
                false, null, "validFor must be >= 1 (number of hours)", ApiKeyCreateFailure.InvalidInput);
        }

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLoadedCore();
            if (_userKeys.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return new ApiKeyCreateResult(
                    false, null, $"'{name}' already exists", ApiKeyCreateFailure.Duplicate);
            }

            var key = ApiKeyCrypto.GenerateKey();
            var createdAt = DateTimeOffset.UtcNow;
            DateTimeOffset? expiresAt = validForHours is { } validFor ? createdAt.AddHours(validFor) : null;
            var updated = new List<StoredUserKey>(_userKeys.Count + 1);
            updated.AddRange(_userKeys);
            updated.Add(new StoredUserKey(name, ApiKeyCrypto.HashKey(key), createdAt, expiresAt));
            _userKeys = updated;

            await PersistUserKeysAsync().ConfigureAwait(false);
            _logger?.LogInformation(
                "Created user API key '{Name}' (expiry: {Expiry}); the raw key was returned to the caller once.",
                name, expiresAt is { } expiry ? expiry.ToString("O") : "never");
            return new ApiKeyCreateResult(true, new ApiKeyCreated(name, key, createdAt, expiresAt), null, ApiKeyCreateFailure.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist the user API key store after creating '{Name}'.", name);
            return new ApiKeyCreateResult(
                false, null, $"failed to persist the user API key store: {ex.Message}", ApiKeyCreateFailure.PersistError);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ApiKeyInfo> GetUserKeys()
    {
        EnsureLoaded();
        return _userKeys
            .Select(k => new ApiKeyInfo(k.Name, k.CreatedAt, k.ExpiresAt))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteUserKeyAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLoadedCore();
            var remaining = _userKeys
                .Where(k => !string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (remaining.Count == _userKeys.Count)
            {
                return false;
            }

            _userKeys = remaining;
            await PersistUserKeysAsync().ConfigureAwait(false);
            _logger?.LogInformation("Deleted user API key '{Name}'.", name);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist the user API key store after deleting '{Name}'.", name);
            return false;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <inheritdoc />
    public bool ValidateKey(string? presentedKey)
    {
        if (string.IsNullOrWhiteSpace(presentedKey))
        {
            return false;
        }

        EnsureLoaded();

        presentedKey = presentedKey.Trim();
        // Fast structural reject (length + base64url alphabet + decodes to 32 bytes) BEFORE any
        // constant-time comparison; a structurally invalid key can never be a valid key.
        if (!ApiKeyCrypto.IsValidApiKeyFormat(presentedKey))
        {
            return false;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presentedKey);
        if (CryptographicOperations.FixedTimeEquals(presentedBytes, Encoding.UTF8.GetBytes(_defaultKey)))
        {
            return true;
        }

        var presentedHash = ApiKeyCrypto.HashKey(presentedKey);
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _userKeys)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedHash, key.Hash))
            {
                // Lazily enforced expiry: a matched key is rejected once its expiry has passed.
                return key.ExpiresAt is null || key.ExpiresAt > now;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates a user key name: non-empty, ≤ 64 chars, no whitespace, no <c>/</c> / <c>\</c> /
    /// <c>:</c>, no control characters (same spirit as the target-name validation). Returns an
    /// error message, or null when valid.
    /// </summary>
    public static string? ValidateKeyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "a non-empty key name is required";
        }

        name = name.Trim();
        if (name.Length > 64)
        {
            return "the key name must be at most 64 characters";
        }

        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || c == '/' || c == '\\' || c == ':' || char.IsControl(c))
            {
                return "the key name must not contain whitespace, '/', '\\', ':' or control characters";
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    //  Lazy load-or-create (once-only, behind the state gate)
    // ------------------------------------------------------------------

    /// <summary>
    /// Ensures the in-memory key state is loaded (synchronous, once-only): the first call pays
    /// a small file-IO cost (the default key file + the optional user-key file), every later
    /// call returns immediately. Mutating operations hold the same gate, so the load can never
    /// interleave with a mutation.
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _stateGate.Wait();
        try
        {
            EnsureLoadedCore();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>Loads the key state when not yet loaded. Caller holds <see cref="_stateGate"/>.</summary>
    private void EnsureLoadedCore()
    {
        if (_loaded)
        {
            return;
        }

        LoadDefaultKey();
        LoadUserKeys();
        _loaded = true;
    }

    /// <summary>
    /// Load-or-create of the default key. FAIL-OPEN by contract: on ANY failure (unprotect
    /// error, malformed JSON, invalid key format) the key is regenerated, re-persisted and
    /// logged at INFO — startup is never bricked and never throws.
    /// </summary>
    private void LoadDefaultKey()
    {
        try
        {
            if (!File.Exists(DefaultKeyFilePath))
            {
                LogGeneratedKey(AdoptNewDefaultKey(), "generated (first start)");
                return;
            }

            var rawBytes = File.ReadAllBytes(DefaultKeyFilePath);
            var envelopeBytes = _keyProtector.Unprotect(rawBytes);
            using var document = JsonDocument.Parse(envelopeBytes);
            var root = document.RootElement;
            var key = root.GetProperty("key").GetString();
            if (key is null)
            {
                throw new FormatException("envelope is missing 'key'");
            }

            var generatedAtRaw = root.GetProperty("generatedAt").GetString();
            if (string.IsNullOrWhiteSpace(generatedAtRaw) ||
                !DateTimeOffset.TryParse(generatedAtRaw, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var generatedAt))
            {
                throw new FormatException("envelope is missing a valid 'generatedAt'");
            }

            if (!ApiKeyCrypto.IsValidApiKeyFormat(key))
            {
                throw new FormatException("stored key is not a valid 32-byte base64url key");
            }

            _defaultKeyState = new DefaultKeyState(key, generatedAt);
            _logger?.LogDebug("Loaded the default API key from {Path} (generatedAt {GeneratedAt}).",
                DefaultKeyFilePath, generatedAt.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                "The default API key file {Path} is missing, corrupt or unreadable ({Error}); regenerating a fresh default key.",
                DefaultKeyFilePath, ex.Message);
            LogGeneratedKey(AdoptNewDefaultKey(), "regenerated after a corrupt or unreadable file");
        }
    }

    /// <summary>Generates a fresh default key, atomically persists it and adopts it in memory.</summary>
    private (string Key, DateTimeOffset GeneratedAt) AdoptNewDefaultKey()
    {
        var key = ApiKeyCrypto.GenerateKey();
        var generatedAt = DateTimeOffset.UtcNow;
        PersistDefaultKey(key, generatedAt);
        _defaultKeyState = new DefaultKeyState(key, generatedAt);
        return (key, generatedAt);
    }

    /// <summary>
    /// Logs a freshly generated/rotated default key at INFO with the RAW key value — a
    /// deliberate, documented trade-off so local same-user operators can capture the key (the
    /// DPAPI blob on Windows is opaque without the same-user unwrap).
    /// </summary>
    private void LogGeneratedKey((string Key, DateTimeOffset GeneratedAt) adopted, string reason)
    {
        _logger?.LogInformation(
            "{Reason} the default API key (stored at {Path}); the raw key is shown ONCE here " +
            "(callers must re-read it after a 401 following a rotation): {Key}",
            string.IsNullOrEmpty(reason) ? "Generated" : reason[0].ToString().ToUpperInvariant() + reason[1..],
            DefaultKeyFilePath,
            adopted.Key);
    }

    /// <summary>
    /// Loads the user key hash store. A MISSING file means an empty store (the file is created
    /// only on the first mutation); a CORRUPT file is treated as an empty store with an error
    /// log (fail-open, never throws).
    /// </summary>
    private void LoadUserKeys()
    {
        if (!File.Exists(UserKeysFilePath))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(UserKeysFilePath);
            using var document = JsonDocument.Parse(text);
            var list = new List<StoredUserKey>();
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString();
                var hashB64 = entry.GetProperty("hash").GetString();
                if (string.IsNullOrWhiteSpace(name) || hashB64 is null)
                {
                    continue;
                }

                DateTimeOffset? createdAt = null;
                if (entry.TryGetProperty("createdAt", out var created) &&
                    created.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(created.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsedCreated))
                {
                    createdAt = parsedCreated;
                }

                DateTimeOffset? expiresAt = null;
                if (entry.TryGetProperty("expiresAt", out var expires) &&
                    expires.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(expires.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsedExpires))
                {
                    expiresAt = parsedExpires;
                }

                list.Add(new StoredUserKey(name, Convert.FromBase64String(hashB64), createdAt ?? DateTimeOffset.UtcNow, expiresAt));
            }

            _userKeys = list;
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                "The user API key file {Path} is corrupt; treating the user key store as EMPTY ({Error}).",
                UserKeysFilePath, ex.Message);
            _userKeys = Array.Empty<StoredUserKey>();
        }
    }

    // ------------------------------------------------------------------
    //  Mutation core (caller holds the state gate)
    // ------------------------------------------------------------------

    /// <summary>Generates a new default key, atomically persists it and adopts it in memory. Returns the new raw key.</summary>
    private string RotateCore()
    {
        var (key, _) = AdoptNewDefaultKey();
        _logger?.LogInformation(
            "Rotated the default API key (stored at {Path}); the previous key is INVALID IMMEDIATELY (no grace period). New raw key (shown ONCE here): {Key}",
            DefaultKeyFilePath, key);
        return key;
    }

    /// <summary>
    /// Persists the default key envelope <c>{"key": "...", "generatedAt": "..."}</c> through the
    /// protector (DPAPI blob on Windows; plain UTF-8 + <c>0600</c> on Unix) with an atomic write.
    /// </summary>
    private void PersistDefaultKey(string key, DateTimeOffset generatedAt)
    {
        var envelope = new JsonObject
        {
            ["key"] = key,
            ["generatedAt"] = generatedAt.ToString("O")
        };
        var protectedBytes = _keyProtector.Protect(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
        AtomicWriteBytes(DefaultKeyFilePath, protectedBytes);
    }

    /// <summary>
    /// Persists the user key store as pretty-printed <c>{"version":1,"keys":[...]}</c>: name +
    /// base64(SHA-256 hash) + createdAt + expiresAt (null = no expiry). Raw user keys NEVER
    /// touch the file. Atomic write + <c>0600</c> on Unix.
    /// </summary>
    private async Task PersistUserKeysAsync()
    {
        // Source-gen serialized file format (hashes only — raw keys never touch the file; no
        // reflection, NativeAOT-safe): version + masked entries, expiresAt null = no expiry.
        var file = new ApiKeyFile(
            1,
            _userKeys.Select(k => new ApiKeyFileEntry(
                k.Name,
                Convert.ToBase64String(k.Hash),
                k.CreatedAt.ToString("O"),
                k.ExpiresAt is { } exp ? exp.ToString("O") : null))
                .ToList());
        await AtomicWriteTextAsync(
            UserKeysFilePath,
            JsonSerializer.Serialize(file, OllamaRouterJsonSerializerContext.Default.ApiKeyFile))
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Atomic file writes (same pattern as RouterConfigPersistence)
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="bytes"/> to a same-directory temp file (with <c>0600</c> Unix
    /// permissions) and moves it over <paramref name="path"/> (atomic on the same volume).
    /// On any failure the temp file is removed and the exception rethrown (callers decide the
    /// failure semantics); the previous file content is never clobbered mid-way.
    /// </summary>
    private void AtomicWriteBytes(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        var tempPath = string.Empty;
        try
        {
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
            File.WriteAllBytes(tempPath, bytes);
            SecretFilePermissions.ApplyOwnerOnly(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (tempPath is not null && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Temp-file cleanup is best-effort; the exception is what matters.
            }

            throw;
        }
    }

    /// <summary>Async text variant of <see cref="AtomicWriteBytes"/> (same pattern).</summary>
    private async Task AtomicWriteTextAsync(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)!;
        var tempPath = string.Empty;
        try
        {
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(tempPath, contents).ConfigureAwait(false);
            SecretFilePermissions.ApplyOwnerOnly(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (tempPath is not null && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Temp-file cleanup is best-effort; the exception is what matters.
            }

            throw;
        }
    }
}

/// <summary>
/// Wire format row of <c>config/api-keys.json</c> (task #50): hashes ONLY (SHA-256 of the
/// base64url key, base64-encoded) + timestamps; lower-case property names to match the
/// documented format; <c>expiresAt</c> JSON null = no expiry. Raw user keys NEVER touch this
/// file. AOT-safe source-gen serialization (registered in
/// <see cref="Serialization.OllamaRouterJsonSerializerContext"/>).
/// </summary>
public sealed record ApiKeyFileEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("expiresAt")] string? ExpiresAt);

/// <summary>
/// Carrier record of <c>config/api-keys.json</c> (task #50): format version + the masked
/// user-key entries.
/// </summary>
public sealed record ApiKeyFile(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("keys")] IReadOnlyList<ApiKeyFileEntry> Keys);
