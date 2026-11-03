using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;

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

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <inheritdoc />
    public string ConfigFilePath { get; }

    /// <summary>
    /// Creates the persistence service. The host environment is resolved DEFENSIVELY from
    /// <paramref name="serviceProvider"/> (a bare test ServiceCollection host without one falls
    /// back to the assembly base directory).
    /// </summary>
    /// <param name="options">The live options monitor (the shared configuration-bound instance in production).</param>
    /// <param name="serviceProvider">The application service provider (IHostEnvironment / IConfiguration resolution).</param>
    /// <param name="logger">Optional logger.</param>
    public RouterConfigPersistence(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;

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
                targetsArray.Add((JsonNode?)node);
            }

            var writeResult = await AtomicWriteAsync(root!, cancellationToken).ConfigureAwait(false);
            if (!writeResult.Success)
            {
                return writeResult;
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
                Currency = pricing.Currency
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
