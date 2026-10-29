using System.Text.Json.Serialization;
using OllamaRouter.Endpoints;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Services;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, ActivityStatisticsTotals>))]
[JsonSerializable(typeof(List<ActivityStatisticsDate>))]
[JsonSerializable(typeof(OllamaErrorResponse))]
[JsonSerializable(typeof(AllOfflineDiagnostics))]
[JsonSerializable(typeof(OfflineTargetInfo))]
[JsonSerializable(typeof(MonitorStateResponse))]
[JsonSerializable(typeof(MonitorFleetTargetItem))]
[JsonSerializable(typeof(MonitorCostStatus))]
[JsonSerializable(typeof(TargetsUpdateRequest))]
[JsonSerializable(typeof(TargetsUpdateResponse))]
[JsonSerializable(typeof(ReclaimVramResponse))]
[JsonSerializable(typeof(MonitorOverheadStatus))]
[JsonSerializable(typeof(MonitorOverheadRecommendation))]
[JsonSerializable(typeof(RouterInfoResponse))]
[JsonSerializable(typeof(RouterFleetSummary))]
[JsonSerializable(typeof(RouterFeatures))]
[JsonSerializable(typeof(RouterFleetResponse))]
[JsonSerializable(typeof(RouterTargetItem))]
[JsonSerializable(typeof(RouterTargetHardware))]
[JsonSerializable(typeof(RouterTargetExecution))]
[JsonSerializable(typeof(RouterModelSummary))]
[JsonSerializable(typeof(RouterModelsResponse))]
[JsonSerializable(typeof(RouterModelDetailResponse))]
[JsonSerializable(typeof(RouterModelTargetCompatibility))]
[JsonSerializable(typeof(RouterContextEvaluateRequest))]
[JsonSerializable(typeof(RouterContextEvaluateResponse))]
[JsonSerializable(typeof(RouterVramReclaimRequest))]
[JsonSerializable(typeof(RouterVramReclaimTargetResult))]
[JsonSerializable(typeof(Dictionary<string, HardwareProfileState>))]
[JsonSerializable(typeof(NodeHardwareProfile))]
[JsonSerializable(typeof(GpuDeviceProfile))]
[JsonSerializable(typeof(SystemRamType))]
[JsonSerializable(typeof(FleetHardwareNode))]
[JsonSerializable(typeof(RouterFleetHardwareResponse))]
[JsonSerializable(typeof(RouterVramReclaimResponse))]
[JsonSerializable(typeof(CandidateModel))]
[JsonSerializable(typeof(UsageRole))]
[JsonSerializable(typeof(BenchmarkContextResult))]
[JsonSerializable(typeof(BenchmarkTargetResult))]
[JsonSerializable(typeof(BenchmarkTargetState))]
[JsonSerializable(typeof(SpeedSnapshotEntry))]
[JsonSerializable(typeof(CandidateSizingEstimate))]
[JsonSerializable(typeof(MemoryFitStatus))]
[JsonSerializable(typeof(BenchmarkRunStatus))]
[JsonSerializable(typeof(Dictionary<string, BenchmarkTargetState>))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, SpeedSnapshotEntry>>))]
[JsonSerializable(typeof(RouterBenchmarkRequest))]
[JsonSerializable(typeof(RouterBenchmarkRunResponse))]
[JsonSerializable(typeof(RouterBenchmarkResultsResponse))]
[JsonSerializable(typeof(RouterBenchmarkClearedResponse))]
public partial class OllamaRouterJsonSerializerContext : JsonSerializerContext
{
}
