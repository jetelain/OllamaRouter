using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services;
using Xunit;

namespace OllamaRouter.Tests.Services;

public class CloudRelayResolverTests
{
    // ---- ResolveCloudModel precedence ----

    [Fact]
    public void ResolveCloudModel_PerModelCloudModel_WinsOverDefaultModel()
    {
        var options = new OllamaRouterOptions
        {
            CloudOverflow = new CloudOverflowOptions { DefaultModel = "default-cloud" }
        };
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };

        Assert.Equal("llama3-cloud", CloudRelayResolver.ResolveCloudModel(options, "llama3:latest"));
    }

    [Fact]
    public void ResolveCloudModel_PerModelCloudModelEmpty_FallsBackToDefaultModel()
    {
        var options = new OllamaRouterOptions
        {
            CloudOverflow = new CloudOverflowOptions { DefaultModel = "default-cloud" }
        };
        options.Models["llama3"] = new ModelThresholds { CloudModel = "" };

        Assert.Equal("default-cloud", CloudRelayResolver.ResolveCloudModel(options, "llama3"));
    }

    [Fact]
    public void ResolveCloudModel_WhenNoPerModelConfigured_UsesDefaultModel()
    {
        var options = new OllamaRouterOptions
        {
            CloudOverflow = new CloudOverflowOptions { DefaultModel = "default-cloud" }
        };

        Assert.Equal("default-cloud", CloudRelayResolver.ResolveCloudModel(options, "unknown-model"));
    }

    [Fact]
    public void ResolveCloudModel_WhenBothEmpty_ReturnsNull()
    {
        var options = new OllamaRouterOptions
        {
            CloudOverflow = new CloudOverflowOptions { DefaultModel = null }
        };
        options.Models["llama3"] = new ModelThresholds { CloudModel = null };

        Assert.Null(CloudRelayResolver.ResolveCloudModel(options, "llama3"));
    }

    [Fact]
    public void ResolveCloudModel_StripsTagBeforeMatchingPerModel()
    {
        var options = new OllamaRouterOptions();
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };

        Assert.Equal("llama3-cloud", CloudRelayResolver.ResolveCloudModel(options, "llama3:8b"));
    }

    // ---- IsCloudOverflowAvailable ----

    [Fact]
    public void IsCloudOverflowAvailable_TrueWhenToggleEnabledAndSwitchOnAndModelResolvable()
    {
        var availability = AvailabilityAllEnabled();
        var options = new OllamaRouterOptions();
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };

        Assert.True(CloudRelayResolver.IsCloudOverflowAvailable(availability, options, "llama3"));
    }

    [Fact]
    public void IsCloudOverflowAvailable_FalseWhenCloudToggleDisabled()
    {
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(RoutingTarget.Cloud)).Returns(false);
        var options = new OllamaRouterOptions();
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };

        Assert.False(CloudRelayResolver.IsCloudOverflowAvailable(availability.Object, options, "llama3"));
    }

    [Fact]
    public void IsCloudOverflowAvailable_FalseWhenStaticSwitchDisabled()
    {
        var availability = AvailabilityAllEnabled();
        var options = new OllamaRouterOptions
        {
            CloudOverflow = new CloudOverflowOptions { Enabled = false }
        };
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };

        Assert.False(CloudRelayResolver.IsCloudOverflowAvailable(availability, options, "llama3"));
    }

    [Fact]
    public void IsCloudOverflowAvailable_FalseWhenNoModelResolvable()
    {
        var availability = AvailabilityAllEnabled();
        var options = new OllamaRouterOptions();

        Assert.False(CloudRelayResolver.IsCloudOverflowAvailable(availability, options, "llama3"));
    }

    [Fact]
    public void IsCloudOverflowAvailable_DefaultModelAlone_IsSufficient()
    {
        var availability = AvailabilityAllEnabled();
        var options = new OllamaRouterOptions
        {
            CloudOverflow = new CloudOverflowOptions { DefaultModel = "default-cloud" }
        };

        Assert.True(CloudRelayResolver.IsCloudOverflowAvailable(availability, options, "any-model"));
    }

    // NOTE: the preferred-relay + fallback candidate chain itself lives in the middleware's
    // dispatch loop (AddCloudRelayCandidates) because it operates on resolved destination URLs
    // and must try the enabled-but-offline preferred relay as a probe; it is covered by the
    // relay middleware tests (DefaultPreferredRelayLocal_RelaysThroughLocalTarget and
    // PreferredRelayOffline_FallsBackToNextOnlineFleetTarget). The model/availability logic
    // shared with CloudRoutingTargetHandler is asserted here.

    // ---- helpers ----

    private static ITargetAvailabilityService AvailabilityAllEnabled()
    {
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
        return availability.Object;
    }
}
