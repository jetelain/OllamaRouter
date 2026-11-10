using OllamaRouter.Options;
using OllamaRouter.Services.Preload;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Services.Preload;

/// <summary>
/// Task #45: the shared preload keep-alive resolution (PreloadKeepAlivePolicy.ResolvePreloadKeepAlive)
/// — the single source of truth for both the preload load path and the fleet/monitor
/// residency-visibility surfaces (keepAliveSeconds + keepAliveSource).
/// </summary>
public class PreloadKeepAlivePolicyTests
{
    [Fact]
    public void Resolve_ExplicitMinusOneOnDesktop_WinsVerbatim_WithExplicitSource()
    {
        var target = new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435", MaxVramMB = 16384, KeepAliveSeconds = -1 };

        var (seconds, source) = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(target, new OllamaRouterOptions());

        Assert.Equal(-1, seconds);
        Assert.Equal("explicit", source);
    }

    [Fact]
    public void Resolve_ExplicitPositive_WinsVerbatim_WithExplicitSource()
    {
        var target = new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.10:11434", MaxVramMB = 98304, KeepAliveSeconds = 300 };

        var (seconds, source) = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(target, new OllamaRouterOptions());

        Assert.Equal(300, seconds);
        Assert.Equal("explicit", source);
    }

    [Fact]
    public void Resolve_CloudClassWithoutOverride_ResolvesToIndefinite_ClassSource()
    {
        var target = new OllamaTargetOptions { Name = "Cloudish", Url = "http://192.168.1.10:11434", Location = TargetLocation.Cloud };

        var (seconds, source) = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(target, new OllamaRouterOptions());

        Assert.Equal(-1, seconds);
        Assert.Equal("class", source);
    }

    [Theory]
    [InlineData(32768)] // exactly at the server threshold
    [InlineData(98304)]
    public void Resolve_ServerVramWithoutOverride_ResolvesToIndefinite_ClassSource(int vramMB)
    {
        var target = new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.10:11434", MaxVramMB = vramMB };

        var (seconds, source) = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(target, new OllamaRouterOptions());

        Assert.Equal(-1, seconds);
        Assert.Equal("class", source);
    }

    [Theory]
    [InlineData(16384)]
    [InlineData(8192)]
    [InlineData(1)]
    public void Resolve_DesktopVramWithoutOverride_ResolvesTo900_ClassSource(int vramMB)
    {
        var target = new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435", MaxVramMB = vramMB };

        var (seconds, source) = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(target, new OllamaRouterOptions());

        Assert.Equal(900, seconds);
        Assert.Equal("class", source);
    }

    [Fact]
    public void Resolve_UnknownVramNonCloud_FallsBackToRouterDefault_DefaultSource()
    {
        var options = new OllamaRouterOptions { PreloadDefaultKeepAliveSeconds = 120 };
        var target = new OllamaTargetOptions { Name = "Mystery", Url = "http://192.168.1.200:11434" }; // remote, no VRAM anywhere

        var (seconds, source) = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(target, options);

        Assert.Equal(120, seconds);
        Assert.Equal("default", source);
    }

    [Fact]
    public void Resolve_RouterDefaultMinusOne_SerializesAsMinusOne_DefaultSource()
    {
        var options = new OllamaRouterOptions { PreloadDefaultKeepAliveSeconds = -1 };
        var target = new OllamaTargetOptions { Name = "Mystery", Url = "http://192.168.1.200:11434" };

        var (seconds, source) = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(target, options);

        Assert.Equal(-1, seconds);
        Assert.Equal("default", source);
    }

    [Theory]
    [InlineData(0)] // 0 = "no extra retention" is NOT a valid per-node preload intent
    [InlineData(-5)] // <= -2 is invalid
    public void Resolve_InvalidExplicitValues_FallThroughToClassConvention(int invalidValue)
    {
        // The values Normalize() resets to null (0, <= -2) must be treated as UNSET here too,
        // so a raw (unnormalized) options object and a normalized one resolve identically.
        var options = new OllamaRouterOptions();
        options.Targets.Add(new OllamaTargetOptions { Name = "A", Url = "http://127.0.0.1:11435", MaxVramMB = 16384, KeepAliveSeconds = invalidValue });
        options.Normalize();
        var normalizedTarget = options.Targets[0]!;
        Assert.Null(normalizedTarget.KeepAliveSeconds); // Normalize reset it

        var (seconds, source) = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(normalizedTarget, options);

        Assert.Equal(900, seconds); // desktop class convention
        Assert.Equal("class", source);

        // And a raw (un-normalized) target with the same invalid value resolves the SAME way
        // (fail-soft: invalid explicit values never leak into the wire).
        var rawTarget = new OllamaTargetOptions { Name = "A", Url = "http://127.0.0.1:11435", MaxVramMB = 16384, KeepAliveSeconds = invalidValue };
        var (rawSeconds, rawSource) = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(rawTarget, options);
        Assert.Equal((seconds, source), (rawSeconds, rawSource));
    }

    [Fact]
    public void Resolve_SourceConstants_MatchWireVocabulary()
    {
        Assert.Equal("explicit", PreloadKeepAlivePolicy.SourceExplicit);
        Assert.Equal("class", PreloadKeepAlivePolicy.SourceClass);
        Assert.Equal("default", PreloadKeepAlivePolicy.SourceDefault);
    }

    [Fact]
    public void GetEffectivePreloadKeepAliveSeconds_DelegatesToResolution_NeverDrifts()
    {
        // The legacy int-only read site MUST equal the resolution's Seconds for every input
        // (it delegates to it, so the two can never drift).
        var options = new OllamaRouterOptions { PreloadDefaultKeepAliveSeconds = 450 };
        var targets = new[]
        {
            new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435", MaxVramMB = 16384 },
            new OllamaTargetOptions { Name = "DesktopPinned", Url = "http://127.0.0.1:11435", MaxVramMB = 16384, KeepAliveSeconds = -1 },
            new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.10:11434", MaxVramMB = 98304 },
            new OllamaTargetOptions { Name = "Cloudish", Url = "http://192.168.1.10:11434", Location = TargetLocation.Cloud },
            new OllamaTargetOptions { Name = "Mystery", Url = "http://192.168.1.200:11434" }
        };

        foreach (var target in targets)
        {
            Assert.Equal(
                PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(target, options).Seconds,
                target.GetEffectivePreloadKeepAliveSeconds(options));
        }
    }
}
