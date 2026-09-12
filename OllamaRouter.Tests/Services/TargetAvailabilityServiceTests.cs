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

    private TargetAvailabilityService CreateSut()
    {
        return new TargetAvailabilityService(stateFilePath, NullLogger<TargetAvailabilityService>.Instance);
    }

    [Fact]
    public void MissingStateFile_AllTargetsEnabledByDefault()
    {
        var sut = CreateSut();

        Assert.True(sut.IsEnabled(RoutingTarget.Local));
        Assert.True(sut.IsEnabled(RoutingTarget.Remote));
        Assert.True(sut.IsEnabled(RoutingTarget.Cloud));
    }

    [Fact]
    public void Update_AppliesInMemoryImmediately()
    {
        var sut = CreateSut();

        sut.Update(local: false, remote: true, cloud: false);

        Assert.False(sut.IsEnabled(RoutingTarget.Local));
        Assert.True(sut.IsEnabled(RoutingTarget.Remote));
        Assert.False(sut.IsEnabled(RoutingTarget.Cloud));

        var snapshot = sut.GetSnapshot();
        Assert.Equal(new TargetAvailabilitySnapshot(false, true, false), snapshot);
    }

    [Fact]
    public void Update_PersistsToJsonStateFile()
    {
        var sut = CreateSut();

        sut.Update(local: false, remote: true, cloud: false);

        Assert.True(File.Exists(stateFilePath));
        var persisted = File.ReadAllText(stateFilePath);
        Assert.Contains("\"Local\": false", persisted);
        Assert.Contains("\"Remote\": true", persisted);
        Assert.Contains("\"Cloud\": false", persisted);
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
        first.Update(local: false, remote: false, cloud: true);

        var second = CreateSut();

        Assert.Equal(new TargetAvailabilitySnapshot(false, false, true), second.GetSnapshot());
    }
}
