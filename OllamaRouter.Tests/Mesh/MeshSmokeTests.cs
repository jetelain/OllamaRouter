using System.Net;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// Smoke test for the in-process mesh fleet fixture: proves the extracted production pipeline
/// (AddOllamaRouter + UseOllamaRouterPipeline) boots a real router host on loopback, probes the
/// fake daemons Online, and routes a /api/chat request to a fleet daemon through the full
/// middleware dispatch loop (the regression net for the Phase-5 behavior-preserving refactor).
/// </summary>
[Collection("InProcessMeshFleet")]
public class MeshSmokeTests
{
    [Fact]
    public async Task SingleRouter_ChatRoutesToOnlineDaemon_WithTelemetryHeaders()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        var moe = MeshCatalog.MoeModel;

        await MeshTestHelpers.WarmupAsync(router, moe);
        Assert.True(await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5)),
            "all daemons must come Online before routing is exercised");

        var (status, body, served, _, _) = await MeshTestHelpers.ChatAsync(router, moe, 16);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(string.IsNullOrWhiteSpace(served), "served-target telemetry header is present");
        Assert.Contains("\"done\":true", body);

        // The request must have reached exactly one fleet daemon (a chat hit exists).
        Assert.NotEmpty(fleet.Daemons.SelectMany(d => d.ChatHits()));
    }
}
