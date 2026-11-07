using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Fleet;

public sealed record RouterFleetResponse(
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterTargetItem> Targets);

public sealed record RouterTargetItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("liveness")] string Liveness,
    [property: JsonPropertyName("latencyMs")] long? LatencyMs,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("hardware")] RouterTargetHardware Hardware,
    [property: JsonPropertyName("execution")] RouterTargetExecution Execution);

public sealed record RouterTargetHardware(
    [property: JsonPropertyName("gpuVendor")] string GpuVendor,
    [property: JsonPropertyName("devices")] IReadOnlyList<string> Devices,
    [property: JsonPropertyName("maxVramMB")] int? MaxVramMB,
    [property: JsonPropertyName("maxParallel")] int MaxParallel,
    [property: JsonPropertyName("speedFactor")] double? SpeedFactor,
    [property: JsonPropertyName("isUnifiedMemory")] bool IsUnifiedMemory = false,
    [property: JsonPropertyName("systemRamMB")] long? SystemRamMB = null,
    [property: JsonPropertyName("memoryType")] string? MemoryType = null,
    [property: JsonPropertyName("bandwidthGBs")] double? BandwidthGBs = null,
    [property: JsonPropertyName("gpuName")] string? GpuName = null);

public sealed record RouterTargetExecution(
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("activeModel")] string? ActiveModel,
    [property: JsonPropertyName("contextAllocation")] string ContextAllocation);
