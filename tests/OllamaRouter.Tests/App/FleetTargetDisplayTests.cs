using OllamaRouter.App.ViewModels;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="FleetTargetDisplay"/> unit tests (task #153, maintainer-confirmed decisions
/// 2026-11-21, CONFIRMED Q1/Q2 variant A): the human-readable memory formatting (÷1024,
/// at most 1 decimal when needed), the keep-alive label matrix, the one-line-per-topic
/// hardware summary (OS / memory / GPU with unknown-sub-value hiding), and the
/// VRAM-override predicate (config set AND ≠ profile TotalFastMemoryMB). Pure statics —
/// no Avalonia, no view model.
/// </summary>
public sealed class FleetTargetDisplayTests
{
    // -- FormatMemoryMB (decision 7, CONFIRMED Q1 A: ÷1024, 1 decimal max) ------------

    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, null)]
    [InlineData(-512L, null)]
    [InlineData(512L, "512 MB")]
    [InlineData(1023L, "1023 MB")]
    [InlineData(1024L, "1 GB")]
    [InlineData(1536L, "1.5 GB")]
    [InlineData(83968L, "82 GB")]
    [InlineData(93379L, "91.2 GB")]
    public void FormatMemoryMB_HiddenWhenZeroOrNull_ConfirmedVectors(long? mb, string? expected)
    {
        Assert.Equal(expected, FleetTargetDisplay.FormatMemoryMB(mb));
    }

    // -- FormatKeepAliveLabel (decision 5: -1 → indefinite, source suffix kept) --------

    [Theory]
    [InlineData(-1, "default", "Keep alive: indefinite (default)")]
    [InlineData(900, "class", "Keep alive: 900s (class)")]
    [InlineData(0, "explicit", "Keep alive: 0s (explicit)")]
    [InlineData(300, null, "Keep alive: 300s")]
    [InlineData(300, "", "Keep alive: 300s")]
    [InlineData(-1, null, "Keep alive: indefinite")]
    public void FormatKeepAliveLabel_Matrix(int seconds, string? source, string? expected)
    {
        Assert.Equal(expected, FleetTargetDisplay.FormatKeepAliveLabel(seconds, source));
    }

    [Fact]
    public void FormatKeepAliveLabel_NullSeconds_IsHidden()
    {
        Assert.Null(FleetTargetDisplay.FormatKeepAliveLabel(null, "default"));
        Assert.Null(FleetTargetDisplay.FormatKeepAliveLabel(null, null));
    }

    // -- BuildGpuLine (decision 3: one line, unknown sub-values hidden) ----------------

    [Fact]
    public void BuildGpuLine_NullOrEmpty_IsHidden()
    {
        Assert.Null(FleetTargetDisplay.BuildGpuLine(null!));
        Assert.Null(FleetTargetDisplay.BuildGpuLine([]));
    }

    [Fact]
    public void BuildGpuLine_AllSubValuesUnknown_RendersTheNameOnly()
    {
        // The test-session profile's lspci AMD GPU: 0 MB / "Unknown" / 0 GB/s → nothing but
        // the display name (the '0 MB / Unknown / 0 GB/s' sub-values must never render).
        var line = FleetTargetDisplay.BuildGpuLine(
        [
            new GpuDeviceProfile(
                "AMD", "Strix Halo [Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics]",
                0, "Unknown", 0, 0),
        ]);
        Assert.Equal(
            "GPU: AMD Strix Halo [Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics]",
            line);
    }

    [Fact]
    public void BuildGpuLine_KnownSubValues_RendersVramTypeAndBandwidth()
    {
        var line = FleetTargetDisplay.BuildGpuLine(
        [
            new GpuDeviceProfile("AMD", "GPU", 512, "GDDR6", 0, 341.2),
        ]);
        Assert.Equal("GPU: AMD GPU (512 MB GDDR6, 341.2 GB/s)", line);
    }

    [Fact]
    public void BuildGpuLine_VendorInsideTheModelName_IsNotDoubled()
    {
        // The FormatGpuDevice precedent: the display name is the model name verbatim when it
        // already starts with the vendor.
        var line = FleetTargetDisplay.BuildGpuLine(
        [
            new GpuDeviceProfile("NVIDIA", "NVIDIA GeForce RTX 4090", 24576, "GDDR6X", 384, 1008),
        ]);
        Assert.Equal("GPU: NVIDIA GeForce RTX 4090 (24 GB GDDR6X, 1008 GB/s)", line);
    }

    [Fact]
    public void BuildGpuLine_VramKnownButTypeUnknown_RendersTheVramWithoutTheType()
    {
        var line = FleetTargetDisplay.BuildGpuLine(
        [
            new GpuDeviceProfile("NVIDIA", "RTX 3090", 24576, "Unknown", 0, 936),
        ]);
        Assert.Equal("GPU: NVIDIA RTX 3090 (24 GB, 936 GB/s)", line);
    }

    [Fact]
    public void BuildGpuLine_BandwidthOnly_RendersTheBandwidthAlone()
    {
        var line = FleetTargetDisplay.BuildGpuLine(
        [
            new GpuDeviceProfile("AMD", "GPU", 0, "Unknown", 0, 341.2),
        ]);
        Assert.Equal("GPU: AMD GPU (341.2 GB/s)", line);
    }

    [Fact]
    public void BuildGpuLine_MultipleGpus_JoinsOnOneLine()
    {
        var line = FleetTargetDisplay.BuildGpuLine(
        [
            new GpuDeviceProfile("NVIDIA", "RTX 4090", 24576, "GDDR6X", 384, 1008),
            new GpuDeviceProfile("AMD", "GPU", 512, "GDDR6", 0, 341.2),
        ]);
        Assert.Equal(
            "GPU: NVIDIA RTX 4090 (24 GB GDDR6X, 1008 GB/s), AMD GPU (512 MB GDDR6, 341.2 GB/s)",
            line);
    }

    // -- HasVramOverride / BuildVramOverrideLabel (decision 2, CONFIRMED Q2 A) ----------

    private static NodeHardwareProfile Profile(long totalFastMemoryMB) => new(
        "Node", "linux", "x64", true, totalFastMemoryMB, SystemRamType.LpDdr5X,
        [], totalFastMemoryMB, 341.2);

    [Fact]
    public void HasVramOverride_PredicateMatrix()
    {
        Assert.False(FleetTargetDisplay.HasVramOverride(null, Profile(93379))); // unset
        Assert.False(FleetTargetDisplay.HasVramOverride(93379, Profile(93379))); // equal → hidden
        Assert.True(FleetTargetDisplay.HasVramOverride(24576, Profile(93379))); // different
        // The annotation is profile-anchored: config set + no profile → hidden.
        Assert.False(FleetTargetDisplay.HasVramOverride(24576, null));
    }

    [Theory]
    [InlineData(93379, 93379, null)]
    [InlineData(24576, 93379, "VRAM override: 24 GB")]
    [InlineData(512, 93379, "VRAM override: 512 MB")]
    public void BuildVramOverrideLabel_Matrix(int config, long profile, string? expected)
    {
        Assert.Equal(expected, FleetTargetDisplay.BuildVramOverrideLabel(config, Profile(profile)));
    }

    [Fact]
    public void BuildVramOverrideLabel_ConfigUnset_IsHidden()
    {
        Assert.Null(FleetTargetDisplay.BuildVramOverrideLabel(null, Profile(93379)));
    }

    [Fact]
    public void BuildVramOverrideLabel_ProfileAbsent_IsHiddenEvenWhenConfigIsSet()
    {
        Assert.Null(FleetTargetDisplay.BuildVramOverrideLabel(24576, null));
    }

    // -- BuildOsLine (decision 3: merged "OS: {os} {arch}") ------------------------------

    [Theory]
    [InlineData("linux", "x64", "OS: linux x64")]
    [InlineData("linux", null, "OS: linux")]
    [InlineData(null, "x64", "OS: x64")]
    [InlineData("linux", "  ", "OS: linux")]
    [InlineData(null, null, null)]
    [InlineData("  ", "  ", null)]
    public void BuildOsLine_MergedWithSegmentHiding(string? os, string? arch, string? expected)
    {
        Assert.Equal(expected, FleetTargetDisplay.BuildOsLine(os, arch));
    }

    // -- BuildMemoryLine (decision 3: "RAM: … · VRAM: … · VRAM Speed: …") ----------------

    [Fact]
    public void BuildMemoryLine_TestSessionProfile_CanonicalLine()
    {
        Assert.Equal(
            "RAM: 91.2 GB · VRAM: 91.2 GB · VRAM Speed: 341.2 GB/s",
            FleetTargetDisplay.BuildMemoryLine(93379, 93379, 341.2));
    }

    [Fact]
    public void BuildMemoryLine_EachSegmentHiddenIndividuallyWhenZero()
    {
        Assert.Equal("RAM: 8 GB", FleetTargetDisplay.BuildMemoryLine(8192, 0, 0));
        Assert.Equal("VRAM: 24 GB", FleetTargetDisplay.BuildMemoryLine(0, 24576, 0));
        Assert.Equal("VRAM Speed: 341.2 GB/s", FleetTargetDisplay.BuildMemoryLine(0, 0, 341.2));
        Assert.Equal(
            "VRAM: 24 GB · VRAM Speed: 936 GB/s",
            FleetTargetDisplay.BuildMemoryLine(null, 24576, 936));
    }

    [Fact]
    public void BuildMemoryLine_AllSegmentsZero_IsHidden()
    {
        Assert.Null(FleetTargetDisplay.BuildMemoryLine(0, 0, 0));
        Assert.Null(FleetTargetDisplay.BuildMemoryLine(null, null, null));
    }
}
