using System.Text.Json;
using System.Text.Json.Nodes;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Serialization;

namespace OllamaRouter.Tests.Serialization;

/// <summary>
/// Task #45 (residency visibility): the <see cref="ResidentModelsJsonConverter"/> must keep
/// <c>residentModels</c> ALWAYS an array in the wire contract (null → [], never omitted, never
/// null in the output) on the source-generated serialization path (both WRITE and READ — the
/// read path is the trim/NativeAOT-sensitive one), and a JSON null must read back as null.
/// </summary>
public class ResidentModelsJsonConverterTests
{
    private static RouterTargetItem Target(string name, IReadOnlyList<string>? residentModels, RouterTargetPreload? preload, int? activeKeepAliveSeconds)
    {
        var activeModel = residentModels is { Count: > 0 } list ? list[0] : null;
        return new RouterTargetItem(
            Name: name,
            Url: "http://127.0.0.1:11435",
            Enabled: true,
            Liveness: "Online",
            LatencyMs: 4,
            Location: "remote",
            Hardware: new RouterTargetHardware("NVIDIA", ["RTX"], 98304, 4, null),
            Execution: new RouterTargetExecution(0, activeModel, "baseline", 0, residentModels, activeKeepAliveSeconds),
            Preload: preload);
    }

    [Fact]
    public void ResidentModels_SerializedViaSourceGenContext_IsAlwaysAnArray_AndRoundTrips()
    {
        var response = new RouterFleetResponse(
        [
            Target("Desktop", ["llama3:8b", "qwen:2b"], new RouterTargetPreload(900, "class"), -1),
            Target("Server", null, new RouterTargetPreload(-1, "class"), null), // null → must serialize as []
            Target("Cloud", [], null, null) // Cloud: no policy, no observed keep-alive
        ]);

        var json = JsonSerializer.Serialize(response, OllamaRouterJsonSerializerContext.Default.RouterFleetResponse);
        var node = JsonNode.Parse(json)!.AsObject();

        // WRITE: null and empty both serialize as [] — never null, never omitted.
        var targets = node["targets"]!.AsArray();
        Assert.Equal(["llama3:8b", "qwen:2b"], targets[0]!["execution"]!["residentModels"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(-1, targets[0]!["execution"]!["activeKeepAliveSeconds"]!.GetValue<int>());
        Assert.Equal((900, "class"), (targets[0]!["preload"]!["keepAliveSeconds"]!.GetValue<int>(), targets[0]!["preload"]!["keepAliveSource"]!.GetValue<string>()));
        Assert.Null(targets[1]!["execution"]!["activeKeepAliveSeconds"]); // Server: observed omitted
        Assert.NotNull(targets[1]!["execution"]!["residentModels"]); // null DTO value → [] on the wire
        Assert.Empty(targets[1]!["execution"]!["residentModels"]!.AsArray());
        Assert.NotNull(targets[2]!["execution"]!["residentModels"]); // Cloud: ALWAYS present
        Assert.Empty(targets[2]!["execution"]!["residentModels"]!.AsArray());
        Assert.Null(targets[2]!["preload"]); // Cloud: policy omitted

        // READ (source-gen): the same context deserializes the document back (trim/AOT-safe path).
        var roundTripped = JsonSerializer.Deserialize(json, OllamaRouterJsonSerializerContext.Default.RouterFleetResponse)!;
        Assert.Equal(3, roundTripped.Targets.Count);
        Assert.Equal(["llama3:8b", "qwen:2b"], roundTripped.Targets[0].Execution.ResidentModels);
        var desktopPreload = roundTripped.Targets[0].Preload!;
        Assert.Equal((900, "class"), (desktopPreload.KeepAliveSeconds, desktopPreload.KeepAliveSource));
        Assert.Equal(-1, roundTripped.Targets[0].Execution.ActiveKeepAliveSeconds);
        Assert.NotNull(roundTripped.Targets[1].Execution.ResidentModels); // [] after round-trip, not null
        Assert.Empty(roundTripped.Targets[1].Execution.ResidentModels!);
        Assert.Null(roundTripped.Targets[1].Execution.ActiveKeepAliveSeconds);
        Assert.NotNull(roundTripped.Targets[2].Execution.ResidentModels);
        Assert.Empty(roundTripped.Targets[2].Execution.ResidentModels!);
        Assert.Null(roundTripped.Targets[2].Preload);
        Assert.Null(roundTripped.Targets[2].Execution.ActiveKeepAliveSeconds);
    }

    [Fact]
    public void ResidentModels_ReadPath_ViaSourceGenContext_NullReadsAsNull_InvalidShapesThrow()
    {
        // A JSON null (legacy/defensive input) reads back as null — the converter is lenient there.
        const string nullDocument = """
            {"targets":[{"name":"Cloud","url":null,"enabled":true,"liveness":"Online","latencyMs":null,"location":"cloud",
              "hardware":{"gpuVendor":"-","devices":[],"maxVramMB":null,"maxParallel":0,"speedFactor":null},
              "execution":{"inFlightRequests":0,"activeModel":null,"contextAllocation":"baseline","residentModels":null}}]}
            """;

        var parsed = JsonSerializer.Deserialize(nullDocument, OllamaRouterJsonSerializerContext.Default.RouterFleetResponse)!;
        Assert.Null(parsed.Targets[0].Execution.ResidentModels);

        // Non-array tokens are not a legal residentModels wire value: the read path fails fast.
        var scalarDocument = nullDocument.Replace("\"residentModels\":null", "\"residentModels\":42");
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(scalarDocument, OllamaRouterJsonSerializerContext.Default.RouterFleetResponse));
    }
}
