using System.Text.Json;
using System.Text.Json.Serialization;
using OllamaRouter.Converters.Dtos;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Context;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Endpoints.RouterApi.Preload;
using OllamaRouter.Endpoints.RouterApi.Recommendations;
using OllamaRouter.Endpoints.RouterApi.Sessions;
using OllamaRouter.Endpoints.RouterApi.SessionAffinity;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Endpoints.RouterApi.Vram;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Models;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Recommendations;

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
[JsonSerializable(typeof(RouterShutdownResponse))]
[JsonSerializable(typeof(RouterFleetResponse))]
[JsonSerializable(typeof(RouterTargetItem))]
[JsonSerializable(typeof(RouterTargetHardware))]
[JsonSerializable(typeof(RouterTargetExecution))]
[JsonSerializable(typeof(RouterTargetPreload))]
[JsonSerializable(typeof(RouterModelSummary))]
[JsonSerializable(typeof(RouterModelsResponse))]
[JsonSerializable(typeof(RouterModelDetailResponse))]
[JsonSerializable(typeof(RouterModelTargetCompatibility))]
// Model pull (task #125): the POST /api/router/models/pull wire DTOs (shared client SDK)
// + the daemon /api/pull, /api/create request and NDJSON-line wire shapes.
[JsonSerializable(typeof(OllamaRouterModelPullRequest))]
[JsonSerializable(typeof(OllamaRouterModelPullEvent))]
[JsonSerializable(typeof(OllamaDaemonPullRequest))]
[JsonSerializable(typeof(OllamaDaemonCreateRequest))]
[JsonSerializable(typeof(OllamaDaemonCreateParameters))]
[JsonSerializable(typeof(OllamaDaemonLineEvent))]
[JsonSerializable(typeof(RouterCartographyResponse))]
[JsonSerializable(typeof(CartographyNodeRow))]
[JsonSerializable(typeof(CartographyNodeHardware))]
[JsonSerializable(typeof(CartographyNodeLoadedModel))]
[JsonSerializable(typeof(CartographyModelRow))]
[JsonSerializable(typeof(CartographyDeploymentRow))]
[JsonSerializable(typeof(CartographyLoadedModelRow))]
[JsonSerializable(typeof(RouterContextEvaluateRequest))]
[JsonSerializable(typeof(RouterContextEvaluateResponse))]
[JsonSerializable(typeof(RouterVramReclaimRequest))]
[JsonSerializable(typeof(RouterModelPreloadRequest))]
[JsonSerializable(typeof(RouterModelPreloadResponse))]
[JsonSerializable(typeof(RouterModelPreloadCandidate))]
// Model uninstall (task #126): POST /api/router/models/uninstall request/response DTOs
// (shared wire contract with the client SDK, camelCase).
[JsonSerializable(typeof(RouterModelUninstallRequest))]
[JsonSerializable(typeof(RouterModelUninstallResponse))]
[JsonSerializable(typeof(RouterModelUninstallTargetResult))]
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
// Native role recommendations + node context ceilings DTOs (task #66): the
// <state>/recommended.json round-trip shape + the generate/apply response records.
[JsonSerializable(typeof(NodeRoleProfile))]
[JsonSerializable(typeof(RoleRecommendation))]
[JsonSerializable(typeof(ModelNodeProfile))]
[JsonSerializable(typeof(FleetRecommendations))]
[JsonSerializable(typeof(RouterRecommendationsGeneratedResponse))]
[JsonSerializable(typeof(RouterRecommendationsAppliedTarget))]
[JsonSerializable(typeof(RouterRecommendationsAppliedResponse))]
// Dynamic fleet configuration / Setup API DTOs (task #19).
[JsonSerializable(typeof(RouterConfigResponse))]
[JsonSerializable(typeof(RouterTargetConfigDto))]
[JsonSerializable(typeof(RouterCloudConfigDto))]
[JsonSerializable(typeof(RouterDynamicContextConfigDto))]
[JsonSerializable(typeof(RouterRecordingConfigDto))]
[JsonSerializable(typeof(RouterResilienceConfigDto))]
[JsonSerializable(typeof(RouterPricingConfigDto))]
[JsonSerializable(typeof(UpdateRouterConfigRequest))]
[JsonSerializable(typeof(UpdateCloudConfigRequest))]
[JsonSerializable(typeof(UpdateRouterConfigResponse))]
[JsonSerializable(typeof(UpdateTargetRequest))]
[JsonSerializable(typeof(UpdateTargetResponse))]
[JsonSerializable(typeof(AddTargetRequest))]
[JsonSerializable(typeof(AddTargetResponse))]
[JsonSerializable(typeof(TargetModelDto))]
[JsonSerializable(typeof(DeleteTargetResponse))]
// Failure-streak reset admin endpoints (task #95).
[JsonSerializable(typeof(ResetTargetStreakResponse))]
[JsonSerializable(typeof(ResetFleetStreaksResponse))]
[JsonSerializable(typeof(ResetFleetStreaksTarget))]
// API key management DTOs (task #50).
[JsonSerializable(typeof(ApiSecurityConfigDto))]
[JsonSerializable(typeof(ApiKeysListResponse))]
[JsonSerializable(typeof(ApiKeyInfoResponse))]
[JsonSerializable(typeof(ApiKeyCreateRequest))]
[JsonSerializable(typeof(ApiKeyCreatedResponse))]
[JsonSerializable(typeof(ApiKeyDeletedResponse))]
[JsonSerializable(typeof(ApiKeyRotatedResponse))]
// Inbound protocol converter DTOs (task #59) + raw tool-argument parse target.
[JsonSerializable(typeof(OllamaRequestDto))]
[JsonSerializable(typeof(OllamaChatMessageDto))]
[JsonSerializable(typeof(OllamaToolCallDto))]
[JsonSerializable(typeof(OllamaToolCallFunctionDto))]
[JsonSerializable(typeof(OpenAiChatRequestDto))]
[JsonSerializable(typeof(OpenAiChatMessageDto))]
[JsonSerializable(typeof(OpenAiToolCallDto))]
[JsonSerializable(typeof(OpenAiToolCallFunctionDto))]
[JsonSerializable(typeof(OpenAiResponseFormatDto))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
// Outbound OpenAI-compatible client DTOs (task #60; task #112: the request/response DTOs and
// their element targets were deleted with the hand-rolled OpenAiHttpChatClient — only the
// error envelope survives, consumed by the routing middleware's error surfacing).
 [JsonSerializable(typeof(OpenAiErrorResponseDto))]
 [JsonSerializable(typeof(OpenAiErrorDetailDto))]
 // Task #115: config/api-keys.json is now serialized by Pmad.LocalApi.Security's internal
 // source-generated LocalApiSecurityJsonContext (same WriteIndented = true output).
 // Per-session usage & savings router API DTOs (task #85).
 [JsonSerializable(typeof(RouterSessionUsageListResponse))]
 [JsonSerializable(typeof(RouterSessionUsageItem))]
 [JsonSerializable(typeof(RouterSessionUsageDetailResponse))]
 [JsonSerializable(typeof(RouterSessionUsageTargetItem))]
 [JsonSerializable(typeof(List<SessionUsageStateEntry>))] // task #105: session-usage.json root
 // Session-affinity lease release router API DTO (task #86).
 [JsonSerializable(typeof(RouterSessionAffinityReleaseResponse))]
public partial class OllamaRouterJsonSerializerContext : JsonSerializerContext
{
}
