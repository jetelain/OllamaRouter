using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Options;
using OllamaRouter.Security;
using Pmad.LocalApi.Security;

namespace OllamaRouter.Configuration;

/// <summary>
/// Outcome of a <see cref="IRouterConfigPersistence"/> operation: success, or a failure with the
/// underlying error message. A failure ALWAYS means the in-memory options were left untouched
/// (the file write happens first — a 500 response never leaves memory and disk diverged).
/// </summary>
public sealed record RouterConfigPersistResult(bool Success, string? Error = null);

/// <summary>
/// Load-merge-atomic-write persistence of the user-owned router configuration layer
/// (<c>config/config.json</c> in the content root, next to the executable) for the dynamic fleet
/// configuration / Setup API (task #19).
/// <para>
/// All mutations follow the same order: (1) merge the change into the on-disk JSON (preserving
/// every unmanaged user key, including per-target keys like <c>SpeedFactor</c> and
/// <c>Recording:TokenMetricsCsv</c>), (2) write the file atomically (same-directory temp file +
/// <see cref="File.Move(string, string, bool)"/>), and only then (3) apply the same values to
/// the live <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c> instance (reference-swap semantics —
/// the <see cref="OllamaRouterOptions.Targets"/> list is never mutated in place) + a best-effort
/// <see cref="IConfigurationRoot.Reload"/> so the configuration-bound providers reconcile with the
/// new file. Every method never throws.
/// </para>
/// <para>
/// Precedence note: <c>config/config.json</c> sits BELOW environment variables / CLI arguments in
/// the configuration chain. If the operator pinned a value via environment, a PUT persists the
/// file value but the effective bound value keeps the environment value (standard .NET behavior;
/// the in-memory apply shows the API value until the next rebind).
    /// </para>
    /// <para>
    /// Keyring note (task #110): when the OS credential store is available, OpenAI target keys
    /// are written to the keyring BEFORE the atomic file write (keyring-first) and the file
    /// stores only the <c>keyring:&lt;targetName&gt;</c> marker; any keyring-write or file-write failure
    /// rolls back the keyring writes of that pass and returns failure, so a failure never
    /// leaves memory, file AND keyring diverged. Cleared / removed target credentials are
    /// deleted from the keyring AFTER a successful file write (fail-soft no-ops otherwise).
    /// </para>
    /// </summary>
    public interface IRouterConfigPersistence
{
    /// <summary>
    /// Absolute path of the user config file (<c>config/config.json</c> in the content root).
    /// </summary>
    string ConfigFilePath { get; }

    /// <summary>
    /// Merges the full target list (in-memory order) into <c>OllamaRouter:Targets</c>, writes the
    /// file atomically, then applies the list to the live in-memory options instance.
    /// Preserves per-target unmanaged JSON keys (KeepAliveSeconds, SpeedFactor, MaxVramMB, ...).
    /// On a legacy configuration (in-memory <c>Targets</c> empty, Local/Remote synthesized from
    /// <c>LocalUrl</c>/<c>RemoteUrl</c>) the caller passes the synthesized entries in the list,
    /// which materializes them into the array. Returns failure (no in-memory change) when the
    /// file cannot be written or the existing file is present but unparseable. Never throws.
    /// </summary>
    Task<RouterConfigPersistResult> PersistTargetsAsync(
        IReadOnlyList<OllamaTargetOptions> targets,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the provided sections (null sections ignored) to <c>config/config.json</c> (merge,
    /// preserving all other user keys incl. an existing <c>Recording:TokenMetricsCsv</c>), writes
    /// atomically, then applies the same values to the live in-memory options instance
    /// (reference-swap per section) + <c>Normalize()</c>. Same failure contract as
    /// <see cref="PersistTargetsAsync"/>. Never throws.
    /// </summary>
    Task<RouterConfigPersistResult> PersistConfigAsync(
        UpdateRouterConfigRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class RouterConfigPersistence : IRouterConfigPersistence
{
    private static readonly JsonSerializerOptions FileWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger? _logger;
    private readonly IKeyProtector _keyProtector;
    private readonly ICredentialStore? _credentialStore;

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <inheritdoc />
    public string ConfigFilePath { get; }

    /// <summary>
    /// Creates the persistence service. The host environment is resolved DEFENSIVELY from
    /// <paramref name="serviceProvider"/> (a bare test ServiceCollection host without one falls
    /// back to the assembly base directory). <paramref name="keyProtector"/> (task #61)
    /// wraps the per-target provider API keys at rest; a bare test host without one falls
    /// back to <see cref="PlainKeyProtector"/> (identity wrap — the wire format is identical,
    /// only the at-rest protection differs). <paramref name="credentialStore"/> (task #110)
    /// optionally routes OpenAI target keys to the OS credential store; a <c>null</c> store
    /// (bare test hosts) behaves exactly like an unavailable keyring (every key takes the
    /// <c>v1:</c> fallback path — byte-identical to the pre-task-#110 behavior).
    /// </summary>
    /// <param name="options">The live options monitor (the shared configuration-bound instance in production).</param>
    /// <param name="serviceProvider">The application service provider (IHostEnvironment / IConfiguration resolution).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="keyProtector">Optional at-rest protector for target API keys (defaults to the identity protector).</param>
    /// <param name="credentialStore">Optional OS credential store for OpenAI target keys (task #110; null = keyring-unavailable fallback).</param>
    public RouterConfigPersistence(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        ILogger? logger = null,
        IKeyProtector? keyProtector = null,
        ICredentialStore? credentialStore = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
        _keyProtector = keyProtector ?? new PlainKeyProtector();
        _credentialStore = credentialStore;

        var environment = serviceProvider.GetService<IHostEnvironment>();
        var contentRoot = string.IsNullOrWhiteSpace(environment?.ContentRootPath)
            ? AppContext.BaseDirectory
            : environment.ContentRootPath;
        ConfigFilePath = Path.Combine(contentRoot, "config", "config.json");
    }

    /// <inheritdoc />
    public async Task<RouterConfigPersistResult> PersistTargetsAsync(
        IReadOnlyList<OllamaTargetOptions> targets,
        CancellationToken cancellationToken = default)
    {
        if (targets is null)
        {
            return Failure("targets list is null");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (error, root) = await LoadOrCreateRootAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return Failure(error);
            }

            var router = EnsureSection(root!);
            var existingByName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            if (router["Targets"] is JsonArray existingArray)
            {
                foreach (var existing in existingArray.OfType<JsonObject>())
                {
                    if (existing["Name"]?.GetValue<string>() is { } name && !existingByName.ContainsKey(name))
                    {
                        existingByName.Add(name, existing);
                    }
                }
            }

            // Rebuild the Targets array in in-memory order: existing on-disk objects keep their
            // unmanaged keys (modeled fields are re-applied on top), brand-new targets get a
            // minimal object. The array is CLEARED first — that detaches every child node (a
            // JsonNode may only have one parent) — and the reused objects are added back into
            // the same (still attached) array instance.
            if (router["Targets"] is not JsonArray targetsArray)
            {
                targetsArray = new JsonArray();
                router["Targets"] = targetsArray;
            }

            targetsArray.Clear();
            var reusedNodes = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
            // Task #110 keyring plan: evaluated ONCE per call (live IsAvailable probe). sets =
            // keyring writes to execute BEFORE the file write (with the prior value captured
            // for rollback); deletes = keyring entries to remove AFTER a successful write
            // (fail-soft; orphans are inert). The plan is built while the nodes are rebuilt
            // below, then executed around the atomic write (see the ordering contract in the
            // ApplyHeterogeneousTargetFields + rollback comments).
            var keyringAvailable = _credentialStore is { } keyringStore && keyringStore.IsAvailable;
            var keyringSets = new List<(string Name, string Key, string? PreviousValue)>();
            var keyringDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                JsonObject node = new();
                if (target.Name is { } targetName &&
                    existingByName.Remove(targetName, out var existing) &&
                    reusedNodes.Add(existing))
                {
                    node = existing;
                }

                node["Name"] = target.Name;
                node["Url"] = target.Url;
                node["Enabled"] = target.Enabled;
                node["MaxParallel"] = target.MaxParallel;
                node["HardwareHint"] = target.HardwareHint;
                ApplyHeterogeneousTargetFields(node, target, keyringAvailable, keyringSets, keyringDeletes);
                targetsArray.Add((JsonNode?)node);
            }

            // Targets present on disk but REMOVED from the new list: their keyring credentials
            // must not outlive the configuration (DELETE /api/router/targets/{name} rides this
            // path; a cleared key of a surviving target was already collected above). Harmless
            // no-ops when no entry exists (legacy v1:-only configurations).
            if (keyringAvailable)
            {
                foreach (var removedName in existingByName.Keys)
                {
                    keyringDeletes.Add(removedName);
                }
            }

            // KEYRING-FIRST (task #110): execute the credential writes BEFORE the file write so
            // a failed file write cannot leave a keyring: marker on disk pointing at a secret
            // that was never stored. A SetPassword failure rolls back every set of THIS pass
            // (restore the prior value, or delete when there was none) and 500s with the file
            // and the in-memory options untouched — the "500 never leaves the stores diverged"
            // invariant, extended to the third store.
            var executedSets = new List<(string Name, string? PreviousValue)>();
            if (keyringAvailable && keyringSets.Count > 0)
            {
                try
                {
                    foreach (var (setName, setKey, setPrevious) in keyringSets)
                    {
                        _credentialStore!.SetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, setName, setKey);
                        executedSets.Add((setName, setPrevious));
                    }
                }
                catch (Exception ex)
                {
                    RollbackExecutedKeyringSets(executedSets);
                    return Failure($"failed to store the target API key in the OS credential store: {ex.Message}");
                }
            }

            var writeResult = await AtomicWriteAsync(root!, cancellationToken).ConfigureAwait(false);
            if (!writeResult.Success)
            {
                RollbackExecutedKeyringSets(executedSets);
                return writeResult;
            }

            // DELETES AFTER the successful write (task #110): fail-soft by contract — a failed
            // delete leaves an inert orphan the config no longer references (retried on the
            // next clear/remove of the same name, or overwritten on the next set).
            if (keyringAvailable)
            {
                foreach (var deleteName in keyringDeletes)
                {
                    _credentialStore?.DeletePassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, deleteName);
                }
            }

            ApplyTargetsInMemory(targets);
            ReloadConfigurationBestEffort();
            return new RouterConfigPersistResult(true);
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Best-effort rollback of the keyring writes already executed in a FAILED persist pass
    /// (task #110): every set is restored in REVERSE order — the captured prior value is
    /// re-written, or the entry is deleted when there was no prior value. Rollback failures
    /// are logged and never masked (the original failure result is what matters); the config
    /// file was never written, so a leftover new value only lingers until the next successful
    /// update of the same target name.
    /// </summary>
    private void RollbackExecutedKeyringSets(List<(string Name, string? PreviousValue)> executedSets)
    {
        for (var i = executedSets.Count - 1; i >= 0; i--)
        {
            var (name, previous) = executedSets[i];
            try
            {
                if (previous is null)
                {
                    // No prior value: restore "absent" (DeletePassword is fail-soft by contract).
                    _credentialStore?.DeletePassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, name);
                }
                else
                {
                    _credentialStore?.SetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, name, previous);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Failed to roll back the OS keyring credential of target '{TargetName}' after a persistence failure; " +
                    "the keyring may hold a value the configuration no longer references until the next update.",
                    name);
            }
        }
    }

    /// <summary>
    /// Writes (or clears) the task #61 modeled fields of one target node: <c>Type</c> (enum
    /// name), <c>Provider</c> (null omitted — removed when cleared so a stale value never
    /// lingers), <c>Models</c> (array of <c>{name, contextLength?, quantization?}</c>, omitted
    /// when empty) and <c>ApiKey</c> — the node REMOVED when the key is null (a cleared key
    /// must not linger on disk). The raw key never appears in the file in plaintext.
    /// <para>
    /// At-rest key format (task #110; <paramref name="keyringAvailable"/> +
    /// <paramref name="keyringSets"/> + <paramref name="keyringDeletes"/> are the per-call
    /// plan of the enclosing persist pass): OpenAI targets with a PLAINTEXT in-memory key
    /// store the secret in the OS credential store (package <c>Pmad.OllamaRouter</c>, service
    /// <c>openai</c>, username = target name) and the file keeps only the
    /// <c>keyring:<targetName></c> marker (the write is queued in <paramref name="keyringSets"/>
    /// with its prior value for rollback and executed by the caller BEFORE the file write).
    /// Everything else — keyring unavailable, non-OpenAI target (defensive), or a value already
    /// carrying a <c>v1:</c> / <c>keyring:</c> marker (defensive pass-through, never re-processed)
    /// — takes the <see cref="IKeyProtector"/> <c>v1:</c> + Base64 envelope (legacy plaintext
    /// values are re-wrapped). A CLEARED key queues the target name in
    /// <paramref name="keyringDeletes"/> so the credential does not outlive the config (the
    /// caller executes the deletes AFTER a successful file write; they are fail-soft no-ops
    /// when no entry exists).
    /// </para>
    /// </summary>
    private void ApplyHeterogeneousTargetFields(
        JsonObject node,
        OllamaTargetOptions target,
        bool keyringAvailable,
        List<(string Name, string Key, string? PreviousValue)> keyringSets,
        HashSet<string> keyringDeletes)
    {
        node["Type"] = target.Type.ToString();

        if (target.Provider is { Length: > 0 } provider)
        {
            node["Provider"] = provider;
        }
        else
        {
            node.Remove("Provider");
        }

        var declaredModels = target.Models
            ?.Where(m => !string.IsNullOrWhiteSpace(m?.Name))
            .ToList();
        if (declaredModels is { Count: > 0 })
        {
            var modelsArray = new JsonArray();
            foreach (var model in declaredModels)
            {
                var modelNode = new JsonObject { ["name"] = model!.Name.Trim() };
                if (model.ContextLength.HasValue)
                {
                    modelNode["contextLength"] = model.ContextLength.Value;
                }
                if (model.Quantization is { Length: > 0 })
                {
                    modelNode["quantization"] = model.Quantization;
                }
                // Cast selects the trim-safe Add(JsonNode?) overload; the generic Add<T>(T)
                // is [RequiresUnreferencedCode]/[RequiresDynamicCode] (IL2026/IL3050 under AOT).
                modelsArray.Add((JsonNode?)modelNode);
            }

            node["Models"] = modelsArray;
        }
        else
        {
            node.Remove("Models");
        }

        if (target.ApiKey is { Length: > 0 } key && target.Name is { } targetName)
        {
            if (keyringAvailable && target.Type == TargetType.OpenAI &&
                !key.StartsWith(TargetApiKeyUnwrapPostProcessor.KeyringPrefix, StringComparison.Ordinal) &&
                !key.StartsWith(TargetApiKeyUnwrapPostProcessor.WrappedPrefix, StringComparison.Ordinal))
            {
                // OS keyring path (task #110): the secret goes to the credential store (the
                // caller executes the queued write BEFORE the file write), the file keeps only
                // the marker. The prior value is captured NOW (before any keyring mutation of
                // this pass) so a later failure can restore the exact previous state.
                var previous = _credentialStore!.GetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, targetName);
                keyringSets.Add((targetName, key, previous));
                node["ApiKey"] = TargetApiKeyUnwrapPostProcessor.KeyringPrefix + targetName;
            }
            else
            {
                node["ApiKey"] = WrapKey(key);
            }
        }
        else
        {
            node.Remove("ApiKey");
            if (keyringAvailable && target.Name is { } clearedName)
            {
                // Cleared key: the credential (if any) must not outlive the config. Harmless
                // no-op when the store holds no entry for the name (legacy v1:-only stores).
                keyringDeletes.Add(clearedName);
            }
        }

        // Task #84: node power-draw override (watts); the key is REMOVED when unset (a
        // JSON null would mean "explicitly cleared" on the wire — the documented clear path
        // is the absent key, matching the Provider/ApiKey pattern above).
        if (target.PowerWatts is { } powerWatts)
        {
            node["PowerWatts"] = powerWatts;
        }
        else
        {
            node.Remove("PowerWatts");
        }

        // Task #143: the daemon-side hardware probe ssh credential; the keys are REMOVED
        // when unset (a cleared field never lingers on disk — the PowerWatts pattern).
        if (target.SshUser is { } sshUser && sshUser.Length > 0)
        {
            node["SshUser"] = sshUser;
        }
        else
        {
            node.Remove("SshUser");
        }

        if (target.SshPort is { } sshPort)
        {
            node["SshPort"] = sshPort;
        }
        else
        {
            node.Remove("SshPort");
        }
    }

    /// <summary>
    /// Wraps one plaintext key for at rest: <c>v1:</c> + Base64(protected UTF-8 bytes).
    /// A value already carrying the <c>v1:</c> prefix (defensive: an in-memory snapshot that
    /// skipped the bind-time unwrap) or the <c>keyring:</c> marker (task #110 defensive
    /// pass-through) is persisted AS-IS — re-wrapping would double-encode it and the single
    /// unwrap pass on the next reload could not restore the plaintext. On the
    /// normal path the in-memory options always carry the unwrapped key, so this is a no-op.
    /// </summary>
    private string WrapKey(string key)
    {
        if (key.StartsWith(TargetApiKeyUnwrapPostProcessor.WrappedPrefix, StringComparison.Ordinal) ||
            key.StartsWith(TargetApiKeyUnwrapPostProcessor.KeyringPrefix, StringComparison.Ordinal))
        {
            return key;
        }

        return $"{TargetApiKeyUnwrapPostProcessor.WrappedPrefix}{Convert.ToBase64String(_keyProtector.Protect(System.Text.Encoding.UTF8.GetBytes(key)))}";
    }

    /// <inheritdoc />
    public async Task<RouterConfigPersistResult> PersistConfigAsync(
        UpdateRouterConfigRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (error, root) = await LoadOrCreateRootAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return Failure(error);
            }

            var router = EnsureSection(root!);

            if (request.Cloud is { } cloud)
            {
                var cloudNode = EnsureObjectNode(router, "CloudOverflow");
                cloudNode["Enabled"] = cloud.Enabled;
                if (cloud.DefaultModel is not null)
                {
                    // "" clears the default model (documented "clear" semantics): persist it as
                    // JSON null — binding it back yields a null string, the "unset" state.
                    cloudNode["DefaultModel"] =
                        string.IsNullOrEmpty(cloud.DefaultModel) ? null : cloud.DefaultModel;
                }
                if (cloud.PreferredRelayTarget is not null)
                {
                    // "" clears the preference to the default relay ("Local"). Persist the
                    // CANONICAL form (not the raw empty string) so a restart cannot drift the
                    // value away from the live in-memory apply ("" on disk would mean "no
                    // preferred relay" — fleet order — instead of "Local preferred").
                    cloudNode["PreferredRelayTarget"] =
                        string.IsNullOrEmpty(cloud.PreferredRelayTarget) ? "Local" : cloud.PreferredRelayTarget;
                }
            }

            if (request.DynamicContext is { } dynamicContext)
            {
                var dynamicNode = EnsureObjectNode(router, "DynamicContext");
                dynamicNode["Enabled"] = dynamicContext.Enabled;
                dynamicNode["DenseClampEnabled"] = dynamicContext.DenseClampEnabled;
                dynamicNode["AutoShrinkCooldownTurns"] = dynamicContext.AutoShrinkCooldownTurns;
                dynamicNode["AutoShrinkIdleTimeoutSeconds"] = dynamicContext.AutoShrinkIdleTimeoutSeconds;
            }

            if (request.Recording is { } recording)
            {
                // TokenMetricsCsv is intentionally NOT written: an existing section on disk is
                // preserved untouched (see ApplyConfigInMemory for the in-memory counterpart).
                var recordingNode = EnsureObjectNode(router, "Recording");
                recordingNode["Enabled"] = recording.Enabled;
                recordingNode["OutputDirectory"] = recording.OutputDirectory;
                recordingNode["MaxFiles"] = recording.MaxFiles;
            }

            if (request.Resilience is { } resilience)
            {
                router["MaxConsecutiveFailures"] = resilience.MaxConsecutiveFailures;
                router["ConsecutiveFailuresTokenTolerance"] = resilience.ConsecutiveFailuresTokenTolerance;
            }

            if (request.Pricing is { } pricing)
            {
                var pricingNode = EnsureObjectNode(router, "Pricing");
                pricingNode["PromptPricePerMillion"] = pricing.PromptPricePerMillion;
                pricingNode["CompletionPricePerMillion"] = pricing.CompletionPricePerMillion;
                pricingNode["PricePerMillion"] = pricing.PricePerMillion;
                pricingNode["Currency"] = pricing.Currency;
                pricingNode["CachedPromptPricePerMillion"] = pricing.CachedPromptPricePerMillion;
                // Task #84: electricity price / default node power (full-section-replace
                // semantics: absent ⇒ JSON null ⇒ cleared ⇒ the effective defaults 0.20 / 150 W
                // apply at read time).
                pricingNode["ElectricityPricePerKwh"] = pricing.ElectricityPricePerKwh;
                pricingNode["DefaultNodePowerWatts"] = pricing.DefaultNodePowerWatts;
            }

            var writeResult = await AtomicWriteAsync(root!, cancellationToken).ConfigureAwait(false);
            if (!writeResult.Success)
            {
                return writeResult;
            }

            ApplyConfigInMemory(request);
            ReloadConfigurationBestEffort();
            return new RouterConfigPersistResult(true);
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Loads the existing file as a JSON object root, or creates an empty root when the file is
    /// absent. Returns (error: null, root) on success; (error message, null) when a PRESENT
    /// file is unparseable or has a non-object root (the file is left untouched).
    /// </summary>
    private async Task<(string? Error, JsonObject? Root)> LoadOrCreateRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigFilePath))
        {
            return (null, new JsonObject());
        }

        try
        {
            var text = await File.ReadAllTextAsync(ConfigFilePath, cancellationToken).ConfigureAwait(false);
            var parsed = JsonNode.Parse(text);
            if (parsed is not JsonObject objectRoot)
            {
                return ($"existing file {ConfigFilePath} does not contain a JSON object root", null);
            }
            return (null, objectRoot);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ($"existing file {ConfigFilePath} could not be parsed: {ex.Message}", null);
        }
    }

    /// <summary>Gets (or creates) the <c>OllamaRouter</c> section object within the file root.</summary>
    private static JsonObject EnsureSection(JsonObject root)
    {
        if (root["OllamaRouter"] is JsonObject section)
        {
            return section;
        }

        var created = new JsonObject();
        root["OllamaRouter"] = created;
        return created;
    }

    /// <summary>Gets (or creates) a nested section object within the <c>OllamaRouter</c> section.</summary>
    private static JsonObject EnsureObjectNode(JsonObject router, string sectionName)
    {
        if (router[sectionName] is JsonObject section)
        {
            return section;
        }

        var created = new JsonObject();
        router[sectionName] = created;
        return created;
    }

    /// <summary>
    /// Serializes the merged root to a same-directory temp file and moves it over the target
    /// path (atomic on the same volume); on any failure the temp file is removed and the
    /// failure is returned (the previous file content is never clobbered mid-way).
    /// </summary>
    private async Task<RouterConfigPersistResult> AtomicWriteAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ConfigFilePath)!;
        var tempPath = string.Empty;
        try
        {
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $"config.json.tmp-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(tempPath, root.ToJsonString(FileWriteOptions), cancellationToken).ConfigureAwait(false);
            // 0600 hardening (task #50): the user config layer lands owner-only on Unix
            // (behavior-neutral on Windows).
            SecretFilePermissions.ApplyOwnerOnly(tempPath);
            File.Move(tempPath, ConfigFilePath, overwrite: true);
            return new RouterConfigPersistResult(true);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(tempPath))
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Temp-file cleanup is best-effort; the failure result is what matters.
                }
            }

            return Failure(ex.Message);
        }
    }

    /// <summary>
    /// Applies the target list to the live options instance by ASSIGNING A NEW LIST REFERENCE
    /// (the existing list is never mutated in place — concurrent enumeration in
    /// <see cref="OllamaRouterOptions.GetEffectiveTargets"/> keeps seeing a stable snapshot).
    /// </summary>
    private void ApplyTargetsInMemory(IReadOnlyList<OllamaTargetOptions> targets)
    {
        var current = _options.CurrentValue;
        current.Targets = new List<OllamaTargetOptions>(targets);
        current.Normalize();
        _logger?.LogInformation(
            "OllamaRouter: applied {Count} fleet target(s) to the live configuration (persistence: {Path}).",
            targets.Count,
            ConfigFilePath);
    }

    /// <summary>
    /// Applies the provided sections to the live options instance (reference-swap per section,
    /// then <c>Normalize()</c>). Null DTO fields mean "no change" (cloud defaultModel /
    /// preferredRelayTarget); empty strings CLEAR them (defaultModel → null, preferredRelayTarget
    /// → "Local"). The existing <c>Recording.TokenMetricsCsv</c> is preserved in memory.
    /// </summary>
    private void ApplyConfigInMemory(UpdateRouterConfigRequest request)
    {
        var current = _options.CurrentValue;

        if (request.Cloud is { } cloud)
        {
            current.CloudOverflow = new CloudOverflowOptions
            {
                Enabled = cloud.Enabled,
                // "" clears: defaultModel → null, preferredRelayTarget → "Local" — the exact
                // same canonical values the file merge persists (file and memory can never
                // drift for a cleared field).
                DefaultModel = cloud.DefaultModel is null
                    ? current.CloudOverflow.DefaultModel
                    : (string.IsNullOrEmpty(cloud.DefaultModel) ? null : cloud.DefaultModel),
                PreferredRelayTarget = cloud.PreferredRelayTarget is null
                    ? current.CloudOverflow.PreferredRelayTarget
                    : (string.IsNullOrEmpty(cloud.PreferredRelayTarget) ? "Local" : cloud.PreferredRelayTarget)
            };
        }

        if (request.DynamicContext is { } dynamicContext)
        {
            current.DynamicContext = new DynamicContextOptions
            {
                Enabled = dynamicContext.Enabled,
                DenseClampEnabled = dynamicContext.DenseClampEnabled,
                AutoShrinkCooldownTurns = dynamicContext.AutoShrinkCooldownTurns,
                AutoShrinkIdleTimeoutSeconds = dynamicContext.AutoShrinkIdleTimeoutSeconds
            };
            current.DynamicContext.Normalize();
        }

        if (request.Recording is { } recording)
        {
            current.Recording = new RecordingOptions
            {
                Enabled = recording.Enabled,
                OutputDirectory = recording.OutputDirectory,
                MaxFiles = recording.MaxFiles,
                // The CSV sub-config is intentionally out of scope for the Setup API: preserve it.
                TokenMetricsCsv = current.Recording?.TokenMetricsCsv
            };
        }

        if (request.Resilience is { } resilience)
        {
            current.MaxConsecutiveFailures = resilience.MaxConsecutiveFailures;
            current.ConsecutiveFailuresTokenTolerance = resilience.ConsecutiveFailuresTokenTolerance;
        }

        if (request.Pricing is { } pricing)
        {
            current.Pricing = new PricingOptions
            {
                PromptPricePerMillion = pricing.PromptPricePerMillion,
                CompletionPricePerMillion = pricing.CompletionPricePerMillion,
                PricePerMillion = pricing.PricePerMillion,
                Currency = pricing.Currency,
                CachedPromptPricePerMillion = pricing.CachedPromptPricePerMillion,
                // Task #84: copied verbatim (null = cleared = the effective defaults 0.20 /
                // 150 W apply at read time) — same full-section-replace pattern as the rest.
                ElectricityPricePerKwh = pricing.ElectricityPricePerKwh,
                DefaultNodePowerWatts = pricing.DefaultNodePowerWatts
            };
        }

        current.Normalize();
        _logger?.LogInformation(
            "OllamaRouter: applied router configuration sections ({Sections}) to the live configuration (persistence: {Path}).",
            string.Join(", ", new[]
            {
                request.Cloud is null ? null : "cloud",
                request.DynamicContext is null ? null : "dynamicContext",
                request.Recording is null ? null : "recording",
                request.Resilience is null ? null : "resilience",
                request.Pricing is null ? null : "pricing"
            }.Where(s => s is not null)),
            ConfigFilePath);
    }

    /// <summary>
    /// Best-effort <see cref="IConfigurationRoot.Reload"/> after a successful write + in-memory
    /// apply: forces the configuration-bound providers to re-read the file (reconciles the
    /// configuration-bound options monitor with the disk content; the JsonConfigurationSource
    /// file watcher covers it independently). No-op / swallowed in hosts whose registered
    /// <see cref="IConfiguration"/> is not a reloadable root (bare test hosts).
    /// </summary>
    private void ReloadConfigurationBestEffort()
    {
        try
        {
            // The host-registered IConfiguration is a ConfigurationRoot (ConfigurationManager in
            // ASP.NET Core hosts), which carries Reload(); a bare ServiceCollection host may
            // register a non-root configuration, in which case this is a no-op.
            if (_serviceProvider.GetService<IConfiguration>() is IConfigurationRoot root)
            {
                root.Reload();
            }
        }
        catch
        {
            // Reload is best-effort: the in-memory apply already made the change visible to every
            // live consumer; a reload hiccup must never surface as a failed mutation.
        }
    }

    private RouterConfigPersistResult Failure(string message)
    {
        _logger?.LogError("OllamaRouter configuration persistence failed ({Path}): {Error}", ConfigFilePath, message);
        return new RouterConfigPersistResult(false, message);
    }
}
