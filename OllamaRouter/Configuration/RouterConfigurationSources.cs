using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;

namespace OllamaRouter.Configuration;

/// <summary>
/// Load status of the optional user-owned configuration layer
/// (<c>config/config.json</c> next to the executable / content root).
/// </summary>
internal enum UserConfigLoadStatus
{
    /// <summary>
    /// The user configuration file does not exist. This is a valid, silent state:
    /// factory defaults from <c>appsettings.json</c> are in effect and no provider is registered.
    /// </summary>
    Absent = 0,

    /// <summary>
    /// The user configuration file was loaded and its provider registered (or was already
    /// registered, in which case registration is a no-op).
    /// </summary>
    Loaded = 1,

    /// <summary>
    /// The user configuration file exists but contains invalid JSON. The file was ignored
    /// (fail-open): no provider is registered, factory defaults are in effect, and no
    /// exception escapes to the host.
    /// </summary>
    InvalidJson = 2
}

/// <summary>
/// Wires the optional, user-owned configuration layer (<c>config/config.json</c> in the content
/// root, i.e. next to the executable) on top of the read-only factory <c>appsettings.json</c>
/// defaults.
/// <para>
/// Fail-open by design: the user file is pre-validated with the very same parser as the JSON
/// configuration provider (<see cref="JsonConfigurationProvider"/>, via
/// <see cref="JsonConfigurationExtensions.AddJsonStream(IConfigurationBuilder, Stream)"/>)
/// <b>before</b> its <see cref="JsonConfigurationSource" /> is registered. Without this guard, an
/// invalid user file would make the provider throw out of
/// <c>ConfigurationBuilder.Build()</c> and brick startup. An invalid file is therefore simply
/// ignored with a prominent error; a botched hand-edit or a truncated write must never take the
/// router down. Invalid edits made while the application is running are logged and the
/// reload failure is swallowed (see <see cref="JsonConfigurationSource.OnLoadException"/>).
/// </para>
/// <para>
/// Effective precedence after wiring:
/// <c>appsettings.json</c> &lt; <c>config/config.json</c> &lt; user secrets &lt; environment
/// variables &lt; command-line arguments (the user source is repositioned immediately after the
/// <c>appsettings.{Environment}.json</c> anchor; if no such anchor exists, e.g. in unit tests
/// or non-Web hosts, it is left appended last).
/// </para>
/// </summary>
internal static class RouterConfigurationSources
{
    private const string UserConfigDirectoryName = "config";
    private const string UserConfigFileName = "config.json";
    private const string AppsettingsFileName = "appsettings.json";
    private const string AppsettingsEnvironmentFileNameFormat = "appsettings.{0}.json";

    private const string PathPlaceholder = "{Path}";
    private const string InvalidJsonMessageTemplate =
        "OllamaRouter user configuration file {Path} contains invalid JSON and was IGNORED (fail-open): " +
        "factory defaults from appsettings.json are in effect. Fix or delete the file to apply user settings.";
    private const string ReloadFailedMessageTemplate =
        "OllamaRouter user configuration file {Path} could not be reloaded; " +
        "previous valid settings remain in effect until the file is fixed.";
    private const string AbsentMessageTemplate =
        "User configuration file {Path} not found; factory defaults from appsettings.json are in effect.";
    private const string LoadedMessageTemplate =
        "Loaded user configuration from {Path}; user values override factory appsettings.json defaults.";

    /// <summary>
    /// Relative path of the user configuration file inside the content root
    /// (<c>config/config.json</c>), using OS-specific directory separators.
    /// </summary>
    internal static readonly string UserConfigRelativePath = Path.Combine(UserConfigDirectoryName, UserConfigFileName);

    /// <summary>
    /// Returns the absolute path of the user configuration file for a given content root.
    /// </summary>
    public static string GetFullPath(string contentRootPath) => Path.Combine(contentRootPath, UserConfigRelativePath);

    /// <summary>
    /// Registers the optional user configuration layer on the given configuration builder,
    /// positioning its provider immediately after the <c>appsettings.{environment}.json</c>
    /// source (or after <c>appsettings.json</c> when <paramref name="environment" /> is null).
    /// </summary>
    /// <param name="configuration">The configuration builder to extend (mutated in place).</param>
    /// <param name="contentRootPath">The content root (next to the executable in production).</param>
    /// <param name="environment">
    /// Optional environment name used to locate the <c>appsettings.{environment}.json</c> anchor
    /// source; null/empty falls back to <c>appsettings.json</c>.
    /// </param>
    /// <param name="logger">
    /// Optional logger. When supplied, <c>Loaded</c>/<c>Absent</c>/<c>InvalidJson</c> outcomes
    /// are logged; when null, only failures are surfaced (via
    /// <see cref="Console.Error" />), because pre-<c>Build()</c> there is no application logger yet.
    /// </param>
    /// <param name="loggerAccessor">
    /// Optional deferred logger accessor used during hot-reload callbacks when an application logger
    /// becomes available after host startup.
    /// </param>
    /// <returns>The <see cref="UserConfigLoadStatus" /> of the user configuration file.</returns>
    public static UserConfigLoadStatus AddUserConfiguration(
        IConfigurationBuilder configuration,
        string contentRootPath,
        string? environment = null,
        ILogger? logger = null,
        Func<ILogger?>? loggerAccessor = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(contentRootPath);

        var fullPath = GetFullPath(contentRootPath);

        // Absent user file: valid, silent state. Never register a provider for a missing file.
        if (!File.Exists(fullPath))
        {
            logger?.LogInformation(AbsentMessageTemplate, fullPath);
            return UserConfigLoadStatus.Absent;
        }

        // Idempotency: a source for the same relative path (case-insensitive) was already registered.
        if (configuration.Sources.Any(s => s is JsonConfigurationSource { Path: var p } &&
            string.Equals(p, UserConfigRelativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return UserConfigLoadStatus.Loaded;
        }

        // Fail-open pre-validation: an invalid user file must never throw out of Build().
        if (!IsLoadable(fullPath))
        {
            ReportFailure(InvalidJsonMessageTemplate, fullPath, logger, loggerAccessor);
            return UserConfigLoadStatus.InvalidJson;
        }

        // Register with a path relative to the builder's base path — the same semantics as
        // AddJsonFile("appsettings.json", ...): in a Web host the base path is the content root,
        // so the provider resolves "config/config.json" next to the executable. (Unit tests
        // must therefore SetBasePath to their isolated content root, as Web hosts do.)
        var source = new JsonConfigurationSource
        {
            Path = UserConfigRelativePath,
            Optional = false,
            ReloadOnChange = true,
            // An invalid hot edit must not crash the process, but must never be silent either:
            // the provider retains its previous valid data on a failed reload, and we log the error prominently.
            OnLoadException = context =>
            {
                context.Ignore = true;
                ReportFailure(ReloadFailedMessageTemplate, fullPath, logger, loggerAccessor, context.Exception);
            }
        };
        AddOrInsertAfterAppsettingsAnchor(configuration, source, environment);

        logger?.LogInformation(LoadedMessageTemplate, fullPath);
        return UserConfigLoadStatus.Loaded;
    }

    /// <summary>
    /// Parses the file with the exact parser used by <see cref="JsonConfigurationProvider"/> so that
    /// anything accepted here can safely be handed to <c>Build()</c> (including non-object roots and
    /// case-insensitive duplicate keys, which plain <see cref="JsonDocument"/> parsing would accept).
    /// </summary>
    private static bool IsLoadable(string fullPath)
    {
        try
        {
            using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            new ConfigurationBuilder().AddJsonStream(fileStream).Build();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReportFailure(
        string messageTemplate,
        string fullPath,
        ILogger? logger,
        Func<ILogger?>? loggerAccessor = null,
        Exception? exception = null)
    {
        var effectiveLogger = loggerAccessor?.Invoke() ?? logger;
        if (effectiveLogger is not null)
        {
            // The templates are compile-time constants.
#pragma warning disable CA2254
            effectiveLogger.LogError(exception, messageTemplate, fullPath);
#pragma warning restore CA2254
            return;
        }

        // No application logger available (pre-Build() startup path, or Serilog not yet configured):
        // surface prominently on stderr so a corrupt user file is always visible.
        Console.Error.WriteLine(messageTemplate.Replace(PathPlaceholder, fullPath));
        if (exception is not null)
        {
            Console.Error.WriteLine(exception.Message);
        }
    }

    private static void AddOrInsertAfterAppsettingsAnchor(
        IConfigurationBuilder configuration,
        IConfigurationSource source,
        string? environment)
    {
        var sources = configuration.Sources;
        // Prefer the environment-specific file (loaded after the base file); fall back to the base file.
        string[] anchorNames = string.IsNullOrEmpty(environment)
            ? [AppsettingsFileName]
            : [string.Format(AppsettingsEnvironmentFileNameFormat, environment), AppsettingsFileName];

        foreach (var anchorName in anchorNames)
        {
            for (var i = 0; i < sources.Count; i++)
            {
                if (sources[i] is JsonConfigurationSource { Path: not null } jsonSource &&
                    string.Equals(Path.GetFileName(jsonSource.Path), anchorName, StringComparison.OrdinalIgnoreCase))
                {
                    sources.Insert(i + 1, source);
                    return;
                }
            }
        }

        // No anchor found (non-Web hosts / unit tests): append the user source last.
        sources.Add(source);
    }
}
