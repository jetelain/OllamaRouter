using System.Text.Json.Serialization;
using OllamaRouter.Endpoints;
using OllamaRouter.Middleware;
using OllamaRouter.Services;

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
[JsonSerializable(typeof(RouterVramReclaimResponse))]
public partial class OllamaRouterJsonSerializerContext : JsonSerializerContext
{
}
