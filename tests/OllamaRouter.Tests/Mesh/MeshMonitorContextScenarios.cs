using System.Net;
using System.Text.Json.Nodes;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// In-process mesh scenarios for task #73: the <c>/monitor</c> surface (web + TUI) shows the
/// ACTUAL per-process context size the Ollama daemon reports in <c>GET /api/ps</c>
/// (<c>context_length</c>), NOT the router's lease-based context allocation. End to end through
/// the full production pipeline (the 2 s coalesced execution-tracker refresh and the real
/// <c>/monitor/api</c> + <c>/monitor</c> HTML endpoints): a modern daemon reporting
/// <c>context_length: 4096</c> renders <c>4k</c> in the fleet row, a legacy daemon that omits
/// the field (and the Cloud pseudo-target) renders <c>-</c>, with no fallback to the lease.
/// </summary>
public class MeshMonitorContextScenarios
{
    private const string Model = "ctx-monitor:latest";

    private static DaemonModel ModelFixture(long? contextLength) => new()
    {
        Name = Model,
        SizeBytes = 5L * 1024 * 1024 * 1024,
        ParameterCount = 8_000_000_000L, // small dense: routable on both fixture daemons.
        ExpertCount = 0,
        Family = "llama",
        ContextLength = contextLength
    };

    private static JsonNode? FleetRow(JsonNode document, string name) =>
        document["fleet"]!.AsArray().FirstOrDefault(f => f!["name"]?.GetValue<string>() == name);

    private static async Task<bool> PollMonitorAsync(HttpClient client, Func<JsonNode, bool> predicate, TimeSpan timeout)
    {
        // Bounded poll (standard mesh settle pattern): re-fetch /monitor/api until the fleet
        // payload satisfies <paramref name="predicate"/> or the deadline passes.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/monitor/api");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var document = JsonNode.Parse(await response.Content.ReadAsStringAsync());
                if (document is not null && predicate(document))
                {
                    return true;
                }
            }

            await Task.Delay(250);
        }

        return false;
    }

    [Fact]
    public async Task MonitorApi_ShowsActualDaemonReportedContext_NotLeaseAllocation()
    {
        // The desktop daemon is a MODERN fixture: it reports the live per-process
        // context_length (4,096) in /api/ps. The server daemon is a LEGACY fixture that omits
        // the field entirely, preserving the old behavior for existing consumers.
        var desktop = new FakeOllamaDaemon("desktop", [ModelFixture(4096)], capacity: 1) { MaxVramMB = 16_384 };
        var server = new FakeOllamaDaemon("server", [ModelFixture(null)], capacity: 2) { MaxVramMB = 98_304 };
        await Task.WhenAll(desktop.StartAsync(), server.StartAsync());
        await using var desktopD = desktop;
        await using var serverD = server;
        await using var router = await MeshRouter.StartAsync("R-monitor-ctx", [desktop, server], cloudEnabled: false);

        Assert.True(await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(10)),
            "all daemons must come Online before the monitor is exercised");

        // Load the model on BOTH daemons (pinned chats) so every fleet row has a resident model.
        var desktopChat = await MeshTestHelpers.SendChatAsync(router, Model, 8, pin: "desktop");
        await MeshTestHelpers.DrainAsync(desktopChat);
        var serverChat = await MeshTestHelpers.SendChatAsync(router, Model, 8, pin: "server");
        await MeshTestHelpers.DrainAsync(serverChat);

        Assert.True(await MeshTestHelpers.WaitForLoadedAsync(router, "desktop", Model, TimeSpan.FromSeconds(5)),
            "the desktop daemon /api/ps must list the loaded model");
        Assert.True(await MeshTestHelpers.WaitForLoadedAsync(router, "server", Model, TimeSpan.FromSeconds(5)),
            "the server daemon /api/ps must list the loaded model");

        // After the tracker's next coalesced refresh (2 s TTL) the fleet rows must show the
        // daemon-reported context — the desktop's 4,096 as "4k" (NOT a lease bucket value),
        // and "-" for the legacy server row (no fallback to the lease allocation) and the
        // Cloud pseudo-target. The activeModel column stays row-consistent.
        var settled = await PollMonitorAsync(router.Client, doc =>
        {
            var desktopRow = FleetRow(doc, "desktop")!;
            var serverRow = FleetRow(doc, "server")!;
            var cloudRow = FleetRow(doc, "Cloud")!;
            return desktopRow["activeModel"]?.GetValue<string>() == Model &&
                   desktopRow["contextAllocation"]?.GetValue<string>() == "4k" &&
                   serverRow["activeModel"]?.GetValue<string>() == Model &&
                   serverRow["contextAllocation"]?.GetValue<string>() == "-" &&
                   cloudRow["contextAllocation"]?.GetValue<string>() == "-";
        }, TimeSpan.FromSeconds(10));

        Assert.True(settled,
            "the /monitor/api fleet rows must show the daemon-reported context: '4k' on the modern " +
            "desktop row, '-' on the legacy server row and the Cloud row (no lease fallback)");
    }
}
