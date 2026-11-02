using Microsoft.Extensions.Configuration;
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

    // ------------------------------------------------------------------
    //  Preload keep-alive resolver + normalization (task #44)
    // ------------------------------------------------------------------

    [Fact]
    public void GetEffectivePreloadKeepAliveSeconds_ExplicitOverride_WinsVerbatimIncludingMinusOneOnDesktop()
    {
        var options = new OllamaRouterOptions();
        var desktop = new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435", MaxVramMB = 16384, KeepAliveSeconds = -1 };
        var cloud = new OllamaTargetOptions { Name = "Cloud", Location = OllamaRouter.Services.TargetLocation.Cloud, KeepAliveSeconds = 300 };

        // Explicit values are honored verbatim (-1 on a desktop-class node stays -1, Open Q5).
        Assert.Equal(-1, desktop.GetEffectivePreloadKeepAliveSeconds(options));
        Assert.Equal(300, cloud.GetEffectivePreloadKeepAliveSeconds(options));
    }

    [Fact]
    public void GetEffectivePreloadKeepAliveSeconds_CloudClassWithoutOverride_ResolvesToIndefinite()
    {
        var options = new OllamaRouterOptions();
        var cloud = new OllamaTargetOptions { Name = "Cloud", Location = OllamaRouter.Services.TargetLocation.Cloud, MaxVramMB = 16384 };

        // Cloud class wins over the (desktop-range) VRAM: resident indefinitely.
        Assert.Equal(-1, cloud.GetEffectivePreloadKeepAliveSeconds(options));
    }

    [Fact]
    public void GetEffectivePreloadKeepAliveSeconds_ServerVramWithoutOverride_ResolvesToIndefinite()
    {
        var options = new OllamaRouterOptions();
        var atThreshold = new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.10:11434", MaxVramMB = OllamaRouter.Services.DynamicContextSizingEngine.ServerClassVramThresholdMB };
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
