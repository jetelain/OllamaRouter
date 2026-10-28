using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OllamaRouter.Configuration;
using OllamaRouter.Options;
using Xunit;

namespace OllamaRouter.Tests.Configuration;

/// <summary>
/// Unit + small host-based integration tests for the layered configuration sources:
/// read-only factory appsettings.json + optional user-owned config/config.json (fail-open,
/// reloadOnChange, anchor-positioned precedence).
/// </summary>
public class RouterConfigurationSourceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup (file watcher may hold the directory briefly).
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// Creates an isolated temp content root containing a factory appsettings.json with a small
    /// documented OllamaRouter section, and returns (root, absolute path of config/config.json).
    /// </summary>
    private (string Root, string UserConfigPath) CreateContentRoot(string? userConfigJson = null, string? factoryJson = null)
    {
        var dir = Directory.CreateTempSubdirectory("router-cfg-test-").FullName;
        _tempDirs.Add(dir);
        File.WriteAllText(
            Path.Combine(dir, "appsettings.json"),
            factoryJson ??
            """
            {
              "OllamaRouter": {
                "BindAddress": "http://factory:11434/",
                "MaxConsecutiveFailures": 2,
                "ConsecutiveFailuresTokenTolerance": 4000,
                "TokenEstimationOverheadFactor": 1.025
              }
            }
            """);

        var userConfigPath = Path.Combine(dir, "config", "config.json");
        if (userConfigJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            File.WriteAllText(userConfigPath, userConfigJson);
        }

        return (dir, userConfigPath);
    }

    private static IConfigurationBuilder CreateFactoryBuilder(string contentRoot) =>
        new ConfigurationBuilder().SetBasePath(contentRoot).AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    // ------------------------------------------------------------------ Factory-only boot

    [Fact]
    public void AddUserConfiguration_NoConfigDirectory_ReturnsAbsentAndRegistersNoProvider()
    {
        var (root, _) = CreateContentRoot(userConfigJson: null);
        var logger = new CapturingLogger();

        var status = RouterConfigurationSources.AddUserConfiguration(
            CreateFactoryBuilder(root), root, "Production", logger);

        Assert.Equal(UserConfigLoadStatus.Absent, status);
    }

    [Fact]
    public void AddUserConfiguration_NoConfigDirectory_BindsFactoryValuesUnchanged()
    {
        var (root, _) = CreateContentRoot(userConfigJson: null);
        var builder = CreateFactoryBuilder(root);

        RouterConfigurationSources.AddUserConfiguration(builder, root, environment: null);

        var options = builder.Build().GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>()!;
        Assert.Equal("http://factory:11434/", options.BindAddress);
        Assert.Equal(2, options.MaxConsecutiveFailures);
        Assert.Equal(4000, options.ConsecutiveFailuresTokenTolerance);
        Assert.Equal(1.025, options.TokenEstimationOverheadFactor, 3);
    }

    // ------------------------------------------------------------------ User override

    [Fact]
    public void AddUserConfiguration_WithUserFile_ReturnsLoadedAndOverridesFactoryDefaults()
    {
        var (root, _) = CreateContentRoot(
            userConfigJson: """
            {
              "OllamaRouter": {
                "BindAddress": "http://user:12345/",
                "MaxConsecutiveFailures": 7,
                "ConsecutiveFailuresTokenTolerance": 500
              }
            }
            """);
        var logger = new CapturingLogger();
        var builder = CreateFactoryBuilder(root);

        var status = RouterConfigurationSources.AddUserConfiguration(builder, root, environment: null, logger);

        Assert.Equal(UserConfigLoadStatus.Loaded, status);
        var options = builder.Build().GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>()!;

        // User values win over factory defaults.
        Assert.Equal("http://user:12345/", options.BindAddress);
        Assert.Equal(7, options.MaxConsecutiveFailures);
        Assert.Equal(500, options.ConsecutiveFailuresTokenTolerance);
        // Keys the user did not override keep their factory values.
        Assert.Equal(1.025, options.TokenEstimationOverheadFactor, 3);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Loaded user configuration"));
    }

    [Fact]
    public void AddUserConfiguration_RegistersReloadableJsonProviderForUserFile()
    {
        var (root, _) = CreateContentRoot(userConfigJson: """{ "OllamaRouter": { "MaxConsecutiveFailures": 3 } }""");
        var builder = CreateFactoryBuilder(root);

        RouterConfigurationSources.AddUserConfiguration(builder, root, environment: null);

        var source = Assert.Single(
            builder.Sources,
            s => s is JsonConfigurationSource { Path: var p } &&
                 string.Equals(p, RouterConfigurationSources.UserConfigRelativePath, StringComparison.OrdinalIgnoreCase));
        var jsonSource = (JsonConfigurationSource)source;
        Assert.True(jsonSource.ReloadOnChange);
        Assert.False(jsonSource.Optional);
    }

    // ------------------------------------------------------------------ Precedence

    [Fact]
    public void AddUserConfiguration_UserFileInsertedAfterAppsettingsAnchor_AndLaterSourcesStillWin()
    {
        // Simulate the real host source chain: appsettings.json + appsettings.Production.json.
        var (root, _) = CreateContentRoot(
            userConfigJson: """{ "OllamaRouter": { "BindAddress": "http://user:9999/" } }""",
            factoryJson: """
            {
              "OllamaRouter": { "BindAddress": "http://factory:11434/", "TokenEstimator": "Heuristic" }
            }
            """);
        File.WriteAllText(Path.Combine(root, "appsettings.Production.json"),
            """{ "OllamaRouter": { "BindAddress": "http://env:8888/" } }""");

        var builder = new ConfigurationBuilder()
            .SetBasePath(root)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);

        RouterConfigurationSources.AddUserConfiguration(builder, root, environment: "Production");

        // Anchor position: the user source sits immediately after appsettings.Production.json,
        // i.e. BEFORE any later-registered source (user secrets / env vars / CLI args).
        var sourcePaths = builder.Sources
            .Select(s => s is JsonConfigurationSource j ? j.Path : null)
            .Where(p => p is not null)
            .Select(p => Path.GetFileName(p)!)
            .ToList();
        var anchorIdx = sourcePaths.FindIndex(p => p.Equals("appsettings.Production.json", StringComparison.OrdinalIgnoreCase));
        var userIdx = sourcePaths.FindIndex(p => p.Equals("config.json", StringComparison.OrdinalIgnoreCase));
        Assert.True(anchorIdx >= 0, "environment anchor source must be present");
        Assert.Equal(anchorIdx + 1, userIdx);

        // Later-registered source (simulating env vars / CLI args) still wins over the user file.
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OllamaRouter:BindAddress"] = "http://cli:7777/"
        });

        var options = builder.Build().GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>()!;
        Assert.Equal("http://cli:7777/", options.BindAddress);
        Assert.Equal("Heuristic", options.TokenEstimator); // untouched by user/env → factory value
    }

    [Fact]
    public void AddUserConfiguration_NoAppsettingsAnchor_LeavesUserSourceAppendedLast()
    {
        var dir = Directory.CreateTempSubdirectory("router-cfg-test-").FullName;
        _tempDirs.Add(dir); // no appsettings.json at all
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "config.json"),
            """{ "OllamaRouter": { "MaxConsecutiveFailures": 5 } }""");

        var builder = new ConfigurationBuilder().SetBasePath(dir); // mirrors a real host (base path = content root)
        var status = RouterConfigurationSources.AddUserConfiguration(builder, dir, environment: null);

        Assert.Equal(UserConfigLoadStatus.Loaded, status);
        Assert.Same(
            builder.Sources[^1],
            builder.Sources.First(s => s is JsonConfigurationSource { Path: var p } &&
                                       string.Equals(p, RouterConfigurationSources.UserConfigRelativePath, StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------------ Corrupt file (fail-open)

    [Fact]
    public void AddUserConfiguration_InvalidJson_ReturnsInvalidJsonRegistersNoProviderAndDoesNotThrow()
    {
        var (root, _) = CreateContentRoot(userConfigJson: "{ \"OllamaRouter\": { \"MaxConsecutiveFailures\": broken");
        var logger = new CapturingLogger();
        var builder = CreateFactoryBuilder(root);

        // Must not throw (fail-open): the corrupt file is simply ignored.
        var status = RouterConfigurationSources.AddUserConfiguration(builder, root, environment: null, logger);

        Assert.Equal(UserConfigLoadStatus.InvalidJson, status);
        Assert.DoesNotContain(
            builder.Sources,
            s => s is JsonConfigurationSource { Path: var p } &&
                 string.Equals(p, RouterConfigurationSources.UserConfigRelativePath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("invalid JSON"));

        // Fail-open: factory defaults are still in effect.
        var options = builder.Build().GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>()!;
        Assert.Equal(2, options.MaxConsecutiveFailures);
        Assert.Equal("http://factory:11434/", options.BindAddress);
    }

    [Fact]
    public void AddUserConfiguration_InvalidJson_CommentsAndTrailingCommas_AreTolerated()
    {
        var (root, _) = CreateContentRoot(
            userConfigJson: """
            // a comment
            {
              "OllamaRouter": { "MaxConsecutiveFailures": 4, },
            }
            """);

        var builder = CreateFactoryBuilder(root);
        var status = RouterConfigurationSources.AddUserConfiguration(builder, root, environment: null);

        Assert.Equal(UserConfigLoadStatus.Loaded, status);
        var options = builder.Build().GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>()!;
        Assert.Equal(4, options.MaxConsecutiveFailures);
    }

    // ------------------------------------------------------------------ Idempotency

    [Fact]
    public void AddUserConfiguration_CalledTwice_RegistersProviderOnce()
    {
        var (root, _) = CreateContentRoot(userConfigJson: """{ "OllamaRouter": { "MaxConsecutiveFailures": 3 } }""");
        var builder = CreateFactoryBuilder(root);

        var first = RouterConfigurationSources.AddUserConfiguration(builder, root, environment: null);
        var second = RouterConfigurationSources.AddUserConfiguration(builder, root, environment: null);

        Assert.Equal(UserConfigLoadStatus.Loaded, first);
        Assert.Equal(UserConfigLoadStatus.Loaded, second);
        Assert.Single(
            builder.Sources,
            s => s is JsonConfigurationSource { Path: var p } &&
                 string.Equals(p, RouterConfigurationSources.UserConfigRelativePath, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ Path helpers

    [Fact]
    public void GetFullPath_CombinesContentRootWithConfigDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "some", "root");
        Assert.Equal(Path.Combine(root, "config", "config.json"), RouterConfigurationSources.GetFullPath(root));
        Assert.Equal(Path.Combine("config", "config.json"), RouterConfigurationSources.UserConfigRelativePath);
    }

    [Fact]
    public void AddUserConfiguration_NullArguments_Throw()
    {
        var (root, _) = CreateContentRoot();
        Assert.Throws<ArgumentNullException>(() => RouterConfigurationSources.AddUserConfiguration(null!, root));
        Assert.Throws<ArgumentException>(() => RouterConfigurationSources.AddUserConfiguration(new ConfigurationBuilder(), ""));
    }

    // ------------------------------------------------------------------ Reload (host-based)

    [Fact]
    public async Task Host_WithUserFile_ReloadOnChangePropagatesFileEdits()
    {
        var (root, userConfigPath) = CreateContentRoot(
            userConfigJson: """{ "OllamaRouter": { "TokenEstimationOverheadFactor": 1.1 } }""");

        var reloadValue = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var host = new HostBuilder()
            .UseContentRoot(root)
            .ConfigureAppConfiguration((_, config) =>
            {
                // Mirror a real Web host: the configuration base path is the content root, so
                // relative source paths (appsettings.json, config/config.json) resolve next to
                // the executable / content root.
                config.SetBasePath(root);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                RouterConfigurationSources.AddUserConfiguration(config, root, environment: null);
            })
            .Build();

        var config = host.Services.GetRequiredService<IConfiguration>();
        using var registration = ChangeToken.OnChange(
            () => config.GetReloadToken(),
            () =>
            {
                // The provider may rebuild asynchronously after the token fires: poll briefly
                // (bounded by the WaitAsync cap below) until the new value is visible.
                var poll = Task.Run(async () =>
                {
                    for (var i = 0; i < 50 && config["OllamaRouter:TokenEstimationOverheadFactor"] != "1.25"; i++)
                    {
                        await Task.Delay(200);
                    }

                    reloadValue.TrySetResult(config["OllamaRouter:TokenEstimationOverheadFactor"] ?? string.Empty);
                });
            });

        Assert.Equal("1.1", config["OllamaRouter:TokenEstimationOverheadFactor"]);

        File.WriteAllText(userConfigPath, """{ "OllamaRouter": { "TokenEstimationOverheadFactor": 1.25 } }""");

        var newFactor = await reloadValue.Task.WaitAsync(TimeSpan.FromSeconds(10))
            ?? throw new Xunit.Sdk.XunitException("reloadOnChange change token did not fire within 10 s");
        Assert.Equal("1.25", newFactor);
    }

    [Fact]
    public async Task Host_WithCorruptUserFile_StillStartsAndBindsFactoryDefaults()
    {
        var (root, _) = CreateContentRoot(userConfigJson: "{ not valid json !");

        var host = new HostBuilder()
            .UseContentRoot(root)
            .ConfigureAppConfiguration((_, config) =>
            {
                config.SetBasePath(root);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                Assert.Equal(UserConfigLoadStatus.InvalidJson,
                    RouterConfigurationSources.AddUserConfiguration(config, root, environment: null));
            })
            .Build();

        await host.StartAsync();

        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("http://factory:11434/", config["OllamaRouter:BindAddress"]);
        Assert.Equal("2", config["OllamaRouter:MaxConsecutiveFailures"]);

        await host.StopAsync();
    }
}
