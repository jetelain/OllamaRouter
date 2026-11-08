using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OllamaRouter.Options;

namespace OllamaRouter.Tests.Options;

public class OllamaRouterOptionsTests
{
    [Fact]
    public void GetEffectiveTargets_ConfiguredFleet_PassesThroughInOrderAndSkipsInvalidEntries()
    {
        var options = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435",
            RemoteUrl = "http://aiserver.local:11434",
            Targets =
            [
                new OllamaTargetOptions { Name = "desktop", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions { Name = null, Url = "http://192.168.1.30:11434" },
                new OllamaTargetOptions { Name = "laptop", Url = "  " },
                new OllamaTargetOptions { Name = "server1", Url = "http://aiserver.local:11434" }
            ]
        };

        var effective = options.GetEffectiveTargets();

        Assert.Equal(["desktop", "server1"], effective.Select(t => t.Name).ToList());
        // Configured targets win over the legacy URLs.
        Assert.DoesNotContain(effective, t => t.Name == "Local");
        Assert.DoesNotContain(effective, t => t.Name == "Remote");
    }

    [Fact]
    public void GetEffectiveTargets_EmptyTargetsListWithNoUrls_ReturnsEmpty()
    {
        var options = new OllamaRouterOptions();

        Assert.Empty(options.GetEffectiveTargets());
    }

    [Fact]
    public void GetEffectiveTargets_LegacyConfig_SynthesizesLocalAndRemoteOnly()
    {
        var options = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435",
            RemoteUrl = "http://aiserver.local:11434"
        };

        var effective = options.GetEffectiveTargets();

        Assert.Equal(["Local", "Remote"], effective.Select(t => t.Name).ToList());
        foreach (var target in effective)
        {
            Assert.Null(target.MaxVramMB);
            Assert.Equal(1, target.MaxParallel);
            Assert.True(target.Enabled);
        }
        Assert.Equal(options.LocalUrl, effective[0].Url);
        Assert.Equal(options.RemoteUrl, effective[1].Url);
    }

    [Fact]
    public void GetEffectiveTargets_LegacyConfigWithOnlyLocalUrl_SynthesizesLocalOnly()
    {
        var options = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435"
        };

        var effective = options.GetEffectiveTargets();

        Assert.Single(effective);
        Assert.Equal("Local", effective[0].Name);
        Assert.Equal("http://127.0.0.1:11435", effective[0].Url);
    }

    [Fact]
    public void GetEffectiveTargets_LegacyConfigWithOnlyRemoteUrl_SynthesizesRemoteOnly()
    {
        var options = new OllamaRouterOptions
        {
            RemoteUrl = "http://aiserver.local:11434"
        };

        var effective = options.GetEffectiveTargets();

        Assert.Single(effective);
        Assert.Equal("Remote", effective[0].Name);
    }

    [Fact]
    public void GetEffectiveMaxVramMB_ExplicitMaxVramMB_ReturnsConfiguredValue()
    {
        var target = new OllamaTargetOptions
        {
            MaxVramMB = 12288
        };

        Assert.Equal(12288, target.GetEffectiveMaxVramMB());
    }

    [Fact]
    public void GetEffectiveMaxVramMB_MissingMaxVramMB_DelegatesToFleetHardware()
    {
        var target = new OllamaTargetOptions { Name = "NodeA", Url = "http://x" };
        var mockFleet = new Moq.Mock<OllamaRouter.Services.Hardware.IFleetHardwareService>();
        mockFleet.Setup(f => f.GetEffectiveMaxVramMB(target)).Returns(24576);

        Assert.Equal(24576, target.GetEffectiveMaxVramMB(mockFleet.Object));
    }

    [Fact]
    public void GetEffectiveMaxVramMB_MissingMaxVramMBAndNoFleetHardware_ReturnsNull()
    {
        var target = new OllamaTargetOptions { Name = "NodeA", Url = "http://x" };
        Assert.Null(target.GetEffectiveMaxVramMB(null));
    }

    [Fact]
    public void GetEffectiveTargets_InvalidEntries_LogOneWarningPerSkippedEntry()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "desktop", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions { Name = null, Url = "http://192.168.1.30:11434" },
                new OllamaTargetOptions { Name = "laptop", Url = "" }
            ]
        };

        var warnings = new List<string>();
        var logger = new CapturingLogger(warnings);

        var effective = options.GetEffectiveTargets(logger);

        Assert.Equal(["desktop"], effective.Select(t => t.Name).ToList());
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, message => Assert.Contains("skipped", message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StateDirectory_DefaultsToNull()
    {
        var options = new OllamaRouterOptions();
        Assert.Null(options.StateDirectory);
    }

    [Fact]
    public void ContextFitMarginTokens_DefaultsTo8192()
    {
        var options = new OllamaRouterOptions();
        Assert.Equal(8192, options.ContextFitMarginTokens);
    }

    [Fact]
    public void Normalize_NegativeContextFitMarginTokens_ClampedToZero()
    {
        var options = new OllamaRouterOptions { ContextFitMarginTokens = -1 };

        options.Normalize();

        Assert.Equal(0, options.ContextFitMarginTokens);
    }

    [Fact]
    public void Normalize_NonNegativeContextFitMarginTokens_Unchanged()
    {
        var options = new OllamaRouterOptions { ContextFitMarginTokens = 10_240 };

        options.Normalize();

        Assert.Equal(10_240, options.ContextFitMarginTokens);
    }

    [Fact]
    public void SessionReservationOptions_Defaults()
    {
        var options = new OllamaRouterOptions();

        Assert.Equal(OllamaRouterOptions.DefaultSessionReservationWindowSeconds, options.SessionReservationWindowSeconds);
        Assert.Equal(30, options.SessionReservationWindowSeconds);
        Assert.Equal(120, options.OtherSessionReservationPenalty);
        Assert.Equal(220, options.MaxParallelCliffPenalty);
    }

    [Fact]
    public void Normalize_NegativeSessionReservationOptions_ResetToDefaults()
    {
        // Task #77: negative reservation-window / contention / cliff values are invalid and
        // reset to their defaults on normalize (re-applied on every config reload).
        var options = new OllamaRouterOptions
        {
            SessionReservationWindowSeconds = -5,
            OtherSessionReservationPenalty = -1,
            MaxParallelCliffPenalty = -99
        };

        options.Normalize();

        Assert.Equal(OllamaRouterOptions.DefaultSessionReservationWindowSeconds, options.SessionReservationWindowSeconds);
        Assert.Equal(120, options.OtherSessionReservationPenalty);
        Assert.Equal(220, options.MaxParallelCliffPenalty);
    }

    [Fact]
    public void Normalize_ZeroSessionReservationOptions_StayZero_Disabled()
    {
        // 0 is a meaningful opt-out (the reservation window / contention / cliff terms are
        // disabled) and must survive normalization; positive custom values are kept too.
        var options = new OllamaRouterOptions
        {
            SessionReservationWindowSeconds = 0,
            OtherSessionReservationPenalty = 0,
            MaxParallelCliffPenalty = 0
        };

        options.Normalize();

        Assert.Equal(0, options.SessionReservationWindowSeconds);
        Assert.Equal(0, options.OtherSessionReservationPenalty);
        Assert.Equal(0, options.MaxParallelCliffPenalty);

        var custom = new OllamaRouterOptions
        {
            SessionReservationWindowSeconds = 15,
            OtherSessionReservationPenalty = 80,
            MaxParallelCliffPenalty = 250
        };

        custom.Normalize();

        Assert.Equal(15, custom.SessionReservationWindowSeconds);
        Assert.Equal(80, custom.OtherSessionReservationPenalty);
        Assert.Equal(250, custom.MaxParallelCliffPenalty);
    }

    [Fact]
    public void ContextFitMarginTokens_BindsFromConfiguration()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OllamaRouter:ContextFitMarginTokens"] = "12345"
            })
            .Build();

        var options = configuration.GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>();

        Assert.NotNull(options);
        Assert.Equal(12345, options!.ContextFitMarginTokens);
    }

    [Fact]
    public void DispatchToLargestCapacityOnOverflow_DefaultsToTrue()
    {
        var options = new OllamaRouterOptions();
        Assert.True(options.DispatchToLargestCapacityOnOverflow);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void DispatchToLargestCapacityOnOverflow_BindsFromConfiguration(string configValue, bool expected)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OllamaRouter:DispatchToLargestCapacityOnOverflow"] = configValue
            })
            .Build();

        var options = configuration.GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>();

        Assert.NotNull(options);
        Assert.Equal(expected, options!.DispatchToLargestCapacityOnOverflow);
    }

    [Fact]
    public void SpeedFactor_DefaultsToOne()
    {
        Assert.Equal(1.0, new OllamaTargetOptions().SpeedFactor);
    }

    [Fact]
    public void SpeedFactor_BindsFromConfigurationPath()
    {
        // The .NET configuration binder accepts colon-free segment keys; the target list index
        // is the second segment (OllamaRouter:Targets:0:SpeedFactor).
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OllamaRouter:Targets:0:Name"] = "Local",
                ["OllamaRouter:Targets:0:Url"] = "http://127.0.0.1:11435",
                ["OllamaRouter:Targets:0:SpeedFactor"] = "2.0"
            })
            .Build();
        var options = new OllamaRouterOptions();
        config.Bind("OllamaRouter", options);

        var target = Assert.Single(options.Targets);
        Assert.Equal(2.0, target.SpeedFactor);
        Assert.Equal(2.0, target.GetEffectiveSpeedFactor());
    }

    [Fact]
    public void Type_DefaultsToOllama_OnNewAndBoundTargets()
    {
        // Task #61 (AC1/AC8): an omitted Type binds to Ollama — existing configs are
        // byte-identical in behavior.
        Assert.Equal(TargetType.Ollama, new OllamaTargetOptions().Type);
        Assert.Null(new OllamaTargetOptions().ApiKey);
        Assert.Null(new OllamaTargetOptions().Provider);
        Assert.Empty(new OllamaTargetOptions().Models);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OllamaRouter:Targets:0:Name"] = "legacy",
                ["OllamaRouter:Targets:0:Url"] = "http://127.0.0.1:11434"
            }).Build();
        var legacy = new OllamaRouterOptions();
        configuration.GetSection("OllamaRouter").Bind(legacy);

        var legacyTarget = legacy.GetEffectiveTargets().Single();
        Assert.Equal(TargetType.Ollama, legacyTarget.Type);
        Assert.Null(legacyTarget.ApiKey);
        Assert.Empty(legacyTarget.Models);
    }

    [Fact]
    public void HeterogeneousFields_BindFromConfiguration_IncludingModelsRoster()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OllamaRouter:Targets:0:Name"] = "provider",
                ["OllamaRouter:Targets:0:Url"] = "http://h:8000/v1",
                ["OllamaRouter:Targets:0:Type"] = "OpenAI",
                ["OllamaRouter:Targets:0:ApiKey"] = "sk-test",
                ["OllamaRouter:Targets:0:Provider"] = "openrouter",
                ["OllamaRouter:Targets:0:Models:0:Name"] = "openai/gpt-4o",
                ["OllamaRouter:Targets:0:Models:0:ContextLength"] = "128000",
                ["OllamaRouter:Targets:0:Models:0:Quantization"] = "bfloat16",
                ["OllamaRouter:Targets:0:Models:1:Name"] = "anthropic/claude-sonnet-4.5"
            }).Build();

        var options = new OllamaRouterOptions();
        configuration.GetSection("OllamaRouter").Bind(options);

        var target = options.GetEffectiveTargets().Single();
        Assert.Equal(TargetType.OpenAI, target.Type);
        Assert.Equal("sk-test", target.ApiKey);
        Assert.Equal("openrouter", target.Provider);
        Assert.Equal(2, target.Models.Count);
        Assert.Equal("openai/gpt-4o", target.Models[0].Name);
        Assert.Equal(128000, target.Models[0].ContextLength);
        Assert.Equal("bfloat16", target.Models[0].Quantization);
        Assert.Equal("anthropic/claude-sonnet-4.5", target.Models[1].Name);
        Assert.Null(target.Models[1].ContextLength);
    }

    [Fact]
    public void Type_BindsCaseInsensitively_FromConfiguredEnumNames()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OllamaRouter:Targets:0:Name"] = "a",
                ["OllamaRouter:Targets:0:Url"] = "http://a",
                ["OllamaRouter:Targets:0:Type"] = "openai",
                ["OllamaRouter:Targets:1:Name"] = "b",
                ["OllamaRouter:Targets:1:Url"] = "http://b",
                ["OllamaRouter:Targets:1:Type"] = "Osnative"
            }).Build();

        var options = new OllamaRouterOptions();
        configuration.GetSection("OllamaRouter").Bind(options);

        var targets = options.GetEffectiveTargets();
        Assert.Equal(TargetType.OpenAI, targets[0].Type);
        Assert.Equal(TargetType.OsNative, targets[1].Type);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GetEffectiveSpeedFactor_NonFiniteOrBelowBaseline_ClampsToOne(double factor)
    {
        var target = new OllamaTargetOptions { SpeedFactor = factor };
        Assert.Equal(1.0, target.GetEffectiveSpeedFactor());
    }

    [Theory]
    [InlineData(2.0, 2.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(3.75, 3.75)]
    public void GetEffectiveSpeedFactor_AboveBaseline_PassesThrough(double factor, double expected)
    {
        var target = new OllamaTargetOptions { SpeedFactor = factor };
        Assert.Equal(expected, target.GetEffectiveSpeedFactor());
    }

    [Fact]
    public void GetEffectiveTargets_LegacyConfig_SynthesizedTargetsKeepDefaultSpeedFactor()
    {
        var options = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435",
            RemoteUrl = "http://aiserver.local:11434"
        };

        var effective = options.GetEffectiveTargets();

        Assert.All(effective, t => Assert.Equal(1.0, t.GetEffectiveSpeedFactor()));
    }

    [Fact]
    public void AddOllamaRouter_UsesOptionsStateDirectory_WhenParameterIsNull()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var options = new OllamaRouterOptions { StateDirectory = @"C:\custom\state" };

        OllamaRouter.Extensions.OllamaRouterServiceCollectionExtensions.AddOllamaRouter(services, options, stateDirectory: null, enableTui: false);

        Assert.Equal(@"C:\custom\state", options.StateDirectory);
    }

    [Fact]
    public void AddOllamaRouter_ExplicitParameterOverridesOptionsStateDirectory()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var options = new OllamaRouterOptions { StateDirectory = @"C:\custom\state" };

        OllamaRouter.Extensions.OllamaRouterServiceCollectionExtensions.AddOllamaRouter(services, options, stateDirectory: @"C:\override\state", enableTui: false);

        Assert.Equal(@"C:\override\state", options.StateDirectory);
    }

    [Fact]
    public void AddOllamaRouter_ConfigOverload_BindsSection_NormalizesAndEnforcesNoTui()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OllamaRouter:MaxConsecutiveFailures"] = "6",
            ["OllamaRouter:ContextFitMarginTokens"] = "-1",
            ["OllamaRouter:DynamicContext:AutoShrinkIdleTimeoutSeconds"] = "-5"
        }).Build();

        OllamaRouter.Extensions.OllamaRouterServiceCollectionExtensions.AddOllamaRouter(
            services, configuration, stateDirectory: null, enableTui: false);

        using var provider = services.BuildServiceProvider();
        var value = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OllamaRouterOptions>>().CurrentValue;

        Assert.Equal(6, value.MaxConsecutiveFailures);
        Assert.Equal(0, value.ContextFitMarginTokens); // normalizer clamp on bind
        Assert.Equal(60, value.DynamicContext.AutoShrinkIdleTimeoutSeconds); // normalizer clamp on bind
        Assert.False(value.TerminalMonitoring.Enabled); // enableTui: false PostConfigure
    }

    // ------------------------------------------------------------------
    //  Preload load-call timeout (task #75)
    // ------------------------------------------------------------------

    [Fact]
    public void PreloadLoadTimeoutSeconds_DefaultsTo20()
    {
        var options = new OllamaRouterOptions();

        Assert.Equal(20, options.PreloadLoadTimeoutSeconds);
        Assert.Equal(20, OllamaRouterOptions.DefaultPreloadLoadTimeoutSeconds);
    }

    [Fact]
    public void Normalize_PreloadLoadTimeoutSeconds_NonPositiveValuesNormalizeToDefault_PositiveValuesKept()
    {
        var zero = new OllamaRouterOptions { PreloadLoadTimeoutSeconds = 0 };
        zero.Normalize();
        Assert.Equal(20, zero.PreloadLoadTimeoutSeconds);

        var negative = new OllamaRouterOptions { PreloadLoadTimeoutSeconds = -5 };
        negative.Normalize();
        Assert.Equal(20, negative.PreloadLoadTimeoutSeconds);

        var raised = new OllamaRouterOptions { PreloadLoadTimeoutSeconds = 60 };
        raised.Normalize();
        Assert.Equal(60, raised.PreloadLoadTimeoutSeconds); // AC5: raising it takes effect without code changes
    }

    [Fact]
    public void PreloadLoadTimeoutSeconds_BindsFromConfigJsonFile()
    {
        // The real config.json layer (AddJsonFile, the user-owned config/config.json shape):
        // the new top-level key binds like every other top-level option.
        var configDir = Path.Combine(Path.GetTempPath(), $"ollamarouter-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        try
        {
            File.WriteAllText(Path.Combine(configDir, "config.json"),
                """{ "OllamaRouter": { "PreloadLoadTimeoutSeconds": 42 } }""");

            var configuration = new ConfigurationBuilder()
                .SetBasePath(configDir)
                .AddJsonFile("config.json", optional: false, reloadOnChange: false)
                .Build();

            var options = configuration.GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>();

            Assert.NotNull(options);
            Assert.Equal(42, options!.PreloadLoadTimeoutSeconds);
        }
        finally
        {
            try { Directory.Delete(configDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void AddOllamaRouter_ConfigOverload_BindsPreloadLoadTimeoutSeconds_NormalizesNonPositiveOnBind()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OllamaRouter:PreloadLoadTimeoutSeconds"] = "0"
        }).Build();

        OllamaRouter.Extensions.OllamaRouterServiceCollectionExtensions.AddOllamaRouter(
            services, configuration, stateDirectory: null, enableTui: false);

        using var provider = services.BuildServiceProvider();
        var value = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OllamaRouterOptions>>().CurrentValue;

        Assert.Equal(20, value.PreloadLoadTimeoutSeconds); // normalizer clamp on bind
    }

    // ------------------------------------------------------------------
    //  Preload keep-alive resolver + normalization (task #44)
    // ------------------------------------------------------------------

    [Fact]
    public void GetEffectivePreloadKeepAliveSeconds_ExplicitOverride_WinsVerbatimIncludingMinusOneOnDesktop()
    {
        var options = new OllamaRouterOptions();
        var desktop = new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435", MaxVramMB = 16384, KeepAliveSeconds = -1 };
        var cloud = new OllamaTargetOptions { Name = "Cloud", Location = OllamaRouter.Services.Routing.TargetLocation.Cloud, KeepAliveSeconds = 300 };

        // Explicit values are honored verbatim (-1 on a desktop-class node stays -1, Open Q5).
        Assert.Equal(-1, desktop.GetEffectivePreloadKeepAliveSeconds(options));
        Assert.Equal(300, cloud.GetEffectivePreloadKeepAliveSeconds(options));
    }

    [Fact]
    public void GetEffectivePreloadKeepAliveSeconds_CloudClassWithoutOverride_ResolvesToIndefinite()
    {
        var options = new OllamaRouterOptions();
        var cloud = new OllamaTargetOptions { Name = "Cloud", Location = OllamaRouter.Services.Routing.TargetLocation.Cloud, MaxVramMB = 16384 };

        // Cloud class wins over the (desktop-range) VRAM: resident indefinitely.
        Assert.Equal(-1, cloud.GetEffectivePreloadKeepAliveSeconds(options));
    }

    [Fact]
    public void GetEffectivePreloadKeepAliveSeconds_ServerVramWithoutOverride_ResolvesToIndefinite()
    {
        var options = new OllamaRouterOptions();
        var atThreshold = new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.10:11434", MaxVramMB = OllamaRouter.Services.ContextSizing.DynamicContextSizingEngine.ServerClassVramThresholdMB };
        var aboveThreshold = new OllamaTargetOptions { Name = "Server2", Url = "http://192.168.1.11:11434", MaxVramMB = 65536 };

        Assert.Equal(-1, atThreshold.GetEffectivePreloadKeepAliveSeconds(options));
        Assert.Equal(-1, aboveThreshold.GetEffectivePreloadKeepAliveSeconds(options));
    }

    [Fact]
    public void GetEffectivePreloadKeepAliveSeconds_DesktopVramWithoutOverride_ResolvesTo15Minutes()
    {
        var options = new OllamaRouterOptions();
        var desktop = new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435", MaxVramMB = 16384 };
        var laptop = new OllamaTargetOptions { Name = "Laptop", Url = "http://192.168.1.20:11435", MaxVramMB = 8192 };

        Assert.Equal(900, desktop.GetEffectivePreloadKeepAliveSeconds(options));
        Assert.Equal(900, laptop.GetEffectivePreloadKeepAliveSeconds(options));
    }

    [Fact]
    public void GetEffectivePreloadKeepAliveSeconds_UnknownVramNonCloud_FallsBackToRouterDefault()
    {
        var options = new OllamaRouterOptions { PreloadDefaultKeepAliveSeconds = 120 };
        var unknown = new OllamaTargetOptions { Name = "Mystery", Url = "http://192.168.1.30:11434" };

        Assert.Equal(120, unknown.GetEffectivePreloadKeepAliveSeconds(options));
    }

    [Fact]
    public void Normalize_PerNodeKeepAliveSeconds_InvalidValuesResetToNull_ValidValuesKept()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "A", Url = "http://127.0.0.1:11435", KeepAliveSeconds = 0 },
                new OllamaTargetOptions { Name = "B", Url = "http://127.0.0.1:11435", KeepAliveSeconds = -5 },
                new OllamaTargetOptions { Name = "C", Url = "http://127.0.0.1:11435", KeepAliveSeconds = -1 },
                new OllamaTargetOptions { Name = "D", Url = "http://127.0.0.1:11435", KeepAliveSeconds = 300 },
                new OllamaTargetOptions { Name = "E", Url = "http://127.0.0.1:11435", KeepAliveSeconds = null }
            ]
        };

        options.Normalize();

        Assert.Null(options.Targets[0]!.KeepAliveSeconds); // 0 = not a preload intent
        Assert.Null(options.Targets[1]!.KeepAliveSeconds); // <= -2 is invalid
        Assert.Equal(-1, options.Targets[2]!.KeepAliveSeconds);
        Assert.Equal(300, options.Targets[3]!.KeepAliveSeconds);
        Assert.Null(options.Targets[4]!.KeepAliveSeconds);
    }

    [Fact]
    public void Normalize_RouterDefaultKeepAliveSeconds_InvalidValuesClampTo900_LegitValuesKept()
    {
        var bad = new OllamaRouterOptions { PreloadDefaultKeepAliveSeconds = -7 };
        bad.Normalize();
        Assert.Equal(900, bad.PreloadDefaultKeepAliveSeconds);

        var zero = new OllamaRouterOptions { PreloadDefaultKeepAliveSeconds = 0 };
        zero.Normalize();
        Assert.Equal(0, zero.PreloadDefaultKeepAliveSeconds); // 0 = no extra retention (legitimate router policy)

        var indefinite = new OllamaRouterOptions { PreloadDefaultKeepAliveSeconds = -1 };
        indefinite.Normalize();
        Assert.Equal(-1, indefinite.PreloadDefaultKeepAliveSeconds);
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> warnings;

        public CapturingLogger(List<string> warnings) => this.warnings = warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}
