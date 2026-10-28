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
/// Fail-open by design: the user file is pre-validated with <see cref="JsonDocument.Parse"/>
/// <b>before</b> its <see cref="JsonConfigurationSource" /> is registered. Without this guard, an
/// invalid user file would make <see cref="JsonConfigurationProvider" /> throw out of
/// <c>ConfigurationBuilder.Build()</c> and brick startup. An invalid file is therefore simply
/// ignored with a prominent error; a botched hand-edit or a truncated write must never take the
/// router down.
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
    /// <summary>
    /// Relative path of the user configuration file inside the content root
    /// (<c>config/config.json</c>), using OS-specific directory separators.
    /// </summary>
    internal static readonly string UserConfigRelativePath = Path.Combine("config", "config.json");

    /// <summary>
    /// Returns the absolute path of the user configuration file for a given content root.
    /// </summary>
    public static string GetFullPath(string contentRootPath) => Path.Combine(contentRootPath, "config", "config.json");

    /// <summary>
    /// Registers the optional user configuration layer on the given configuration builder,
    /// repositioning its provider immediately after the <c>appsettings.{environment}.json</c>
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
    /// are logged; when null, only the <c>InvalidJson</c> case is surfaced (via
    /// <see cref="Console.Error" />), because pre-<c>Build()</c> there is no application logger yet.
    /// </param>
    /// <returns>The <see cref="UserConfigLoadStatus" /> of the user configuration file.</returns>
    public static UserConfigLoadStatus AddUserConfiguration(
        IConfigurationBuilder configuration,
        string contentRootPath,
        string? environment = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(contentRootPath);

        var fullPath = GetFullPath(contentRootPath);

        // Absent user file: valid, silent state. Never register a provider for a missing file.
        if (!File.Exists(fullPath))
        {
            logger?.LogInformation(
                "User configuration file {Path} not found; factory defaults from appsettings.json are in effect.", fullPath);
            return UserConfigLoadStatus.Absent;
        }

        // Fail-open pre-validation: an invalid user file must never throw out of
        // ConfigurationBuilder.Build(). Use the same lenient JSONC options as the JSON
        // configuration provider (comments + trailing commas tolerated).
        try
        {
            using var _ = JsonDocument.Parse(
                File.ReadAllText(fullPath),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            ReportInvalidJson(fullPath, logger);
            return UserConfigLoadStatus.InvalidJson;
        }

        // Idempotency: a source for the same relative path (case-insensitive) was already registered.
        if (configuration.Sources.Any(s => s is JsonConfigurationSource { Path: var p } &&
            string.Equals(p, UserConfigRelativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return UserConfigLoadStatus.Loaded;
        }

        // Register with a path relative to the builder's base path — the same semantics as
        // AddJsonFile("appsettings.json", ...): in a Web host the base path is the content root,
        // so the provider resolves "config/config.json" next to the executable. (Unit tests
        // must therefore SetBasePath to their isolated content root, as Web hosts do.)
        configuration.Sources.Add(new JsonConfigurationSource
        {
            Path = UserConfigRelativePath,
            Optional = false,
            ReloadOnChange = true
        });
        InsertAfterAppsettingsAnchor(configuration, UserConfigRelativePath, environment);

        logger?.LogInformation(
            "Loaded user configuration from {Path}; user values override factory appsettings.json defaults.", fullPath);
        return UserConfigLoadStatus.Loaded;
    }

    private static void ReportInvalidJson(string fullPath, ILogger? logger)
    {
        const string message =
            "OllamaRouter user configuration file {Path} contains invalid JSON and was IGNORED (fail-open): " +
            "factory defaults from appsettings.json are in effect. Fix or delete the file to apply user settings.";
        if (logger is not null)
        {
            logger.LogError(message, fullPath);
            return;
        }

        // Pre-Build() startup path: no application logger exists yet — surface prominently on
        // stderr (before Serilog takes over) so a corrupt user file is always visible.
        Console.Error.WriteLine(message.Replace("{Path}", fullPath));
    }

    private static void InsertAfterAppsettingsAnchor(IConfigurationBuilder configuration, string userConfigPath, string? environment)
    {
        var sources = configuration.Sources;
        // Prefer the environment-specific file (loaded after the base file); fall back to the base file.
        var anchorNames = string.IsNullOrEmpty(environment)
            ? new[] { "appsettings.json" }
            : new[] { $"appsettings.{environment}.json", "appsettings.json" };

        foreach (var anchorName in anchorNames)
        {
            var anchorIndex = -1;
            for (var i = 0; i < sources.Count; i++)
            {
                if (!(sources[i] is JsonConfigurationSource jsonSource))
                {
                    continue;
                }

                if (string.Equals(jsonSource.Path, userConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // our own source
                }

                if (jsonSource.Path != null && jsonSource.Path.EndsWith(anchorName, StringComparison.OrdinalIgnoreCase))
                {
                    anchorIndex = i;
                    break;
                }
            }

            if (anchorIndex < 0)
            {
                continue;
            }

            // No anchor / already in final position: leave appended last.
            if (anchorIndex != sources.Count - 1)
            {
                var newSource = sources[sources.Count - 1];
                sources.RemoveAt(sources.Count - 1);
                sources.Insert(anchorIndex + 1, newSource);
            }

            return;
        }

        // No anchor found (non-Web hosts / unit tests): leave the user source appended last.
    }
}
