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
            Assert.Equal("16gb", target.HardwareHint);
            Assert.Equal(16384, target.GetEffectiveMaxVramMB());
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

    [Theory]
    [InlineData("8gb", 8192)]
    [InlineData("16gb", 16384)]
    [InlineData("96gb", 98304)]
    [InlineData("8GB", 8192)]
    [InlineData(" 16Gb ", 16384)]
    public void GetEffectiveMaxVramMB_HardwareHint_NormalizesPresetsCaseInsensitively(string hint, int expected)
    {
        var target = new OllamaTargetOptions { Name = "t", Url = "http://x", HardwareHint = hint };

        Assert.Equal(expected, target.GetEffectiveMaxVramMB());
    }

    [Fact]
    public void GetEffectiveMaxVramMB_UnknownOrMissingHint_ReturnsNull()
    {
        Assert.Null(new OllamaTargetOptions().GetEffectiveMaxVramMB());
        Assert.Null(new OllamaTargetOptions { HardwareHint = "48gb" }.GetEffectiveMaxVramMB());
        Assert.Null(new OllamaTargetOptions { HardwareHint = " " }.GetEffectiveMaxVramMB());
    }

    [Fact]
    public void GetEffectiveMaxVramMB_ExplicitMaxVramMB_OverridesHardwareHint()
    {
        var target = new OllamaTargetOptions
        {
            HardwareHint = "8gb",
            MaxVramMB = 12288
        };

        Assert.Equal(12288, target.GetEffectiveMaxVramMB());
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
