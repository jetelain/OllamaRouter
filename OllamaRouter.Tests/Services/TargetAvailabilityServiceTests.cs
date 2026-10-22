using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class TargetAvailabilityServiceTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string stateFilePath;

    public TargetAvailabilityServiceTests()
    {
        tempDirectory = Directory.CreateTempSubdirectory("OllamaRouterTests").FullName;
        stateFilePath = Path.Combine(tempDirectory, "targets.json");
    }

    public void Dispose()
    {
        Directory.Delete(tempDirectory, recursive: true);
    }

    private static IReadOnlyList<RoutingTarget> LegacyTargets() =>
        [RoutingTarget.Local, RoutingTarget.Remote, RoutingTarget.Cloud];

    private TargetAvailabilityService CreateSut(IReadOnlyList<RoutingTarget>? targets = null)
    {
        return new TargetAvailabilityService(stateFilePath, targets ?? LegacyTargets(), NullLogger<TargetAvailabilityService>.Instance);
    }

    private static Dictionary<string, bool> Flags(params (string Name, bool Value)[] entries) =>
        entries.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void MissingStateFile_AllTargetsEnabledByDefault()
    {
        var sut = CreateSut();

        Assert.True(sut.IsEnabled(RoutingTarget.Local));
        Assert.True(sut.IsEnabled(RoutingTarget.Remote));
        Assert.True(sut.IsEnabled(RoutingTarget.Cloud));
    }

    [Fact]
    public void NTargetFleet_AllTargetsEnabledByDefault()
    {
        var sut = CreateSut([new("desktop"), new("laptop"), new("server1"), RoutingTarget.Cloud]);

        Assert.True(sut.IsEnabled(new RoutingTarget("desktop")));
        Assert.True(sut.IsEnabled(new RoutingTarget("laptop")));
        Assert.True(sut.IsEnabled(new RoutingTarget("server1")));
        Assert.True(sut.IsEnabled(RoutingTarget.Cloud));
    }

    [Fact]
    public void Update_AppliesInMemoryImmediately()
    {
        var sut = CreateSut();

        sut.Update(Flags(("Local", false), ("Remote", true), ("Cloud", false)));

        Assert.False(sut.IsEnabled(RoutingTarget.Local));
        Assert.True(sut.IsEnabled(RoutingTarget.Remote));
        Assert.False(sut.IsEnabled(RoutingTarget.Cloud));

        var snapshot = sut.GetSnapshot();
        Assert.False(snapshot.Flags[RoutingTarget.Local]);
        Assert.True(snapshot.Flags[RoutingTarget.Remote]);
        Assert.False(snapshot.Flags[RoutingTarget.Cloud]);
    }

    [Fact]
    public void Update_MatchesKeysCaseInsensitively()
    {
        var sut = CreateSut();

        sut.Update(Flags(("local", false), ("CLOUD", false)));

        Assert.False(sut.IsEnabled(RoutingTarget.Local));
        Assert.True(sut.IsEnabled(RoutingTarget.Remote));
        Assert.False(sut.IsEnabled(RoutingTarget.Cloud));
    }

    [Fact]
    public void Update_MissingKeysKeepCurrentValue()
    {
        var sut = CreateSut();

        sut.Update(Flags(("Local", false), ("Remote", false)));

        // Not part of the payload: keeps its previous (default) value.
        Assert.True(sut.IsEnabled(RoutingTarget.Cloud));
        Assert.False(sut.IsEnabled(RoutingTarget.Local));
        Assert.False(sut.IsEnabled(RoutingTarget.Remote));
    }

    [Fact]
    public void Update_UnknownKeysAreIgnored()
    {
        var sut = CreateSut();

        sut.Update(Flags(("desktop", false), ("Local", true)));

        // "desktop" is not a known target of this (legacy) fleet: it is ignored and an
        // unknown target always reads back as enabled (default).
        Assert.True(sut.IsEnabled(new RoutingTarget("desktop")));
        Assert.True(sut.IsEnabled(RoutingTarget.Local));
        Assert.True(sut.IsEnabled(RoutingTarget.Remote));
    }

    [Fact]
    public void Update_PersistsFlatNameToBoolJsonStateFile()
    {
        var sut = CreateSut();

        sut.Update(Flags(("Local", false), ("Remote", true), ("Cloud", false)));

        Assert.True(File.Exists(stateFilePath));
        var persisted = File.ReadAllText(stateFilePath);
        Assert.Contains("\"Local\": false", persisted);
        Assert.Contains("\"Remote\": true", persisted);
        Assert.Contains("\"Cloud\": false", persisted);
    }

    [Fact]
    public void Update_ArbitraryFleetNames_PersistsAndReloadsCaseInsensitively()
    {
        var fleet = new[] { new RoutingTarget("desktop"), new RoutingTarget("laptop"), new RoutingTarget("server1"), RoutingTarget.Cloud };
        var sut = CreateSut(fleet);

        sut.Update(Flags(("laptop", false)));

        var persisted = File.ReadAllText(stateFilePath);
        Assert.Contains("\"desktop\": true", persisted);
        Assert.Contains("\"laptop\": false", persisted);
        Assert.Contains("\"server1\": true", persisted);
        Assert.Contains("\"Cloud\": true", persisted);

        // Reload into a new instance: the same state is visible case-insensitively.
        var reloaded = CreateSut(fleet.Select(t => new RoutingTarget(t.Value.ToLowerInvariant())).ToList());
        Assert.True(reloaded.IsEnabled(new RoutingTarget("desktop")));
        Assert.False(reloaded.IsEnabled(new RoutingTarget("laptop")));
        Assert.True(reloaded.IsEnabled(new RoutingTarget("server1")));
        Assert.True(reloaded.IsEnabled(RoutingTarget.Cloud));
    }

    [Fact]
    public void UnknownPersistedKeysAreIgnoredOnLoad()
    {
        var legacyFleet = LegacyTargets();
        File.WriteAllText(stateFilePath, "{ \"Local\": false, \"desktop\": false, \"Remote\": true, \"Cloud\": true }");

        var sut = CreateSut(legacyFleet);

        Assert.False(sut.IsEnabled(RoutingTarget.Local));
        Assert.True(sut.IsEnabled(RoutingTarget.Remote));
        Assert.True(sut.IsEnabled(RoutingTarget.Cloud));
    }

    [Fact]
    public void CorruptedStateFile_FallsBackToAllTargetsEnabled()
    {
        File.WriteAllText(stateFilePath, "{ this is not valid json");

        var sut = CreateSut();

        Assert.True(sut.IsEnabled(RoutingTarget.Local));
        Assert.True(sut.IsEnabled(RoutingTarget.Remote));
        Assert.True(sut.IsEnabled(RoutingTarget.Cloud));
    }

    [Fact]
    public void StateIsRestoredAcrossInstances()
    {
        var first = CreateSut();
        first.Update(Flags(("Local", false), ("Remote", false), ("Cloud", true)));

        var second = CreateSut();

        var snapshot = second.GetSnapshot();
        Assert.False(snapshot.Flags[RoutingTarget.Local]);
        Assert.False(snapshot.Flags[RoutingTarget.Remote]);
        Assert.True(snapshot.Flags[RoutingTarget.Cloud]);
    }
}
